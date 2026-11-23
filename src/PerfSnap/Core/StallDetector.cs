using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Storage;

namespace PerfSnap.Core;

/// <summary>
/// 遅延の自動検知（詳細設計 §6）。
/// 専用スレッド 2 本が、それぞれ 0.5 秒・1 秒ごとに目覚めるだけ。待ちはカーネルの待機なので CPU を使わない。
/// </summary>
public sealed class StallDetector : IDisposable
{
    private static readonly TimeSpan SystemInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AppInterval = TimeSpan.FromSeconds(1);

    private readonly Func<DetectionSettings> _settings;
    private readonly Logger _log;
    private readonly StallJudge _judge = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _systemThread;
    private Thread? _appThread;

    public event Action<StallInfo>? StallDetected;

    public StallDetector(Func<DetectionSettings> settings, Logger log)
    {
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        if (_systemThread is not null) return;
        _stop.Reset();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _systemThread = new Thread(SystemLoop) { IsBackground = true, Name = "PerfSnap.SystemStall" };
        _appThread = new Thread(AppLoop) { IsBackground = true, Name = "PerfSnap.AppHung" };
        _systemThread.Start();
        _appThread.Start();
    }

    public void Stop()
    {
        if (_systemThread is null) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _stop.Set();
        _systemThread.Join(TimeSpan.FromSeconds(3));
        _appThread?.Join(TimeSpan.FromSeconds(5));
        _systemThread = null;
        _appThread = null;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        lock (_judge) _judge.NotifyPowerChange(DateTimeOffset.Now);
    }

    private void SystemLoop()
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var t0 = sw.Elapsed;
            if (_stop.Wait(SystemInterval)) return;
            var late = sw.Elapsed - t0 - SystemInterval;

            var s = _settings().SystemStall;
            if (!s.Enabled) continue;

            long? lateMs;
            var now = DateTimeOffset.Now;
            lock (_judge) lateMs = _judge.Evaluate(late, s.ThresholdMs, now);
            if (lateMs is { } ms)
            {
                _log.Info(nameof(StallDetector), $"システムの停止を検知しました（{ms} ms の遅れ）");
                Raise(new StallInfo(StallKind.System, ms, now, ForegroundProcess: null));
            }
        }
    }

    private void AppLoop()
    {
        var hungWindow = IntPtr.Zero; // 同じ「応答なし」で何度も知らせない
        while (!_stop.Wait(AppInterval))
        {
            var s = _settings().AppHung;
            if (!s.Enabled) { hungWindow = IntPtr.Zero; continue; }

            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) continue;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == _ownPid) continue;

            var sw = Stopwatch.StartNew();
            var ok = NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_NULL, UIntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, (uint)s.TimeoutMs, out _);
            var error = Marshal.GetLastWin32Error();

            var hung = ok == IntPtr.Zero && (error == NativeMethods.ERROR_TIMEOUT || NativeMethods.IsHungAppWindow(hwnd));
            if (!hung)
            {
                if (hwnd == hungWindow) hungWindow = IntPtr.Zero;
                continue;
            }
            if (hwnd == hungWindow) continue;
            hungWindow = hwnd;

            var name = ProcessName((int)pid);
            var waited = Math.Max(sw.ElapsedMilliseconds, s.TimeoutMs);
            _log.Info(nameof(StallDetector), $"前面アプリの応答なしを検知しました（{name}）");
            Raise(new StallInfo(StallKind.AppHung, waited, DateTimeOffset.Now, name));
        }
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private void Raise(StallInfo info)
    {
        try { StallDetected?.Invoke(info); }
        catch (Exception ex) { _log.Error(nameof(StallDetector), $"検知の処理で例外：{ex}"); }
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}

using System.Diagnostics;
using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Recording;
using PerfSnap.Storage;

namespace PerfSnap.Core;

public enum CaptureState { Idle, Monitoring, TimedCapturing, Saving }

public enum NoticeLevel { Info, Warning, Error }

/// <summary>利用者への通知（詳細設計 §12）。Folder があれば、クリックでそのフォルダを開く。</summary>
public sealed record Notice(string Id, string Text, NoticeLevel Level, string? Folder = null);

/// <summary>
/// 記録の司令塔（詳細設計 §5）。状態を一元管理し、記録操作は 1 本の列で順番に実行する。
/// </summary>
public sealed class CaptureController : IDisposable
{
    private static readonly string Source = nameof(CaptureController);

    private readonly IWprRecorder _wpr;
    private readonly ICounterRecorder _counters;
    private readonly RetentionManager _retention;
    private readonly ISystemInfoCollector _sysInfo;
    private readonly IClock _clock;
    private readonly Logger _log;
    private readonly AutoSaveGate _gate = new();
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly string _toolVersion;

    private volatile CaptureState _state = CaptureState.Idle;
    private bool _monitoringWanted;
    private CancellationTokenSource? _timedCts;
    private string? _timedFolder;
    private DateTimeOffset _timedStartedAt;
    private Profile _activeProfile;

    public CaptureController(AppSettings settings, IWprRecorder wpr, ICounterRecorder counters, RetentionManager retention,
        ISystemInfoCollector sysInfo, IClock clock, Logger log)
    {
        Settings = settings;
        _wpr = wpr;
        _counters = counters;
        _retention = retention;
        _sysInfo = sysInfo;
        _clock = clock;
        _log = log;
        _activeProfile = Profiles.Find(settings.Recording.DefaultProfile) ?? Profiles.InputLatency;
        _toolVersion = typeof(CaptureController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public AppSettings Settings { get; private set; }
    public CaptureState State => _state;
    public Profile ActiveProfile => _activeProfile;
    public bool CountersRunning { get; private set; }
    public string OutputRoot => Settings.Storage.ResolvedOutputDirectory();
    /// <summary>時間指定記録の終了予定時刻。</summary>
    public DateTimeOffset? TimedCaptureEndsAt { get; private set; }

    public event Action<CaptureState>? StateChanged;
    public event Action<Notice>? Notified;
    /// <summary>手動・ホットキーの保存が始まったとき（メモ入力のため）。引数は保存フォルダ。</summary>
    public event Action<string, Trigger>? SaveStarted;

    // ---------------- 起動・終了 ----------------

    /// <summary>起動時の処理（詳細設計 §10.1 の 3〜6）。</summary>
    public async Task InitializeAsync()
    {
        await RunExclusiveAsync(async () =>
        {
            // 前回の異常終了で残った記録を止める（要件 F-32）
            await _wpr.CancelAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (Settings.Counters.Enabled) await SetCountersAsync(true).ConfigureAwait(false);
        if (Settings.General.StartMonitoringOnLaunch) await StartMonitoringAsync().ConfigureAwait(false);
    }

    /// <summary>終了時の処理（詳細設計 §10.2）。fast=true はログオフ・シャットダウン時（保存しない）。</summary>
    public async Task ShutdownAsync(bool fast)
    {
        _timedCts?.Cancel();
        if (!fast && _state == CaptureState.TimedCapturing)
        {
            await SaveNowAsync(Trigger.Timed).ConfigureAwait(false);
        }

        await RunExclusiveAsync(async () =>
        {
            if (_state is CaptureState.Monitoring or CaptureState.TimedCapturing)
            {
                await _wpr.CancelAsync().ConfigureAwait(false);
            }
            _monitoringWanted = false;
            SetState(CaptureState.Idle);
            if (Settings.General.StopCountersOnExit && CountersRunning)
            {
                await _counters.StopAsync().ConfigureAwait(false);
                CountersRunning = false;
            }
        }).ConfigureAwait(false);
    }

    // ---------------- 監視（フライトレコーダー） ----------------

    public Task StartMonitoringAsync() => RunExclusiveAsync(async () =>
    {
        if (_state != CaptureState.Idle)
        {
            if (_state == CaptureState.Monitoring) return;
            Notify("M-04", "記録中または保存中のため、今は開始できません", NoticeLevel.Warning);
            return;
        }
        _monitoringWanted = true;
        await StartFlightRecorderCoreAsync().ConfigureAwait(false);
    });

    public Task StopMonitoringAsync() => RunExclusiveAsync(async () =>
    {
        _monitoringWanted = false;
        if (_state != CaptureState.Monitoring) return;
        await _wpr.CancelAsync().ConfigureAwait(false);
        SetState(CaptureState.Idle);
        _log.Info(Source, "監視を停止しました");
    });

    public Task SetProfileAsync(string profileId) => RunExclusiveAsync(async () =>
    {
        var p = Profiles.Find(profileId);
        if (p is null || p == _activeProfile) return;
        _activeProfile = p;
        _log.Info(Source, $"プロファイルを「{p.DisplayName}」に変更しました");
        if (_state == CaptureState.Monitoring)
        {
            await _wpr.CancelAsync().ConfigureAwait(false);
            SetState(CaptureState.Idle);
            await StartFlightRecorderCoreAsync().ConfigureAwait(false);
        }
    });

    private async Task StartFlightRecorderCoreAsync()
    {
        var r = await _wpr.StartMemoryModeAsync(_activeProfile).ConfigureAwait(false);
        if (!r.Ok)
        {
            // 他のツールとの競合などで失敗した場合、残りを片付けてから 1 回だけやり直す
            await _wpr.CancelAsync().ConfigureAwait(false);
            r = await _wpr.StartMemoryModeAsync(_activeProfile).ConfigureAwait(false);
        }
        if (r.Ok)
        {
            SetState(CaptureState.Monitoring);
            _log.Info(Source, $"監視を開始しました（{_activeProfile.DisplayName}）");
        }
        else
        {
            SetState(CaptureState.Idle);
            Notify("M-06", "記録を開始できませんでした。詳しくはログを確認してください", NoticeLevel.Error);
        }
    }

    // ---------------- 保存 ----------------

    /// <summary>自動検知を受けたとき（詳細設計 §5.1、§6.3）。</summary>
    public async Task OnStallAsync(StallInfo stall)
    {
        if (_state != CaptureState.Monitoring)
        {
            _log.Info(Source, $"SKIP 自動保存（状態={_state}、{stall.Kind}、{stall.LateMs} ms）");
            return;
        }
        var decision = _gate.Check(Settings.Detection, _clock.Now);
        if (decision != AutoSaveGate.Decision.Pass)
        {
            _log.Info(Source, $"SKIP 自動保存（{decision}、{stall.Kind}、{stall.LateMs} ms）");
            return;
        }
        _gate.Record(_clock.Now);
        await SaveNowAsync(Trigger.Auto, stall).ConfigureAwait(false);
    }

    /// <summary>今すぐ保存（詳細設計 §5.2）。</summary>
    public async Task SaveNowAsync(Trigger trigger, StallInfo? stall = null)
    {
        switch (_state)
        {
            case CaptureState.Saving:
                Notify("M-04", "前の保存がまだ終わっていません", NoticeLevel.Warning);
                return;
            case CaptureState.Idle:
                Notify("M-03", "監視が止まっています。メニューから「監視」をオンにしてください", NoticeLevel.Warning);
                return;
            case CaptureState.TimedCapturing:
                _timedCts?.Cancel();
                await RunExclusiveAsync(() => FinishTimedCaptureCoreAsync()).ConfigureAwait(false);
                return;
        }

        await RunExclusiveAsync(async () =>
        {
            if (_state != CaptureState.Monitoring) return;
            await SaveFlightRecorderCoreAsync(trigger, stall).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task SaveFlightRecorderCoreAsync(Trigger trigger, StallInfo? stall)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = _clock.Now;
        SetState(CaptureState.Saving);

        await _wpr.MarkerAsync($"PerfSnap_{CaptureFolder.TriggerText(trigger)}").ConfigureAwait(false);

        if (!CheckFreeSpace())
        {
            SetState(CaptureState.Monitoring);
            return;
        }

        var folder = CaptureFolder.Create(OutputRoot, trigger, startedAt);
        var meta = NewMeta(trigger, startedAt, stall);
        meta.Result = "saving";
        CaptureFolder.WriteMeta(folder, meta);

        var delay = trigger == Trigger.Auto ? Settings.Detection.DelayBeforeSaveSeconds : 0;
        var reason = stall is null ? "" : $"（自動検知：{stall.LateMs / 1000.0:0.0} 秒の遅れ）";
        Notify("M-01", $"保存しています…{reason}", NoticeLevel.Info);
        if (trigger is Trigger.Manual or Trigger.Hotkey || Settings.Detection.ShowMemoOnAutoSave)
        {
            SaveStarted?.Invoke(folder, trigger);
        }
        if (delay > 0) await _clock.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);

        var ok = await StopToFolderAsync(folder, trigger, meta).ConfigureAwait(false);
        FinishMeta(folder, meta, ok, sw);

        // 監視を再開する
        SetState(CaptureState.Idle);
        if (_monitoringWanted) await StartFlightRecorderCoreAsync().ConfigureAwait(false);

        AfterSave(folder, ok, sw);
    }

    // ---------------- 時間指定の記録 ----------------

    public Task StartTimedCaptureAsync(int seconds) => RunExclusiveAsync(async () =>
    {
        if (_state is CaptureState.TimedCapturing or CaptureState.Saving)
        {
            Notify("M-04", "記録中または保存中のため、今は開始できません", NoticeLevel.Warning);
            return;
        }
        if (!CheckFreeSpace()) return;

        if (_state == CaptureState.Monitoring)
        {
            await _wpr.CancelAsync().ConfigureAwait(false);
            SetState(CaptureState.Idle);
        }

        var r = await _wpr.StartFileModeAsync(_activeProfile).ConfigureAwait(false);
        if (!r.Ok)
        {
            Notify("M-06", "記録を開始できませんでした。詳しくはログを確認してください", NoticeLevel.Error);
            if (_monitoringWanted) await StartFlightRecorderCoreAsync().ConfigureAwait(false);
            return;
        }

        _timedStartedAt = _clock.Now;
        _timedFolder = CaptureFolder.Create(OutputRoot, Trigger.Timed, _timedStartedAt);
        var startMeta = NewMeta(Trigger.Timed, _timedStartedAt, null);
        startMeta.Result = "recording";
        CaptureFolder.WriteMeta(_timedFolder, startMeta);
        TimedCaptureEndsAt = _timedStartedAt.AddSeconds(seconds);
        SetState(CaptureState.TimedCapturing);
        _log.Info(Source, $"時間指定の記録を開始しました（{seconds} 秒）");

        var cts = new CancellationTokenSource();
        _timedCts = cts;
        _ = Task.Run(async () =>
        {
            try { await _clock.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await RunExclusiveAsync(() => FinishTimedCaptureCoreAsync()).ConfigureAwait(false);
        });
    });

    private async Task FinishTimedCaptureCoreAsync()
    {
        if (_state != CaptureState.TimedCapturing || _timedFolder is null) return;
        var sw = Stopwatch.StartNew();
        var folder = _timedFolder;
        _timedFolder = null;
        TimedCaptureEndsAt = null;
        SetState(CaptureState.Saving);
        Notify("M-01", "保存しています…", NoticeLevel.Info);

        var meta = NewMeta(Trigger.Timed, _timedStartedAt, null);
        var ok = await StopToFolderAsync(folder, Trigger.Timed, meta).ConfigureAwait(false);
        FinishMeta(folder, meta, ok, sw);

        SetState(CaptureState.Idle);
        if (_monitoringWanted) await StartFlightRecorderCoreAsync().ConfigureAwait(false);
        AfterSave(folder, ok, sw);
    }

    // ---------------- 軽量監視 ----------------

    public Task SetCountersAsync(bool enabled) => RunExclusiveAsync(async () =>
    {
        if (enabled)
        {
            var r = await _counters.StartAsync(Settings.Counters, OutputRoot).ConfigureAwait(false);
            CountersRunning = r.Ok;
            if (!r.Ok) Notify("M-06", "軽量監視を開始できませんでした。詳しくはログを確認してください", NoticeLevel.Error);
        }
        else
        {
            await _counters.StopAsync().ConfigureAwait(false);
            CountersRunning = false;
        }
        Settings.Counters.Enabled = enabled;
        StateChanged?.Invoke(_state);
    });

    // ---------------- 設定の反映 ----------------

    /// <summary>設定画面で保存された設定を反映する。軽量監視の設定が変われば作り直す。</summary>
    public async Task ApplySettingsAsync(AppSettings newSettings)
    {
        var old = Settings;
        Settings = newSettings;
        var countersChanged = old.Counters.IntervalSeconds != newSettings.Counters.IntervalSeconds
            || old.Counters.MaxSizeMB != newSettings.Counters.MaxSizeMB
            || old.Counters.Enabled != newSettings.Counters.Enabled
            || !string.Equals(old.Storage.ResolvedOutputDirectory(), newSettings.Storage.ResolvedOutputDirectory(), StringComparison.OrdinalIgnoreCase);
        if (countersChanged) await SetCountersAsync(newSettings.Counters.Enabled).ConfigureAwait(false);
        if (old.Recording.DefaultProfile != newSettings.Recording.DefaultProfile)
        {
            await SetProfileAsync(newSettings.Recording.DefaultProfile).ConfigureAwait(false);
        }
    }

    // ---------------- メモ ----------------

    public void WriteMemo(string folder, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            File.WriteAllText(Path.Combine(folder, CaptureFolder.MemoFile), $"{_clock.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text.Trim()}{Environment.NewLine}");
        }
        catch (IOException ex)
        {
            _log.Warn(Source, $"メモを保存できませんでした（{ex.Message}）");
        }
    }

    // ---------------- 共通処理 ----------------

    private async Task<bool> StopToFolderAsync(string folder, Trigger trigger, CaptureMeta meta)
    {
        var etl = Path.Combine(folder, CaptureFolder.TraceFile);
        var r = await _wpr.StopAsync(etl, $"PerfSnap {CaptureFolder.TriggerText(trigger)}",
            TimeSpan.FromSeconds(Settings.Recording.StopTimeoutSeconds)).ConfigureAwait(false);
        if (!r.Ok)
        {
            meta.Error = r.Message;
            await _wpr.CancelAsync().ConfigureAwait(false);
            return false;
        }

        meta.Files.Add(CaptureFolder.TraceFile);

        // 軽量監視のデータも同じフォルダに残す（いつから重かったかを一緒に見られるように）
        if (CountersRunning)
        {
            var snap = await _counters.SnapshotAsync(Path.Combine(folder, CaptureFolder.CountersFile)).ConfigureAwait(false);
            if (snap.Ok) meta.Files.Add(CaptureFolder.CountersFile);
        }

        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, CaptureFolder.SysInfoFile), _sysInfo.Collect()).ConfigureAwait(false);
            meta.Files.Add(CaptureFolder.SysInfoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Source, $"調査情報を保存できませんでした（{ex.Message}）");
        }
        return true;
    }

    private void FinishMeta(string folder, CaptureMeta meta, bool ok, Stopwatch sw)
    {
        meta.DurationMs = sw.ElapsedMilliseconds;
        meta.Result = ok ? "ok" : "failed";
        if (File.Exists(Path.Combine(folder, CaptureFolder.MemoFile))) meta.Files.Add(CaptureFolder.MemoFile);
        CaptureFolder.WriteMeta(folder, meta);
    }

    private void AfterSave(string folder, bool ok, Stopwatch sw)
    {
        if (ok)
        {
            var size = RetentionManager.DirectorySize(folder);
            _log.Info(Source, $"保存しました：{Path.GetFileName(folder)}（{size / 1024 / 1024} MB、{sw.Elapsed.TotalSeconds:0} 秒）");
            Notify("M-02", $"保存しました（{size / 1024 / 1024} MB、{sw.Elapsed.TotalSeconds:0} 秒）。クリックでフォルダを開きます", NoticeLevel.Info, folder);
        }
        else
        {
            Notify("M-06", "保存に失敗しました。詳しくはログを確認してください", NoticeLevel.Error, folder);
        }

        _retention.Prune(OutputRoot, (long)Settings.Storage.MaxTotalGB * 1024 * 1024 * 1024, keepFolder: folder);
    }

    private bool CheckFreeSpace()
    {
        try
        {
            Directory.CreateDirectory(OutputRoot);
            var min = (long)Settings.Storage.MinFreeSpaceGB * 1024 * 1024 * 1024;
            if (_retention.HasEnoughFreeSpace(OutputRoot, min, out var free)) return true;
            _log.Warn(Source, $"空き容量が不足しているため保存しません（空き {free / 1024 / 1024} MB）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Error(Source, $"保存先を確認できませんでした（{ex.Message}）");
        }
        Notify("M-05", $"保存先の空きが {Settings.Storage.MinFreeSpaceGB} GB 未満のため保存しませんでした", NoticeLevel.Warning);
        return false;
    }

    private CaptureMeta NewMeta(Trigger trigger, DateTimeOffset createdAt, StallInfo? stall) => new()
    {
        ToolVersion = _toolVersion,
        CreatedAt = createdAt,
        Trigger = CaptureFolder.TriggerText(trigger),
        Profile = _activeProfile.Id,
        Stall = stall is null ? null : new StallMeta { Kind = stall.Kind.ToString(), LateMs = stall.LateMs, DetectedAt = stall.DetectedAt },
        ForegroundProcess = stall?.ForegroundProcess,
    };

    private void SetState(CaptureState s)
    {
        if (_state == s) return;
        _state = s;
        StateChanged?.Invoke(s);
    }

    private void Notify(string id, string text, NoticeLevel level, string? folder = null)
    {
        if (level != NoticeLevel.Info) _log.Write(level == NoticeLevel.Error ? LogLevel.Error : LogLevel.Warn, Source, $"{id} {text}");
        Notified?.Invoke(new Notice(id, text, level, folder));
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        await _queue.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Source, $"想定外の例外：{ex}");
            Notify("M-06", "想定外のエラーが起きました。詳しくはログを確認してください", NoticeLevel.Error);
        }
        finally
        {
            _queue.Release();
        }
    }

    public void Dispose()
    {
        _timedCts?.Cancel();
        _timedCts?.Dispose();
        _queue.Dispose();
    }
}

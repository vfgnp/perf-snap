using System.Diagnostics;
using Microsoft.Win32;
using PerfSnap.Core;
using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Recording;
using PerfSnap.Storage;

namespace PerfSnap.App;

/// <summary>タスクトレイ常駐の本体（要件 F-01〜F-06）。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string Source = nameof(TrayApp);

    private readonly Logger _log;
    private readonly SettingsStore _store;
    private readonly CaptureController _controller;
    private readonly StallDetector _detector;
    private readonly AutoStartManager _autoStart;
    private readonly HotkeyManager _hotkey;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly Control _ui; // ほかのスレッドからの通知を UI スレッドに戻すため
    private readonly List<string> _startupWarnings;

    private readonly ToolStripMenuItem _header = new() { Enabled = false };
    private readonly ToolStripMenuItem _saveNow = new("今すぐ保存");
    private readonly ToolStripMenuItem _timed = new("時間を指定して記録");
    private readonly ToolStripMenuItem _monitor = new("監視（フライトレコーダー）");
    private readonly ToolStripMenuItem _counters = new("軽量監視（カウンター）");
    private readonly ToolStripMenuItem _detection = new("自動検知");
    private readonly ToolStripMenuItem _profiles = new("プロファイル");

    private string? _lastFolder;
    private bool _exiting;

    public TrayApp(Logger log, SettingsStore store, AppSettings settings, List<string> startupWarnings)
    {
        _log = log;
        _store = store;
        _startupWarnings = startupWarnings;

        _ui = new Control();
        _ui.CreateControl();

        var runner = new ProcessRunner();
        CaptureController? controller = null;
        var wpr = new WprRecorder(runner, log, () => controller?.Settings.Recording ?? settings.Recording);
        controller = new CaptureController(settings, wpr, new CounterRecorder(runner, log),
            new RetentionManager(log), new SystemInfoCollector(), SystemClock.Instance, log);
        _controller = controller;
        _autoStart = new AutoStartManager(runner, log);
        _detector = new StallDetector(() => _controller.Settings.Detection, log);
        _hotkey = new HotkeyManager();

        _tray = new NotifyIcon
        {
            Icon = _icons.For(CaptureState.Idle),
            Text = "PerfSnap：起動中",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => Fire(_controller.SaveNowAsync(Trigger.Manual));
        _tray.BalloonTipClicked += (_, _) => OpenFolder(_lastFolder ?? _controller.OutputRoot);

        _controller.StateChanged += _ => OnUi(RefreshUi);
        _controller.Notified += n => OnUi(() => ShowNotice(n));
        _controller.SaveStarted += (folder, _) => OnUi(() => ShowMemo(folder));
        _detector.StallDetected += stall => Fire(_controller.OnStallAsync(stall));
        _hotkey.Pressed += () => Fire(_controller.SaveNowAsync(Trigger.Hotkey));

        SystemEvents.SessionEnding += OnSessionEnding;
        Application.ApplicationExit += (_, _) => Cleanup(fast: true);

        _ui.BeginInvoke(new Action(() => Fire(InitializeAsync())));
    }

    private async Task InitializeAsync()
    {
        foreach (var w in _startupWarnings) _log.Warn(Source, w);
        if (_startupWarnings.Count > 0)
        {
            ShowNotice(new Notice("M-00", "設定の一部を既定値に戻しました。詳しくはログを確認してください", NoticeLevel.Warning));
        }

        RegisterHotkey();
        await _controller.InitializeAsync();
        _detector.Start();
        RefreshUi();
        await CheckAutoStartLocationAsync();
        _log.Info(Source, "起動しました");
        await Task.Run(MemoryTrimmer.Trim);
    }

    // ---------------- メニュー ----------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _header.Font = new Font(menu.Font, FontStyle.Bold);

        _saveNow.Click += (_, _) => Fire(_controller.SaveNowAsync(Trigger.Manual));
        foreach (var sec in _controller.Settings.Recording.TimedCaptureSeconds)
        {
            var item = new ToolStripMenuItem($"{sec} 秒");
            item.Click += (_, _) => Fire(_controller.StartTimedCaptureAsync(sec));
            _timed.DropDownItems.Add(item);
        }
        _monitor.Click += (_, _) => Fire(_controller.State == CaptureState.Monitoring
            ? _controller.StopMonitoringAsync()
            : _controller.StartMonitoringAsync());
        _counters.Click += (_, _) => Fire(_controller.SetCountersAsync(!_controller.CountersRunning));
        _detection.Click += (_, _) => ToggleDetection();
        foreach (var p in Profiles.All)
        {
            var item = new ToolStripMenuItem(p.DisplayName) { Tag = p.Id };
            item.Click += (_, _) => Fire(_controller.SetProfileAsync(p.Id));
            _profiles.DropDownItems.Add(item);
        }

        var openFolder = new ToolStripMenuItem("保存フォルダを開く");
        openFolder.Click += (_, _) => OpenFolder(_controller.OutputRoot);
        var settings = new ToolStripMenuItem("設定...");
        settings.Click += (_, _) => Fire(OpenSettingsAsync());
        var exit = new ToolStripMenuItem("終了");
        exit.Click += (_, _) => Fire(ExitAsync());

        menu.Items.AddRange([
            _header, new ToolStripSeparator(),
            _saveNow, _timed, new ToolStripSeparator(),
            _monitor, _counters, _detection, _profiles, new ToolStripSeparator(),
            openFolder, settings, new ToolStripSeparator(),
            exit,
        ]);
        menu.Opening += (_, _) => RefreshUi();
        return menu;
    }

    private void RefreshUi()
    {
        var state = _controller.State;
        _tray.Icon = _icons.For(state);
        var stateText = state switch
        {
            CaptureState.Idle => "待機中",
            CaptureState.Monitoring => "監視中（直近のデータを保持）",
            CaptureState.TimedCapturing => _controller.TimedCaptureEndsAt is { } end
                ? $"記録中（残り {Math.Max(0, (int)(end - DateTimeOffset.Now).TotalSeconds)} 秒）"
                : "記録中",
            _ => "保存中",
        };
        _tray.Text = Truncate($"PerfSnap：{stateText}", 127);
        _header.Text = $"PerfSnap（{stateText}）";

        var hk = _controller.Settings.Hotkey.Display();
        _saveNow.ShortcutKeyDisplayString = hk;
        _saveNow.Text = state == CaptureState.TimedCapturing ? "記録を終えて保存" : "今すぐ保存";
        _saveNow.Enabled = state is CaptureState.Monitoring or CaptureState.TimedCapturing;
        _timed.Enabled = state is CaptureState.Idle or CaptureState.Monitoring;
        _monitor.Checked = state == CaptureState.Monitoring;
        _monitor.Enabled = state is CaptureState.Idle or CaptureState.Monitoring;
        _counters.Checked = _controller.CountersRunning;
        var det = _controller.Settings.Detection;
        _detection.Checked = det.SystemStall.Enabled || det.AppHung.Enabled;
        foreach (ToolStripMenuItem item in _profiles.DropDownItems)
        {
            item.Checked = (string?)item.Tag == _controller.ActiveProfile.Id;
            item.Enabled = state is CaptureState.Idle or CaptureState.Monitoring;
        }
    }

    private void ToggleDetection()
    {
        var s = _controller.Settings.Clone();
        var on = !(s.Detection.SystemStall.Enabled || s.Detection.AppHung.Enabled);
        s.Detection.SystemStall.Enabled = on;
        s.Detection.AppHung.Enabled = on;
        _store.Save(s);
        Fire(_controller.ApplySettingsAsync(s));
        _log.Info(Source, $"自動検知を{(on ? "オン" : "オフ")}にしました");
        RefreshUi();
    }

    // ---------------- 設定画面 ----------------

    private async Task OpenSettingsAsync()
    {
        var registered = await _autoStart.IsRegisteredAsync();
        using var form = new SettingsForm(_controller.Settings, registered, _store.FilePath);
        if (form.ShowDialog() != DialogResult.OK) return;

        var s = form.Result;
        _store.Save(s);
        await _controller.ApplySettingsAsync(s);
        RegisterHotkey();

        if (form.AutoStartChecked != registered)
        {
            var ok = form.AutoStartChecked
                ? await _autoStart.RegisterAsync(Environment.ProcessPath ?? Application.ExecutablePath)
                : await _autoStart.UnregisterAsync();
            if (!ok) ShowNotice(new Notice("M-06", "自動起動の設定を変更できませんでした。詳しくはログを確認してください", NoticeLevel.Error));
        }
        RefreshUi();
        _log.Info(Source, "設定を保存しました");
    }

    private async Task CheckAutoStartLocationAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var matches = await _autoStart.MatchesExeAsync(exe);
        if (matches == false)
        {
            ShowNotice(new Notice("M-08", "自動起動に登録された場所と、この exe の場所が違います。設定画面で登録し直してください", NoticeLevel.Warning));
        }
    }

    private void RegisterHotkey()
    {
        var hk = _controller.Settings.Hotkey;
        if (!_hotkey.Register(hk))
        {
            ShowNotice(new Notice("M-07", $"ホットキー {hk.Display()} は別のアプリが使っています。設定で変更してください", NoticeLevel.Warning));
        }
    }

    // ---------------- 通知・メモ ----------------

    private void ShowNotice(Notice n)
    {
        if (n.Folder is not null)
        {
            _lastFolder = n.Folder;
            // 保存が終わったら、保存処理で使ったメモリを返す
            _ = Task.Run(MemoryTrimmer.Trim);
        }
        if (!_controller.Settings.General.ShowNotifications && n.Level == NoticeLevel.Info) return;
        var icon = n.Level switch
        {
            NoticeLevel.Error => ToolTipIcon.Error,
            NoticeLevel.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        _tray.ShowBalloonTip(5000, "PerfSnap", n.Text, icon);
    }

    private void ShowMemo(string folder)
    {
        var form = new MemoForm(text => _controller.WriteMemo(folder, text));
        form.FormClosed += (_, _) => form.Dispose();
        form.Show();
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _log.Warn(Source, $"フォルダを開けませんでした（{ex.Message}）");
        }
    }

    // ---------------- 終了 ----------------

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _detector.Stop();
        await _controller.ShutdownAsync(fast: false);
        Cleanup(fast: false);
        ExitThread();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Cleanup(fast: true);

    private bool _cleaned;

    /// <summary>後片付け（詳細設計 §10.2）。何度呼ばれても 1 回だけ実行する。</summary>
    private void Cleanup(bool fast)
    {
        if (_cleaned) return;
        _cleaned = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _detector.Stop();
        if (fast)
        {
            // UI スレッドで待つため、別スレッドで実行して時間を区切る
            Task.Run(() => _controller.ShutdownAsync(fast: true)).Wait(TimeSpan.FromSeconds(10));
        }
        _hotkey.Dispose();
        _tray.Visible = false;
        _log.Info(Source, "終了しました");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _icons.Dispose();
            _detector.Dispose();
            _controller.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------- 補助 ----------------

    private void OnUi(Action action)
    {
        if (_ui.IsDisposed) return;
        if (_ui.InvokeRequired) _ui.BeginInvoke(action);
        else action();
    }

    /// <summary>非同期処理を投げっぱなしにせず、例外をログに残す。</summary>
    private void Fire(Task task) => task.ContinueWith(t =>
    {
        if (t.Exception is { } ex) _log.Error(Source, $"想定外の例外：{ex.GetBaseException()}");
    }, TaskScheduler.Default);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

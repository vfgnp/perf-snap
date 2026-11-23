using PerfSnap.Storage;

namespace PerfSnap.App;

/// <summary>設定画面（基本設計 §6.2）。OK を押したときだけ、編集した設定の写しを返す。</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _s;

    // 全般
    private readonly CheckBox _autoStart = new() { Text = "ログオン時に自動で起動する（管理者権限）", AutoSize = true };
    private readonly CheckBox _startMonitoring = new() { Text = "起動したら監視を始める", AutoSize = true };
    private readonly CheckBox _notifications = new() { Text = "通知を表示する", AutoSize = true };
    private readonly CheckBox _stopCounters = new() { Text = "終了時に軽量監視も止める", AutoSize = true };
    // ホットキー
    private readonly CheckBox _ctrl = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox _alt = new() { Text = "Alt", AutoSize = true };
    private readonly CheckBox _shift = new() { Text = "Shift", AutoSize = true };
    private readonly CheckBox _win = new() { Text = "Win", AutoSize = true };
    private readonly TextBox _key = new() { Width = 80 };
    // 記録
    private readonly ComboBox _profile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly NumericUpDown _bufferMB = Num(64, 2048, 32);
    private readonly CheckBox _compress = new() { Text = "保存時に圧縮する（サイズ約 1/3、時間は少し延びる）", AutoSize = true };
    private readonly CheckBox _countersEnabled = new() { Text = "軽量監視（カウンター）を使う", AutoSize = true };
    private readonly NumericUpDown _counterInterval = Num(1, 300);
    private readonly NumericUpDown _counterMax = Num(16, 10240);
    // 自動検知
    private readonly CheckBox _systemStall = new() { Text = "A. システムの停止を検知する", AutoSize = true };
    private readonly NumericUpDown _stallThreshold = Num(500, 10000, 100);
    private readonly CheckBox _appHung = new() { Text = "B. 前面アプリの応答なしを検知する", AutoSize = true };
    private readonly NumericUpDown _hungTimeout = Num(1000, 10000, 100);
    private readonly NumericUpDown _delay = Num(0, 60);
    private readonly NumericUpDown _cooldown = Num(1, 1440);
    private readonly NumericUpDown _maxPerDay = Num(1, 1000);
    private readonly CheckBox _memoOnAuto = new() { Text = "自動保存のときもメモ欄を出す", AutoSize = true };
    // 保存
    private readonly TextBox _outputDir = new() { Width = 320 };
    private readonly NumericUpDown _maxTotal = Num(1, 1000);
    private readonly NumericUpDown _minFree = Num(1, 1000);

    public bool AutoStartChecked => _autoStart.Checked;

    public SettingsForm(AppSettings current, bool autoStartRegistered, string settingsPath)
    {
        _s = current.Clone();
        Text = "PerfSnap の設定";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 430);

        var tabs = new TabControl { Dock = DockStyle.Top, Height = 370 };
        tabs.TabPages.Add(Page("全般", _autoStart, _startMonitoring, _notifications, _stopCounters,
            Note($"設定ファイル：{settingsPath}")));
        tabs.TabPages.Add(Page("ホットキー", Row("修飾キー", _ctrl, _alt, _shift, _win), Row("キー", _key),
            Note("「今すぐ保存」のキーです。キーは A〜Z、F1〜F12 などの名前で書きます。")));
        tabs.TabPages.Add(Page("記録", Row("既定のプロファイル", _profile),
            Row("監視に使うメモリ（MB）", _bufferMB),
            Note("大きいほど長い時間を残せますが、そのぶん PC のメモリを使います。変更は次に監視を始めたときに反映されます。"),
            _compress, _countersEnabled,
            Row("軽量監視の間隔（秒）", _counterInterval), Row("軽量監視の上限（MB）", _counterMax)));
        tabs.TabPages.Add(Page("自動検知", _systemStall, Row("　遅れのしきい値（ミリ秒）", _stallThreshold),
            _appHung, Row("　応答を待つ時間（ミリ秒）", _hungTimeout), Row("検知から保存までの待ち（秒）", _delay),
            Row("次の自動保存までの間隔（分）", _cooldown), Row("1 日の自動保存の上限（回）", _maxPerDay), _memoOnAuto));
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) => Browse();
        tabs.TabPages.Add(Page("保存", Row("保存先", _outputDir, browse), Row("容量の上限（GB）", _maxTotal),
            Row("最低限残す空き容量（GB）", _minFree),
            Note("上限を超えると、PerfSnap が作った古いデータから自動で削除します。ネットワークフォルダは指定できません。")));

        var ok = new Button { Text = "保存", DialogResult = DialogResult.None, Width = 90, Location = new Point(320, 388) };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90, Location = new Point(420, 388) };
        ok.Click += (_, _) => OnOk();
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([tabs, ok, cancel]);

        foreach (var p in Profiles.All) _profile.Items.Add(p.DisplayName);
        LoadValues(autoStartRegistered);
    }

    /// <summary>編集結果（OK のときだけ有効）。</summary>
    public AppSettings Result => _s;

    private void LoadValues(bool autoStartRegistered)
    {
        _autoStart.Checked = autoStartRegistered;
        _startMonitoring.Checked = _s.General.StartMonitoringOnLaunch;
        _notifications.Checked = _s.General.ShowNotifications;
        _stopCounters.Checked = _s.General.StopCountersOnExit;

        var mods = _s.Hotkey.Modifiers.Select(m => m.ToUpperInvariant()).ToHashSet();
        _ctrl.Checked = mods.Contains("CTRL") || mods.Contains("CONTROL");
        _alt.Checked = mods.Contains("ALT");
        _shift.Checked = mods.Contains("SHIFT");
        _win.Checked = mods.Contains("WIN");
        _key.Text = _s.Hotkey.Key;

        var idx = Profiles.All.ToList().FindIndex(p => p.Id == _s.Recording.DefaultProfile);
        _profile.SelectedIndex = Math.Max(0, idx);
        _bufferMB.Value = _s.Recording.BufferMB;
        _compress.Checked = _s.Recording.Compress;
        _countersEnabled.Checked = _s.Counters.Enabled;
        _counterInterval.Value = _s.Counters.IntervalSeconds;
        _counterMax.Value = _s.Counters.MaxSizeMB;

        _systemStall.Checked = _s.Detection.SystemStall.Enabled;
        _stallThreshold.Value = _s.Detection.SystemStall.ThresholdMs;
        _appHung.Checked = _s.Detection.AppHung.Enabled;
        _hungTimeout.Value = _s.Detection.AppHung.TimeoutMs;
        _delay.Value = _s.Detection.DelayBeforeSaveSeconds;
        _cooldown.Value = _s.Detection.CooldownMinutes;
        _maxPerDay.Value = _s.Detection.MaxAutoSavesPerDay;
        _memoOnAuto.Checked = _s.Detection.ShowMemoOnAutoSave;

        _outputDir.Text = _s.Storage.OutputDirectory;
        _maxTotal.Value = _s.Storage.MaxTotalGB;
        _minFree.Value = _s.Storage.MinFreeSpaceGB;
    }

    private void OnOk()
    {
        var hk = new HotkeySettings { Key = _key.Text.Trim(), Modifiers = [] };
        if (_ctrl.Checked) hk.Modifiers.Add("Ctrl");
        if (_alt.Checked) hk.Modifiers.Add("Alt");
        if (_shift.Checked) hk.Modifiers.Add("Shift");
        if (_win.Checked) hk.Modifiers.Add("Win");
        if (!HotkeyParser.TryParse(hk, out _, out _))
        {
            MessageBox.Show(this, "ホットキーを解釈できません。修飾キーを 1 つ以上選び、キーは A〜Z や F1〜F12 などの名前で入力してください。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!SettingsStore.IsValidOutputDirectory(_outputDir.Text, out var reason))
        {
            MessageBox.Show(this, $"保存先を使えません：{reason}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _s.General.StartMonitoringOnLaunch = _startMonitoring.Checked;
        _s.General.ShowNotifications = _notifications.Checked;
        _s.General.StopCountersOnExit = _stopCounters.Checked;
        _s.Hotkey = hk;
        _s.Recording.DefaultProfile = Profiles.All[Math.Max(0, _profile.SelectedIndex)].Id;
        _s.Recording.BufferMB = (int)_bufferMB.Value;
        _s.Recording.Compress = _compress.Checked;
        _s.Counters.Enabled = _countersEnabled.Checked;
        _s.Counters.IntervalSeconds = (int)_counterInterval.Value;
        _s.Counters.MaxSizeMB = (int)_counterMax.Value;
        _s.Detection.SystemStall.Enabled = _systemStall.Checked;
        _s.Detection.SystemStall.ThresholdMs = (int)_stallThreshold.Value;
        _s.Detection.AppHung.Enabled = _appHung.Checked;
        _s.Detection.AppHung.TimeoutMs = (int)_hungTimeout.Value;
        _s.Detection.DelayBeforeSaveSeconds = (int)_delay.Value;
        _s.Detection.CooldownMinutes = (int)_cooldown.Value;
        _s.Detection.MaxAutoSavesPerDay = (int)_maxPerDay.Value;
        _s.Detection.ShowMemoOnAutoSave = _memoOnAuto.Checked;
        _s.Storage.OutputDirectory = _outputDir.Text.Trim();
        _s.Storage.MaxTotalGB = (int)_maxTotal.Value;
        _s.Storage.MinFreeSpaceGB = (int)_minFree.Value;

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "保存先のフォルダを選んでください", UseDescriptionForTitle = true };
        var current = Environment.ExpandEnvironmentVariables(_outputDir.Text);
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == DialogResult.OK) _outputDir.Text = dlg.SelectedPath;
    }

    private static NumericUpDown Num(int min, int max, int step = 1) =>
        new() { Minimum = min, Maximum = max, Increment = step, Width = 90, TextAlign = HorizontalAlignment.Right };

    private static TabPage Page(string title, params Control[] rows)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        foreach (var r in rows)
        {
            r.Margin = new Padding(0, 4, 0, 6);
            flow.Controls.Add(r);
        }
        var page = new TabPage(title);
        page.Controls.Add(flow);
        return page;
    }

    private static FlowLayoutPanel Row(string label, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 200, MinimumSize = new Size(200, 0), Margin = new Padding(0, 6, 8, 0) });
        foreach (var c in controls) row.Controls.Add(c);
        return row;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = SystemColors.GrayText };
}

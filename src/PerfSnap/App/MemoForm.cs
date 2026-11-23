namespace PerfSnap.App;

/// <summary>
/// 保存時の一言メモ（基本設計 §6.3）。保存は入力を待たずに進む。30 秒入力がなければ閉じる。
/// </summary>
internal sealed class MemoForm : Form
{
    private readonly TextBox _text;
    private readonly System.Windows.Forms.Timer _timeout;
    private readonly Action<string> _onSubmit;

    public MemoForm(Action<string> onSubmit)
    {
        _onSubmit = onSubmit;
        Text = "PerfSnap：何が起きましたか？";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 110);

        var label = new Label
        {
            Text = "保存しています。状況を一言残せます（任意）",
            AutoSize = true,
            Location = new Point(10, 10),
        };
        _text = new TextBox { Location = new Point(10, 34), Width = 340, PlaceholderText = "例：Teams で入力中に 2 秒止まった" };
        var ok = new Button { Text = "残す", Location = new Point(194, 72), Width = 75, DialogResult = DialogResult.OK };
        var skip = new Button { Text = "閉じる", Location = new Point(275, 72), Width = 75, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Submit();
        skip.Click += (_, _) => Close();
        AcceptButton = ok;
        CancelButton = skip;
        Controls.AddRange([label, _text, ok, skip]);

        _timeout = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timeout.Tick += (_, _) => Close();
        _text.TextChanged += (_, _) => { _timeout.Stop(); _timeout.Start(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        _timeout.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _text.Focus();
    }

    private void Submit()
    {
        _onSubmit(_text.Text);
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timeout.Dispose();
        base.Dispose(disposing);
    }
}

using System.Drawing.Drawing2D;
using PerfSnap.Core;
using PerfSnap.Platform;

namespace PerfSnap.App;

/// <summary>
/// 状態ごとのトレイアイコン（基本設計 §4.2）。色だけに頼らず、形も変える。
/// 起動時に一度だけ描いて使い回す。
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<CaptureState, Icon> _icons = new();

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize.Width;
        _icons[CaptureState.Idle] = Draw(size, Color.FromArgb(120, 128, 136), DrawIdle);
        _icons[CaptureState.Monitoring] = Draw(size, Color.FromArgb(34, 139, 84), DrawWave);
        _icons[CaptureState.TimedCapturing] = Draw(size, Color.FromArgb(200, 48, 48), DrawDot);
        _icons[CaptureState.Saving] = Draw(size, Color.FromArgb(214, 150, 20), DrawArrow);
    }

    public Icon For(CaptureState state) => _icons[state];

    private static Icon Draw(int size, Color color, Action<Graphics, float> drawMark)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 0.5f, 0.5f, size - 1.5f, size - 1.5f);
            drawMark(g, size);
        }
        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(h);
        }
    }

    private static Pen WhitePen(float s) => new(Color.White, Math.Max(1.5f, s / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

    private static void DrawIdle(Graphics g, float s)
    {
        using var pen = WhitePen(s);
        g.DrawEllipse(pen, s * 0.32f, s * 0.32f, s * 0.34f, s * 0.34f);
    }

    private static void DrawWave(Graphics g, float s)
    {
        using var pen = WhitePen(s);
        g.DrawLines(pen, new[]
        {
            new PointF(s * 0.18f, s * 0.52f), new PointF(s * 0.36f, s * 0.52f), new PointF(s * 0.46f, s * 0.28f),
            new PointF(s * 0.58f, s * 0.74f), new PointF(s * 0.68f, s * 0.52f), new PointF(s * 0.82f, s * 0.52f),
        });
    }

    private static void DrawDot(Graphics g, float s)
    {
        using var brush = new SolidBrush(Color.White);
        g.FillEllipse(brush, s * 0.33f, s * 0.33f, s * 0.34f, s * 0.34f);
    }

    private static void DrawArrow(Graphics g, float s)
    {
        using var pen = WhitePen(s);
        g.DrawLine(pen, s * 0.5f, s * 0.22f, s * 0.5f, s * 0.70f);
        g.DrawLines(pen, new[] { new PointF(s * 0.30f, s * 0.52f), new PointF(s * 0.5f, s * 0.72f), new PointF(s * 0.70f, s * 0.52f) });
    }

    public void Dispose()
    {
        foreach (var i in _icons.Values) i.Dispose();
        _icons.Clear();
    }
}

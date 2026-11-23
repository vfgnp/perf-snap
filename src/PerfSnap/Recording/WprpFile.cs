namespace PerfSnap.Recording;

/// <summary>
/// 同梱の独自プロファイル PerfSnap.wprp（scripts/build-wprp.ps1 で生成）。
/// 組み込みプロファイルは搭載メモリに応じてバッファを確保し、結合テストでは約 1.3 GB を使った（テスト報告 §4）。
/// ここでバッファの量を指定して、監視中のメモリ使用量に上限を設ける。
/// </summary>
public static class WprpFile
{
    public const int MinBufferMB = 64;
    public const int MaxBufferMB = 2048;

    /// <summary>カーネル側（システム コレクター）に割り当てる割合。残りをユーザー側（イベント コレクター）に割り当てる。</summary>
    public const double SystemShare = 0.75;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfSnap", "PerfSnap.wprp");

    public static string Template()
    {
        using var stream = typeof(WprpFile).Assembly.GetManifestResourceStream("PerfSnap.wprp")
            ?? throw new InvalidOperationException("PerfSnap.wprp が exe に含まれていません");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>バッファの合計（MB）を、システム側とイベント側の 1 MB バッファの数に分ける。</summary>
    public static (int System, int Event) SplitBuffers(int totalMB)
    {
        totalMB = Math.Clamp(totalMB, MinBufferMB, MaxBufferMB);
        var system = (int)Math.Round(totalMB * SystemShare);
        return (system, totalMB - system);
    }

    public static string Render(int totalMB)
    {
        var (system, evt) = SplitBuffers(totalMB);
        return Template()
            .Replace("{SYSTEM_BUFFERS}", system.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{EVENT_BUFFERS}", evt.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>プロファイルを書き出し、そのパスを返す。内容が同じなら書き直さない。</summary>
    public static string Write(int totalMB, string? path = null)
    {
        path ??= DefaultPath;
        var content = Render(totalMB);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
        return path;
    }
}

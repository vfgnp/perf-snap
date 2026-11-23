using PerfSnap.Logging;
using PerfSnap.Storage;

namespace PerfSnap.Tests;

/// <summary>テストごとの一時フォルダ。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PerfSnapTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void ファイルがなければ既定値で作る()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        var s = new SettingsStore(path).Load(out var w);
        Assert.Empty(w);
        Assert.True(File.Exists(path));
        Assert.Equal(1500, s.Detection.SystemStall.ThresholdMs);
        Assert.Equal("P", s.Hotkey.Key);
    }

    [Fact]
    public void 保存した内容を読み戻せる()
    {
        using var t = new TempDir();
        var store = new SettingsStore(Path.Combine(t.Path, "settings.json"));
        var s = new AppSettings();
        s.Detection.CooldownMinutes = 30;
        s.Hotkey = new HotkeySettings { Modifiers = ["Ctrl", "Shift"], Key = "F9" };
        store.Save(s);
        var back = store.Load(out var w);
        Assert.Empty(w);
        Assert.Equal(30, back.Detection.CooldownMinutes);
        Assert.Equal(["Ctrl", "Shift"], back.Hotkey.Modifiers);
        Assert.Equal("F9", back.Hotkey.Key);
    }

    [Fact]
    public void 壊れたファイルは別名で残し既定値で起動する()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "settings.json");
        File.WriteAllText(path, "{ これは JSON ではない");
        var s = new SettingsStore(path).Load(out var w);
        Assert.NotEmpty(w);
        Assert.Single(Directory.GetFiles(t.Path, "settings.json.broken-*"));
        Assert.Equal(10, s.Storage.MaxTotalGB);
    }

    [Fact]
    public void 範囲外の値は既定値に戻す()
    {
        var s = new AppSettings();
        s.Detection.SystemStall.ThresholdMs = 50;
        s.Counters.IntervalSeconds = 0;
        var w = SettingsStore.Validate(s);
        Assert.Equal(2, w.Count);
        Assert.Equal(1500, s.Detection.SystemStall.ThresholdMs);
        Assert.Equal(15, s.Counters.IntervalSeconds);
    }

    [Theory]
    [InlineData(@"\\server\share\perf", false)]
    [InlineData(@"relative\path", false)]
    [InlineData(@"%LOCALAPPDATA%\PerfSnap\captures", true)]
    public void 保存先はローカルの絶対パスだけ許す(string dir, bool expected)
    {
        Assert.Equal(expected, SettingsStore.IsValidOutputDirectory(dir, out _));
    }
}

public class CaptureFolderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 14, 30, 15, TimeSpan.FromHours(9));

    [Fact]
    public void 日時ときっかけでフォルダを作り重なれば番号を付ける()
    {
        using var t = new TempDir();
        var a = CaptureFolder.Create(t.Path, Trigger.Hotkey, T0);
        var b = CaptureFolder.Create(t.Path, Trigger.Hotkey, T0);
        Assert.Equal("20261123_143015_hotkey", Path.GetFileName(a));
        Assert.Equal("20261123_143015_hotkey_2", Path.GetFileName(b));
    }

    [Fact]
    public void 自分のmetaだけを自分のものと判定する()
    {
        using var t = new TempDir();
        var own = CaptureFolder.Create(t.Path, Trigger.Manual, T0);
        CaptureFolder.WriteMeta(own, new CaptureMeta { CreatedAt = T0 });
        var other = Directory.CreateDirectory(Path.Combine(t.Path, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "meta.json"), """{"tool":"SomethingElse"}""");

        Assert.NotNull(CaptureFolder.TryReadOwnMeta(own));
        Assert.Null(CaptureFolder.TryReadOwnMeta(other));
    }
}

public class RetentionManagerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));

    private static string MakeCapture(string root, int dayOffset, int bytes)
    {
        var dir = CaptureFolder.Create(root, Trigger.Auto, T0.AddDays(dayOffset));
        CaptureFolder.WriteMeta(dir, new CaptureMeta { CreatedAt = T0.AddDays(dayOffset) });
        File.WriteAllBytes(Path.Combine(dir, "trace.etl"), new byte[bytes]);
        return dir;
    }

    [Fact]
    public void 上限を超えたら自分の古いフォルダから消し他は消さない()
    {
        using var t = new TempDir();
        var oldest = MakeCapture(t.Path, 0, 1000);
        var middle = MakeCapture(t.Path, 1, 1000);
        var newest = MakeCapture(t.Path, 2, 1000);

        var foreign = Directory.CreateDirectory(Path.Combine(t.Path, "利用者のフォルダ")).FullName;
        File.WriteAllBytes(Path.Combine(foreign, "data.bin"), new byte[5000]);
        var counters = Directory.CreateDirectory(Path.Combine(t.Path, "counters")).FullName;
        File.WriteAllBytes(Path.Combine(counters, "PerfSnap_Light.blg"), new byte[100]);

        // 上限 7000 バイト：合計 は約 8100 + meta。古いものから消して上限以下にする
        var deleted = new RetentionManager(Logger.Null()).Prune(t.Path, 7000, keepFolder: newest);

        Assert.Contains(Path.GetFileName(oldest), deleted);
        Assert.False(Directory.Exists(oldest));
        Assert.True(Directory.Exists(newest));
        Assert.True(Directory.Exists(foreign));   // meta.json のないフォルダは消さない（要件 N-21）
        Assert.True(Directory.Exists(counters));
        _ = middle;
    }

    [Fact]
    public void 上限以内なら何も消さない()
    {
        using var t = new TempDir();
        MakeCapture(t.Path, 0, 1000);
        Assert.Empty(new RetentionManager(Logger.Null()).Prune(t.Path, 1_000_000, null));
    }

    [Fact]
    public void 今保存したフォルダは上限を超えていても消さない()
    {
        using var t = new TempDir();
        var only = MakeCapture(t.Path, 0, 5000);
        new RetentionManager(Logger.Null()).Prune(t.Path, 100, keepFolder: only);
        Assert.True(Directory.Exists(only));
    }
}

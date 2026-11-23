using System.Text.Json;

namespace PerfSnap.Storage;

public enum Trigger { Manual, Hotkey, Auto, Timed }

public enum StallKind { System, AppHung }

public sealed record StallInfo(StallKind Kind, long LateMs, DateTimeOffset DetectedAt, string? ForegroundProcess);

/// <summary>保存フォルダに書く meta.json（詳細設計 §9.2）。</summary>
public sealed class CaptureMeta
{
    public const string ToolName = "PerfSnap";

    public string Tool { get; set; } = ToolName;
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Trigger { get; set; } = "";
    public string Profile { get; set; } = "";
    public StallMeta? Stall { get; set; }
    public string? ForegroundProcess { get; set; }
    public List<string> Files { get; set; } = [];
    public long DurationMs { get; set; }
    public string Result { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class StallMeta
{
    public string Kind { get; set; } = "";
    public long LateMs { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
}

/// <summary>保存フォルダの作成とファイル名の規約（詳細設計 §7.1、§9.1）。</summary>
public static class CaptureFolder
{
    public const string MetaFile = "meta.json";
    public const string TraceFile = "trace.etl";
    public const string SysInfoFile = "sysinfo.txt";
    public const string MemoFile = "memo.txt";
    public const string CountersFile = "counters.blg";
    public const string CountersDir = "counters";

    public static string TriggerText(Trigger t) => t switch
    {
        Trigger.Manual => "manual",
        Trigger.Hotkey => "hotkey",
        Trigger.Auto => "auto",
        _ => "timed",
    };

    /// <summary>yyyyMMdd_HHmmss_きっかけ のフォルダを作る。同じ名前があれば _2、_3 … を付ける。</summary>
    public static string Create(string root, Trigger trigger, DateTimeOffset now)
    {
        Directory.CreateDirectory(root);
        var baseName = $"{now:yyyyMMdd_HHmmss}_{TriggerText(trigger)}";
        var path = Path.Combine(root, baseName);
        for (var i = 2; Directory.Exists(path); i++)
        {
            path = Path.Combine(root, $"{baseName}_{i}");
        }
        Directory.CreateDirectory(path);
        return path;
    }

    public static void WriteMeta(string folder, CaptureMeta meta) =>
        File.WriteAllText(Path.Combine(folder, MetaFile),
            JsonSerializer.Serialize(meta, SettingsJsonContext.Default.CaptureMeta));

    /// <summary>PerfSnap が作ったフォルダなら meta を返す。違えば null。</summary>
    public static CaptureMeta? TryReadOwnMeta(string folder)
    {
        var path = Path.Combine(folder, MetaFile);
        if (!File.Exists(path)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.CaptureMeta);
            return meta?.Tool == CaptureMeta.ToolName ? meta : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

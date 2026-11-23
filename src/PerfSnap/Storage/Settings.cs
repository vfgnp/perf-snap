using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfSnap.Storage;

// 設定ファイルの形式（詳細設計 §7.1）。既定値はプロパティの初期値。

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkey { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public CounterSettings Counters { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings),
            SettingsJsonContext.Default.AppSettings)!;
}

public sealed class GeneralSettings
{
    public bool StartMonitoringOnLaunch { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool StopCountersOnExit { get; set; }
}

public sealed class HotkeySettings
{
    public List<string> Modifiers { get; set; } = ["Ctrl", "Alt", "Shift"];
    public string Key { get; set; } = "P";

    public string Display() => string.Join("+", Modifiers.Append(Key));
}

public sealed class RecordingSettings
{
    public string DefaultProfile { get; set; } = Profiles.InputLatency.Id;
    public List<int> TimedCaptureSeconds { get; set; } = [30, 60, 120, 300];
    public int StopTimeoutSeconds { get; set; } = 120;
    /// <summary>フライトレコーダーが使うメモリの上限（MB）。64〜2048。</summary>
    public int BufferMB { get; set; } = 256;
    /// <summary>保存時にトレースを圧縮する。</summary>
    public bool Compress { get; set; } = true;
}

public sealed class CounterSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 15;
    public int MaxSizeMB { get; set; } = 512;
}

public sealed class DetectionSettings
{
    public SystemStallSettings SystemStall { get; set; } = new();
    public AppHungSettings AppHung { get; set; } = new();
    public int DelayBeforeSaveSeconds { get; set; } = 5;
    public int CooldownMinutes { get; set; } = 10;
    public int MaxAutoSavesPerDay { get; set; } = 20;
    public bool ShowMemoOnAutoSave { get; set; }
}

public sealed class SystemStallSettings
{
    public bool Enabled { get; set; } = true;
    public int ThresholdMs { get; set; } = 1500;
}

public sealed class AppHungSettings
{
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; } = 2000;
}

public sealed class StorageSettings
{
    public const string DefaultOutputDirectory = @"%LOCALAPPDATA%\PerfSnap\captures";

    public string OutputDirectory { get; set; } = DefaultOutputDirectory;
    public int MaxTotalGB { get; set; } = 10;
    public int MinFreeSpaceGB { get; set; } = 5;

    /// <summary>環境変数を展開した保存先。</summary>
    public string ResolvedOutputDirectory() => Environment.ExpandEnvironmentVariables(OutputDirectory);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CaptureMeta))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

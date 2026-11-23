using System.Text.Json;

namespace PerfSnap.Storage;

/// <summary>設定ファイルの読み書きと入力値の検査（詳細設計 §7.2）。</summary>
public sealed class SettingsStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfSnap", "settings.json");

    private readonly string _path;

    public SettingsStore(string? path = null) => _path = path ?? DefaultPath;

    public string FilePath => _path;

    /// <summary>設定を読む。読めない・範囲外の値は既定値に戻し、その内容を warnings に返す。</summary>
    public AppSettings Load(out List<string> warnings)
    {
        warnings = [];
        if (!File.Exists(_path))
        {
            var created = new AppSettings();
            Save(created);
            return created;
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings);
        }
        catch (JsonException ex)
        {
            settings = null;
            warnings.Add($"設定ファイルを読めませんでした（{ex.Message}）。");
        }

        if (settings is null)
        {
            var broken = $"{_path}.broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_path, broken, overwrite: true);
            warnings.Add($"壊れた設定ファイルを {Path.GetFileName(broken)} として残し、既定値で起動しました。");
            settings = new AppSettings();
            Save(settings);
            return settings;
        }

        warnings.AddRange(Validate(settings));
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>範囲外の値を既定値に戻す。戻した項目の説明を返す。</summary>
    public static List<string> Validate(AppSettings s)
    {
        var w = new List<string>();
        var d = new AppSettings();

        s.General ??= d.General;
        s.Hotkey ??= d.Hotkey;
        s.Recording ??= d.Recording;
        s.Counters ??= d.Counters;
        s.Detection ??= d.Detection;
        s.Detection.SystemStall ??= d.Detection.SystemStall;
        s.Detection.AppHung ??= d.Detection.AppHung;
        s.Storage ??= d.Storage;

        s.Detection.SystemStall.ThresholdMs = Clamp("detection.systemStall.thresholdMs", s.Detection.SystemStall.ThresholdMs, 500, 10000, d.Detection.SystemStall.ThresholdMs, w);
        s.Detection.AppHung.TimeoutMs = Clamp("detection.appHung.timeoutMs", s.Detection.AppHung.TimeoutMs, 1000, 10000, d.Detection.AppHung.TimeoutMs, w);
        s.Detection.CooldownMinutes = Clamp("detection.cooldownMinutes", s.Detection.CooldownMinutes, 1, 1440, d.Detection.CooldownMinutes, w);
        s.Detection.MaxAutoSavesPerDay = Clamp("detection.maxAutoSavesPerDay", s.Detection.MaxAutoSavesPerDay, 1, 1000, d.Detection.MaxAutoSavesPerDay, w);
        s.Detection.DelayBeforeSaveSeconds = Clamp("detection.delayBeforeSaveSeconds", s.Detection.DelayBeforeSaveSeconds, 0, 60, d.Detection.DelayBeforeSaveSeconds, w);
        s.Counters.IntervalSeconds = Clamp("counters.intervalSeconds", s.Counters.IntervalSeconds, 1, 300, d.Counters.IntervalSeconds, w);
        s.Counters.MaxSizeMB = Clamp("counters.maxSizeMB", s.Counters.MaxSizeMB, 16, 10240, d.Counters.MaxSizeMB, w);
        s.Recording.StopTimeoutSeconds = Clamp("recording.stopTimeoutSeconds", s.Recording.StopTimeoutSeconds, 30, 600, d.Recording.StopTimeoutSeconds, w);
        s.Recording.BufferMB = Clamp("recording.bufferMB", s.Recording.BufferMB, 64, 2048, d.Recording.BufferMB, w);
        s.Storage.MaxTotalGB = Clamp("storage.maxTotalGB", s.Storage.MaxTotalGB, 1, 1000, d.Storage.MaxTotalGB, w);
        s.Storage.MinFreeSpaceGB = Clamp("storage.minFreeSpaceGB", s.Storage.MinFreeSpaceGB, 1, 1000, d.Storage.MinFreeSpaceGB, w);

        if (Profiles.Find(s.Recording.DefaultProfile) is null)
        {
            w.Add($"recording.defaultProfile「{s.Recording.DefaultProfile}」は存在しないため既定値に戻しました。");
            s.Recording.DefaultProfile = d.Recording.DefaultProfile;
        }

        if (s.Recording.TimedCaptureSeconds is null || s.Recording.TimedCaptureSeconds.Count == 0 ||
            s.Recording.TimedCaptureSeconds.Any(x => x < 5 || x > 3600))
        {
            w.Add("recording.timedCaptureSeconds は 5〜3600 秒の一覧にしてください。既定値に戻しました。");
            s.Recording.TimedCaptureSeconds = d.Recording.TimedCaptureSeconds;
        }

        if (!IsValidOutputDirectory(s.Storage.OutputDirectory, out var reason))
        {
            w.Add($"storage.outputDirectory を既定値に戻しました（{reason}）。");
            s.Storage.OutputDirectory = StorageSettings.DefaultOutputDirectory;
        }

        if (!HotkeyParser.TryParse(s.Hotkey, out _, out _))
        {
            w.Add($"ホットキー「{s.Hotkey.Display()}」を解釈できないため既定値に戻しました。");
            s.Hotkey = d.Hotkey;
        }

        return w;
    }

    public static bool IsValidOutputDirectory(string? dir, out string reason)
    {
        if (string.IsNullOrWhiteSpace(dir)) { reason = "空です"; return false; }
        var expanded = Environment.ExpandEnvironmentVariables(dir);
        if (expanded.StartsWith(@"\\", StringComparison.Ordinal) || expanded.StartsWith("//", StringComparison.Ordinal))
        {
            reason = "ネットワークパスは使えません"; return false;
        }
        if (!Path.IsPathFullyQualified(expanded)) { reason = "絶対パスではありません"; return false; }
        var root = Path.GetPathRoot(expanded);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { reason = "ドライブが存在しません"; return false; }
        reason = "";
        return true;
    }

    private static int Clamp(string name, int value, int min, int max, int fallback, List<string> w)
    {
        if (value >= min && value <= max) return value;
        w.Add($"{name} = {value} は範囲外（{min}〜{max}）のため既定値 {fallback} に戻しました。");
        return fallback;
    }
}

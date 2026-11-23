using System.Text;

namespace PerfSnap.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// ツール自身のログ（詳細設計 §11）。
/// 書き込むのは操作・エラーなどの出来事だけで、頻度は低い。1 MB で世代交代し、5 世代まで残す。
/// </summary>
public sealed class Logger : IDisposable
{
    private const long MaxBytes = 1024 * 1024;
    private const int Generations = 5;

    private readonly object _lock = new();
    private readonly string? _path;
    private StreamWriter? _writer;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>ファイルに書かないロガー（テスト用）。</summary>
    public static Logger Null() => new(null);

    public Logger(string? path)
    {
        _path = path;
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Open();
        }
    }

    public void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public void Write(LogLevel level, string source, string message)
    {
        if (level < MinimumLevel || _path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(level)}] {source}: {message}";
        lock (_lock)
        {
            try
            {
                RotateIfNeeded();
                _writer?.WriteLine(line);
            }
            catch (IOException)
            {
                // ログの失敗で本体を止めない
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    private void Open()
    {
        var stream = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private void RotateIfNeeded()
    {
        if (_writer is null || _writer.BaseStream.Length < MaxBytes) return;
        _writer.Dispose();
        var dir = Path.GetDirectoryName(_path!)!;
        var name = Path.GetFileNameWithoutExtension(_path!);
        var ext = Path.GetExtension(_path!);
        for (var i = Generations - 1; i >= 1; i--)
        {
            var src = Path.Combine(dir, $"{name}.{i}{ext}");
            var dst = Path.Combine(dir, $"{name}.{i + 1}{ext}");
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
        File.Move(_path!, Path.Combine(dir, $"{name}.1{ext}"), overwrite: true);
        Open();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

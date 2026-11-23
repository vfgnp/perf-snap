using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Storage;

namespace PerfSnap.Recording;

public sealed record OpResult(bool Ok, string Message)
{
    public static readonly OpResult Success = new(true, "");
    public static OpResult Fail(string message) => new(false, message);
}

public interface IWprRecorder
{
    Task<OpResult> StartMemoryModeAsync(Profile profile);
    Task<OpResult> StartFileModeAsync(Profile profile);
    Task<OpResult> MarkerAsync(string name);
    Task<OpResult> StopAsync(string etlPath, string description, TimeSpan timeout);
    Task<OpResult> CancelAsync();
}

/// <summary>wpr.exe の操作（詳細設計 §8.1）。すべてのコマンドの最後に -instancename PerfSnap を付ける。</summary>
public sealed class WprRecorder : IWprRecorder
{
    public const string InstanceName = "PerfSnap";
    public static string WprPath => Path.Combine(Environment.SystemDirectory, "wpr.exe");

    private readonly IProcessRunner _runner;
    private readonly Logger _log;
    private readonly Func<RecordingSettings> _settings;
    private readonly string? _wprpPath;

    public WprRecorder(IProcessRunner runner, Logger log, Func<RecordingSettings> settings, string? wprpPath = null)
    {
        _runner = runner;
        _log = log;
        _settings = settings;
        _wprpPath = wprpPath;
    }

    /// <summary>同梱プロファイルで開始する引数（例：-start "C:\…\PerfSnap.wprp!PerfSnap_InputLatency"）。</summary>
    public static List<string> StartArgs(Profile profile, string wprpPath, bool fileMode)
    {
        var args = new List<string> { "-start", $"{wprpPath}!{profile.WprpName}" };
        if (fileMode) args.Add("-filemode");
        AddInstance(args);
        return args;
    }

    /// <summary>
    /// 保存して停止の引数。-skipPdbGen で .NET のシンボル生成を省く（セキュリティ製品やドライバーの調査には不要）。
    /// -compress でファイルを約 1/3 にする（結合テストで 420 MB → 146 MB、時間は約 8 秒増）。
    /// </summary>
    public static List<string> StopArgs(string etlPath, string description, bool compress)
    {
        var args = new List<string> { "-stop", etlPath, description, "-skipPdbGen" };
        if (compress) args.Add("-compress");
        AddInstance(args);
        return args;
    }

    public Task<OpResult> StartMemoryModeAsync(Profile profile) => StartAsync(profile, fileMode: false);

    public Task<OpResult> StartFileModeAsync(Profile profile) => StartAsync(profile, fileMode: true);

    private Task<OpResult> StartAsync(Profile profile, bool fileMode)
    {
        string path;
        try
        {
            path = WprpFile.Write(_settings().BufferMB, _wprpPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error(nameof(WprRecorder), $"プロファイルを書き出せませんでした（{ex.Message}）");
            return Task.FromResult(OpResult.Fail(ex.Message));
        }
        return RunAsync(fileMode ? "開始（ファイル）" : "開始（メモリー）", StartArgs(profile, path, fileMode), TimeSpan.FromSeconds(30));
    }

    public Task<OpResult> MarkerAsync(string name)
    {
        var args = new List<string> { "-marker", name };
        AddInstance(args);
        return RunAsync("目印", args, TimeSpan.FromSeconds(10));
    }

    public Task<OpResult> StopAsync(string etlPath, string description, TimeSpan timeout)
    {
        var args = StopArgs(etlPath, description, _settings().Compress);
        return RunAsync("保存して停止", args, timeout);
    }

    public Task<OpResult> CancelAsync()
    {
        var args = new List<string> { "-cancel" };
        AddInstance(args);
        return RunAsync("破棄して停止", args, TimeSpan.FromSeconds(30), quietOnFailure: true);
    }

    // ヘルプの指定どおり、-instancename は必ず最後の引数にする
    private static void AddInstance(List<string> args)
    {
        args.Add("-instancename");
        args.Add(InstanceName);
    }

    private async Task<OpResult> RunAsync(string what, List<string> args, TimeSpan timeout, bool quietOnFailure = false)
    {
        _log.Debug(nameof(WprRecorder), $"wpr {string.Join(' ', args)}");
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(WprPath, args, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Error(nameof(WprRecorder), $"{what}：wpr.exe を起動できませんでした（{ex.Message}）");
            return OpResult.Fail(ex.Message);
        }

        if (r.Succeeded)
        {
            _log.Info(nameof(WprRecorder), $"{what}：成功");
            return OpResult.Success;
        }

        var level = quietOnFailure ? LogLevel.Debug : LogLevel.Error;
        _log.Write(level, nameof(WprRecorder), $"{what}：失敗（{r.Summary()}）");
        return OpResult.Fail(r.Summary());
    }
}

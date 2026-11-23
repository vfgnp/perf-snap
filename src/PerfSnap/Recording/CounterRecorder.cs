using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Storage;

namespace PerfSnap.Recording;

public interface ICounterRecorder
{
    /// <summary>軽量監視を（作り直して）開始する。</summary>
    Task<OpResult> StartAsync(CounterSettings settings, string outputRoot);
    Task<OpResult> StopAsync();

    /// <summary>
    /// 記録中の .blg は読めない（結合テスト IT-E で確認）ため、いったん止めてコピーし、記録を再開する。
    /// 再開すると .blg は新しく書き直される。
    /// </summary>
    Task<OpResult> SnapshotAsync(string destFile);
}

/// <summary>logman.exe の操作（詳細設計 §8.2）。</summary>
public sealed class CounterRecorder : ICounterRecorder
{
    public const string CollectorName = "PerfSnap_Light";
    public static string LogmanPath => Path.Combine(Environment.SystemDirectory, "logman.exe");
    public static string CounterFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfSnap", "counters.txt");

    private readonly IProcessRunner _runner;
    private readonly Logger _log;
    private readonly string _counterFile;

    public CounterRecorder(IProcessRunner runner, Logger log, string? counterFile = null)
    {
        _runner = runner;
        _log = log;
        _counterFile = counterFile ?? CounterFilePath;
    }

    public static List<string> CreateArgs(CounterSettings s, string counterFile, string outputRoot)
    {
        var interval = TimeSpan.FromSeconds(s.IntervalSeconds);
        return
        [
            "create", "counter", CollectorName,
            "-cf", counterFile,
            "-si", $"{(int)interval.TotalHours:00}:{interval.Minutes:00}:{interval.Seconds:00}",
            "-f", "bincirc",
            "-max", s.MaxSizeMB.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-o", Path.Combine(outputRoot, CaptureFolder.CountersDir, CollectorName),
            "-ow",
        ];
    }

    public async Task<OpResult> StartAsync(CounterSettings settings, string outputRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_counterFile)!);
        await File.WriteAllLinesAsync(_counterFile, CounterSet.Light).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(outputRoot, CaptureFolder.CountersDir));

        // 設定が変わっていても反映されるよう、毎回作り直す（前回の残りがなければ失敗するが問題ない）
        await RunAsync("停止", ["stop", CollectorName], quietOnFailure: true).ConfigureAwait(false);
        await RunAsync("削除", ["delete", CollectorName], quietOnFailure: true).ConfigureAwait(false);

        var created = await RunAsync("作成", CreateArgs(settings, _counterFile, outputRoot)).ConfigureAwait(false);
        if (!created.Ok) return created;
        var started = await RunAsync("開始", ["start", CollectorName]).ConfigureAwait(false);
        _outputRoot = started.Ok ? outputRoot : null;
        return started;
    }

    public Task<OpResult> StopAsync() => RunAsync("停止", ["stop", CollectorName], quietOnFailure: true);

    private string? _outputRoot;

    public async Task<OpResult> SnapshotAsync(string destFile)
    {
        if (_outputRoot is null) return OpResult.Fail("軽量監視が動いていません");
        var stopped = await RunAsync("停止（書き出しのため）", ["stop", CollectorName]).ConfigureAwait(false);
        if (!stopped.Ok) return stopped;

        OpResult result;
        try
        {
            // logman は出力名に _000001 のような番号を付けるため、いちばん新しい .blg を探す
            var dir = Path.Combine(_outputRoot, CaptureFolder.CountersDir);
            var blg = new DirectoryInfo(dir).EnumerateFiles($"{CollectorName}*.blg")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (blg is null)
            {
                result = OpResult.Fail("軽量監視の .blg が見つかりません");
            }
            else
            {
                File.Copy(blg.FullName, destFile, overwrite: true);
                result = OpResult.Success;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = OpResult.Fail(ex.Message);
        }
        if (!result.Ok) _log.Warn(nameof(CounterRecorder), $"軽量監視のデータを書き出せませんでした（{result.Message}）");

        var restarted = await RunAsync("再開", ["start", CollectorName]).ConfigureAwait(false);
        return restarted.Ok ? result : restarted;
    }

    private async Task<OpResult> RunAsync(string what, List<string> args, bool quietOnFailure = false)
    {
        _log.Debug(nameof(CounterRecorder), $"logman {string.Join(' ', args)}");
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(LogmanPath, args, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Error(nameof(CounterRecorder), $"{what}：logman.exe を起動できませんでした（{ex.Message}）");
            return OpResult.Fail(ex.Message);
        }

        if (r.Succeeded)
        {
            _log.Info(nameof(CounterRecorder), $"{what}：成功");
            return OpResult.Success;
        }
        _log.Write(quietOnFailure ? LogLevel.Debug : LogLevel.Error, nameof(CounterRecorder), $"{what}：失敗（{r.Summary()}）");
        return OpResult.Fail(r.Summary());
    }
}

using System.Diagnostics;

namespace PerfSnap.Platform;

/// <summary>外部コマンドの実行結果。</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;

    public string Summary()
    {
        if (TimedOut) return "タイムアウトしました";
        var text = (StdOut + Environment.NewLine + StdErr).Trim();
        return $"終了コード {ExitCode}: {text}";
    }
}

/// <summary>外部コマンドを起動する境界。テストでは模擬に差し替える。</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>時刻の境界。テストでは時刻を自由に進められる模擬に差し替える。</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct = default);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    private readonly System.Text.Encoding _encoding;

    public ProcessRunner()
    {
        // wpr / logman はコンソールのコードページ（日本語環境では CP932）で出力する
        var oem = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        try { _encoding = System.Text.Encoding.GetEncoding(oem); }
        catch (ArgumentException) { _encoding = System.Text.Encoding.UTF8; }
        catch (NotSupportedException) { _encoding = System.Text.Encoding.UTF8; }
    }

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = _encoding,
            StandardErrorEncoding = _encoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ProcessResult(-1, "", "", TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }
}

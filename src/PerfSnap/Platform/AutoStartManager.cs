using System.Security;
using System.Security.Principal;
using System.Text;
using PerfSnap.Logging;

namespace PerfSnap.Platform;

/// <summary>
/// ログオン時の自動起動（詳細設計 §8.3）。
/// schtasks の引数だけで登録すると「AC 電源のときだけ」「72 時間で停止」が付くため、XML で登録する。
/// </summary>
public sealed class AutoStartManager
{
    public const string TaskName = "PerfSnap";
    public static string SchtasksPath => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    private readonly IProcessRunner _runner;
    private readonly Logger _log;

    public AutoStartManager(IProcessRunner runner, Logger log)
    {
        _runner = runner;
        _log = log;
    }

    public static string BuildTaskXml(string exePath, string userId)
    {
        var exe = SecurityElement.Escape(exePath);
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>PerfSnap をログオン時に管理者権限で起動します。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Enabled>true</Enabled>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>--autostart</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public async Task<bool> IsRegisteredAsync()
    {
        var r = await _runner.RunAsync(SchtasksPath, ["/Query", "/TN", TaskName], TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return r.Succeeded;
    }

    /// <summary>登録されているタスクの実行ファイルが exePath と一致するか。未登録なら null。</summary>
    public async Task<bool?> MatchesExeAsync(string exePath)
    {
        var r = await _runner.RunAsync(SchtasksPath, ["/Query", "/TN", TaskName, "/XML"], TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!r.Succeeded) return null;
        return r.StdOut.Contains(SecurityElement.Escape(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> RegisterAsync(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xmlPath = Path.Combine(Path.GetTempPath(), $"PerfSnap_task_{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks の /XML は UTF-16 を期待する
            await File.WriteAllTextAsync(xmlPath, BuildTaskXml(exePath, user), Encoding.Unicode).ConfigureAwait(false);
            var r = await _runner.RunAsync(SchtasksPath, ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (r.Succeeded) _log.Info(nameof(AutoStartManager), "自動起動を登録しました");
            else _log.Error(nameof(AutoStartManager), $"自動起動を登録できませんでした（{r.Summary()}）");
            return r.Succeeded;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (IOException) { }
        }
    }

    public async Task<bool> UnregisterAsync()
    {
        var r = await _runner.RunAsync(SchtasksPath, ["/Delete", "/TN", TaskName, "/F"], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (r.Succeeded) _log.Info(nameof(AutoStartManager), "自動起動を解除しました");
        else _log.Warn(nameof(AutoStartManager), $"自動起動を解除できませんでした（{r.Summary()}）");
        return r.Succeeded;
    }
}

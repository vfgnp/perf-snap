using System.Text;
using PerfSnap.App;
using PerfSnap.Logging;
using PerfSnap.Recording;
using PerfSnap.Storage;

namespace PerfSnap;

internal static class Program
{
    private const string MutexName = @"Global\PerfSnap_SingleInstance";

    [STAThread]
    private static int Main()
    {
        // wpr / logman の出力（CP932 など）を読むため
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PerfSnap はすでに起動しています。タスクトレイのアイコンから操作してください。",
                "PerfSnap", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();

        var store = new SettingsStore();
        var settings = store.Load(out var warnings);
        var outputRoot = settings.Storage.ResolvedOutputDirectory();
        using var log = new Logger(Path.Combine(outputRoot, "perfsnap.log"));

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnFatal(log, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(log, e.ExceptionObject as Exception);

        using var app = new TrayApp(log, store, settings, warnings);
        Application.Run(app);
        return 0;
    }

    private static void OnFatal(Logger log, Exception? ex)
    {
        log.Error("Program", $"想定外の例外で終了します：{ex}");
        // 記録を残したまま落ちないよう、最低限の後片付けをする（要件 F-32）
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(WprRecorder.WprPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-cancel");
            psi.ArgumentList.Add("-instancename");
            psi.ArgumentList.Add(WprRecorder.InstanceName);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15_000);
        }
        catch (Exception cleanupEx)
        {
            log.Error("Program", $"後片付けに失敗しました：{cleanupEx.Message}");
        }
    }
}

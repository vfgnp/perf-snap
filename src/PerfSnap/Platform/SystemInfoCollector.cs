using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PerfSnap.Platform;

public interface ISystemInfoCollector
{
    string Collect();
}

/// <summary>
/// 保存時の調査情報（詳細設計 §9.3）。外部コマンドは使わず、.NET の API とレジストリだけで集める。
/// </summary>
public sealed class SystemInfoCollector : ISystemInfoCollector
{
    public string Collect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PerfSnap 調査情報");
        sb.AppendLine($"取得日時      : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"PC 名         : {Environment.MachineName}");
        sb.AppendLine($"OS            : {OsText()}");
        sb.AppendLine($"CPU           : {Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim()}（論理 {Environment.ProcessorCount} コア）");
        var mem = GC.GetGCMemoryInfo();
        sb.AppendLine($"メモリ        : {mem.TotalAvailableMemoryBytes / 1024 / 1024:N0} MB");
        sb.AppendLine($"起動後の時間  : {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm\\:ss}");
        sb.AppendLine($"ランタイム    : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine("# プロセス一覧（CPU 時間の多い順）");
        sb.AppendLine($"{"名前",-36} {"PID",7} {"CPU時間(秒)",12} {"メモリ(MB)",10}");

        var rows = new List<(string Name, int Pid, double Cpu, long Mem)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                double cpu = -1;
                long ws = 0;
                try { cpu = p.TotalProcessorTime.TotalSeconds; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                try { ws = p.WorkingSet64; } catch (InvalidOperationException) { }
                rows.Add((p.ProcessName, p.Id, cpu, ws));
            }
        }

        foreach (var r in rows.OrderByDescending(r => r.Cpu))
        {
            var cpuText = r.Cpu < 0 ? "-" : r.Cpu.ToString("N1");
            sb.AppendLine($"{r.Name,-36} {r.Pid,7} {cpuText,12} {r.Mem / 1024 / 1024,10:N0}");
        }
        return sb.ToString();
    }

    private static string OsText()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var name = Reg(key, "ProductName");
        var display = Reg(key, "DisplayVersion");
        var build = Reg(key, "CurrentBuild");
        var ubr = Registry.LocalMachine.OpenSubKey(key)?.GetValue("UBR");
        // Windows 11 でも ProductName が "Windows 10" のままのことがあるため、ビルド番号を併記する
        return $"{name} {display}（ビルド {build}.{ubr}）";
    }

    private static string? Reg(string key, string value) =>
        Registry.LocalMachine.OpenSubKey(key)?.GetValue(value)?.ToString();
}

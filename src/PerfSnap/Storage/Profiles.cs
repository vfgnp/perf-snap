namespace PerfSnap.Storage;

/// <summary>
/// 記録プロファイル（詳細設計 §7.3）。WprpName は同梱の PerfSnap.wprp の中のプロファイル名。
/// Sources は、そのプロファイルの元にした wpr の組み込みプロファイル（説明用）。
/// </summary>
public sealed record Profile(string Id, string DisplayName, string WprpName, IReadOnlyList<string> Sources);

public static class Profiles
{
    public static readonly Profile InputLatency = new(
        "input-latency", "入力遅延の調査", "PerfSnap_InputLatency",
        ["CPU", "DiskIO", "Minifilter", "DesktopComposition"]);

    public static readonly Profile Detailed = new(
        "detailed", "詳細", "PerfSnap_Detailed",
        ["CPU", "DiskIO", "Minifilter", "DesktopComposition", "FileIO", "Registry"]);

    public static readonly IReadOnlyList<Profile> All = [InputLatency, Detailed];

    public static Profile? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>軽量監視のカウンター一覧（詳細設計 §7.4）。</summary>
public static class CounterSet
{
    public static readonly IReadOnlyList<string> Light =
    [
        @"\Processor(_Total)\% Processor Time",
        @"\Processor(_Total)\% DPC Time",
        @"\Processor(_Total)\% Interrupt Time",
        @"\System\Processor Queue Length",
        @"\Memory\Available MBytes",
        @"\Memory\Pages/sec",
        @"\PhysicalDisk(_Total)\% Idle Time",
        @"\PhysicalDisk(_Total)\Avg. Disk Queue Length",
        @"\Process(*)\% Processor Time",
        @"\Process(*)\IO Data Bytes/sec",
        @"\Process(*)\Working Set",
    ];
}

using PerfSnap.Logging;

namespace PerfSnap.Storage;

/// <summary>
/// 保存先の容量管理（詳細設計 §9.4、要件 N-21）。
/// 消すのは meta.json の tool が PerfSnap のフォルダだけ。counters と、指定したフォルダは消さない。
/// </summary>
public class RetentionManager
{
    private readonly Logger _log;

    public RetentionManager(Logger log) => _log = log;

    /// <summary>保存先ドライブの空きが minFreeBytes 以上あるか。</summary>
    public virtual bool HasEnoughFreeSpace(string root, long minFreeBytes, out long freeBytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        freeBytes = drive.AvailableFreeSpace;
        return freeBytes >= minFreeBytes;
    }

    /// <summary>合計が maxTotalBytes を超えていれば、古い保存フォルダから消す。消したフォルダ名を返す。</summary>
    public IReadOnlyList<string> Prune(string root, long maxTotalBytes, string? keepFolder)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(root)) return deleted;

        var total = DirectorySize(root);
        if (total <= maxTotalBytes) return deleted;

        var keep = keepFolder is null ? null : Path.GetFullPath(keepFolder).TrimEnd('\\');
        var candidates = Directory.EnumerateDirectories(root)
            .Where(d => !string.Equals(Path.GetFileName(d), CaptureFolder.CountersDir, StringComparison.OrdinalIgnoreCase))
            .Where(d => !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), keep, StringComparison.OrdinalIgnoreCase))
            .Select(d => (Dir: d, Meta: CaptureFolder.TryReadOwnMeta(d)))
            .Where(x => x.Meta is not null)
            .OrderBy(x => x.Meta!.CreatedAt)
            .ToList();

        foreach (var (dir, _) in candidates)
        {
            if (total <= maxTotalBytes) break;
            var size = DirectorySize(dir);
            try
            {
                Directory.Delete(dir, recursive: true);
                total -= size;
                deleted.Add(Path.GetFileName(dir));
                _log.Info(nameof(RetentionManager), $"容量の上限を超えたため削除しました：{Path.GetFileName(dir)}（{size / 1024 / 1024} MB）");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(nameof(RetentionManager), $"削除できませんでした：{Path.GetFileName(dir)}（{ex.Message}）");
            }
        }

        return deleted;
    }

    public static long DirectorySize(string dir)
    {
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sum;
    }
}

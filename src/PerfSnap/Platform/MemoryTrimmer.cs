using System.Runtime;
using System.Runtime.InteropServices;

namespace PerfSnap.Platform;

/// <summary>
/// 待機中のメモリを減らす（要件 N-02）。
/// 結合テストで、起動直後の作業セットが約 120 MB あった。起動処理や保存処理で一時的に使ったメモリを
/// GC で回収し、作業セットを OS に返す。待機中はほとんどのページに触れないので、少ないまま保たれる。
/// </summary>
public static class MemoryTrimmer
{
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        EmptyWorkingSet(self.Handle);
    }
}

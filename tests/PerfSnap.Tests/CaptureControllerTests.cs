using PerfSnap.Core;
using PerfSnap.Logging;
using PerfSnap.Platform;
using PerfSnap.Recording;
using PerfSnap.Storage;

namespace PerfSnap.Tests;

internal sealed class FakeWpr : IWprRecorder
{
    public List<string> Calls { get; } = [];
    public bool FailStart { get; set; }
    public bool FailStop { get; set; }

    public Task<OpResult> StartMemoryModeAsync(Profile profile) { Calls.Add($"start-memory:{profile.Id}"); return Result(!FailStart); }
    public Task<OpResult> StartFileModeAsync(Profile profile) { Calls.Add($"start-file:{profile.Id}"); return Result(!FailStart); }
    public Task<OpResult> MarkerAsync(string name) { Calls.Add($"marker:{name}"); return Result(true); }
    public Task<OpResult> CancelAsync() { Calls.Add("cancel"); return Result(true); }

    public Task<OpResult> StopAsync(string etlPath, string description, TimeSpan timeout)
    {
        Calls.Add("stop");
        if (FailStop) return Result(false);
        File.WriteAllBytes(etlPath, new byte[1024]);
        return Result(true);
    }

    private static Task<OpResult> Result(bool ok) => Task.FromResult(ok ? OpResult.Success : OpResult.Fail("模擬の失敗"));
}

internal sealed class FakeCounters : ICounterRecorder
{
    public List<string> Calls { get; } = [];
    public Task<OpResult> StartAsync(CounterSettings settings, string outputRoot) { Calls.Add("start"); return Task.FromResult(OpResult.Success); }
    public Task<OpResult> StopAsync() { Calls.Add("stop"); return Task.FromResult(OpResult.Success); }
    public Task<OpResult> SnapshotAsync(string destFile)
    {
        Calls.Add("snapshot");
        File.WriteAllBytes(destFile, new byte[16]);
        return Task.FromResult(OpResult.Success);
    }
}

internal sealed class FakeSysInfo : ISystemInfoCollector
{
    public string Collect() => "テスト用の調査情報";
}

internal sealed class FakeRetention(bool enoughSpace) : RetentionManager(Logger.Null())
{
    public override bool HasEnoughFreeSpace(string root, long minFreeBytes, out long freeBytes)
    {
        freeBytes = enoughSpace ? long.MaxValue : 0;
        return enoughSpace;
    }
}

/// <summary>待ち時間をすぐ終える時計。Gate を使えば、待ちの終わりをテストから指示できる。</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));
    public TaskCompletionSource? Gate { get; set; }
    public List<TimeSpan> Delays { get; } = [];

    public async Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        Delays.Add(delay);
        if (Gate is { } g) await g.Task.WaitAsync(ct);
    }
}

public sealed class CaptureControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeWpr _wpr = new();
    private readonly FakeCounters _counters = new();
    private readonly FakeClock _clock = new();
    private readonly List<Notice> _notices = [];

    private CaptureController Create(bool enoughSpace = true, Action<AppSettings>? configure = null)
    {
        var s = new AppSettings();
        s.Storage.OutputDirectory = _dir.Path;
        s.Counters.Enabled = false;
        configure?.Invoke(s);
        var c = new CaptureController(s, _wpr, _counters, new FakeRetention(enoughSpace), new FakeSysInfo(), _clock, Logger.Null());
        c.Notified += n => { lock (_notices) _notices.Add(n); };
        return c;
    }

    private string[] CaptureDirs() => Directory.GetDirectories(_dir.Path).Where(d => !d.EndsWith("counters")).ToArray();

    [Fact]
    public async Task 起動時に前回の記録を片付けてから監視を始める()
    {
        using var c = Create();
        await c.InitializeAsync();
        Assert.Equal("cancel", _wpr.Calls[0]);
        Assert.Equal("start-memory:input-latency", _wpr.Calls[1]);
        Assert.Equal(CaptureState.Monitoring, c.State);
    }

    [Fact]
    public async Task 監視していないときの保存は断る()
    {
        using var c = Create(configure: s => s.General.StartMonitoringOnLaunch = false);
        await c.InitializeAsync();
        await c.SaveNowAsync(Trigger.Hotkey);
        Assert.Contains(_notices, n => n.Id == "M-03");
        Assert.Empty(CaptureDirs());
    }

    [Fact]
    public async Task 保存するとetlと調査情報とmetaを残し監視を再開する()
    {
        using var c = Create();
        await c.InitializeAsync();
        string? memoFolder = null;
        c.SaveStarted += (f, _) => memoFolder = f;

        await c.SaveNowAsync(Trigger.Hotkey);

        var dir = Assert.Single(CaptureDirs());
        Assert.EndsWith("_hotkey", dir);
        Assert.True(File.Exists(Path.Combine(dir, "trace.etl")));
        Assert.True(File.Exists(Path.Combine(dir, "sysinfo.txt")));
        var meta = CaptureFolder.TryReadOwnMeta(dir)!;
        Assert.Equal("ok", meta.Result);
        Assert.Equal("hotkey", meta.Trigger);
        Assert.Equal(dir, memoFolder);                    // メモ入力のきっかけを出す
        Assert.Equal(CaptureState.Monitoring, c.State);   // 監視を再開している
        Assert.Equal(["cancel", "start-memory:input-latency", "marker:PerfSnap_hotkey", "stop", "start-memory:input-latency"], _wpr.Calls);
        Assert.Contains(_notices, n => n.Id == "M-02" && n.Folder == dir);
    }

    [Fact]
    public async Task 自動検知は待ってから保存し歯止めで二回目を止める()
    {
        using var c = Create();
        await c.InitializeAsync();
        var stall = new StallInfo(StallKind.System, 2140, _clock.Now, null);

        await c.OnStallAsync(stall);
        await c.OnStallAsync(stall); // 10 分以内なので止まる

        var dir = Assert.Single(CaptureDirs());
        Assert.Contains(TimeSpan.FromSeconds(5), _clock.Delays);
        var meta = CaptureFolder.TryReadOwnMeta(dir)!;
        Assert.Equal("auto", meta.Trigger);
        Assert.Equal(2140, meta.Stall!.LateMs);

        _clock.Now = _clock.Now.AddMinutes(11);
        await c.OnStallAsync(stall with { DetectedAt = _clock.Now });
        Assert.Equal(2, CaptureDirs().Length);
    }

    [Fact]
    public async Task 保存に失敗したら片付けて監視を再開し失敗を記録する()
    {
        using var c = Create();
        await c.InitializeAsync();
        _wpr.FailStop = true;

        await c.SaveNowAsync(Trigger.Manual);

        var dir = Assert.Single(CaptureDirs());
        Assert.Equal("failed", CaptureFolder.TryReadOwnMeta(dir)!.Result);
        Assert.Contains("cancel", _wpr.Calls.Skip(2));
        Assert.Equal(CaptureState.Monitoring, c.State);
        Assert.Contains(_notices, n => n.Id == "M-06");
    }

    [Fact]
    public async Task 空き容量が足りなければ保存しない()
    {
        using var c = Create(enoughSpace: false);
        await c.InitializeAsync();
        await c.SaveNowAsync(Trigger.Manual);
        Assert.Empty(CaptureDirs());
        Assert.Contains(_notices, n => n.Id == "M-05");
        Assert.Equal(CaptureState.Monitoring, c.State);
    }

    [Fact]
    public async Task 監視を開始できなければ待機中に戻して知らせる()
    {
        _wpr.FailStart = true;
        using var c = Create();
        await c.InitializeAsync();
        Assert.Equal(CaptureState.Idle, c.State);
        Assert.Contains(_notices, n => n.Id == "M-06");
    }

    [Fact]
    public async Task 時間指定の記録は終わったら保存して監視に戻る()
    {
        using var c = Create();
        await c.InitializeAsync();
        _clock.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.Notified += n => { if (n.Id == "M-02") saved.TrySetResult(); };

        await c.StartTimedCaptureAsync(30);
        Assert.Equal(CaptureState.TimedCapturing, c.State);
        Assert.Contains("start-file:input-latency", _wpr.Calls);

        _clock.Gate.SetResult(); // 30 秒経過
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var dir = Assert.Single(CaptureDirs());
        Assert.EndsWith("_timed", dir);
        Assert.Equal("ok", CaptureFolder.TryReadOwnMeta(dir)!.Result);
        Assert.Equal(CaptureState.Monitoring, c.State);
    }

    [Fact]
    public async Task 時間指定の記録中に今すぐ保存すると早めに止めて保存する()
    {
        using var c = Create();
        await c.InitializeAsync();
        _clock.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await c.StartTimedCaptureAsync(300);
        await c.SaveNowAsync(Trigger.Hotkey);

        var dir = Assert.Single(CaptureDirs());
        Assert.Equal("ok", CaptureFolder.TryReadOwnMeta(dir)!.Result);
        Assert.Equal(CaptureState.Monitoring, c.State);
    }

    [Fact]
    public async Task 軽量監視が動いていれば保存フォルダにカウンターも残す()
    {
        using var c = Create(configure: s => s.Counters.Enabled = true);
        await c.InitializeAsync();
        await c.SaveNowAsync(Trigger.Manual);

        var dir = Assert.Single(CaptureDirs());
        Assert.True(File.Exists(Path.Combine(dir, "counters.blg")));
        Assert.Contains("counters.blg", CaptureFolder.TryReadOwnMeta(dir)!.Files);
        Assert.Equal(["start", "snapshot"], _counters.Calls);
    }

    [Fact]
    public async Task 終了時は記録を止める()
    {
        using var c = Create();
        await c.InitializeAsync();
        await c.ShutdownAsync(fast: true);
        Assert.Equal("cancel", _wpr.Calls[^1]);
        Assert.Equal(CaptureState.Idle, c.State);
    }

    public void Dispose() => _dir.Dispose();
}

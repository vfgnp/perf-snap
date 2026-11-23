using PerfSnap.Storage;

namespace PerfSnap.Core;

/// <summary>
/// 検知 A の判定（詳細設計 §6.1）。スレッドから切り離して、テストできるようにしている。
/// </summary>
public sealed class StallJudge
{
    public static readonly TimeSpan ResumeLimit = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PowerChangeQuietPeriod = TimeSpan.FromSeconds(10);

    private DateTimeOffset _suppressUntil = DateTimeOffset.MinValue;

    /// <summary>スリープ・復帰などの電源の変化を受けたとき呼ぶ。前後しばらくは判定しない。</summary>
    public void NotifyPowerChange(DateTimeOffset now) => _suppressUntil = now + PowerChangeQuietPeriod;

    /// <summary>遅れが検知に当たるか。当たらなければ null。</summary>
    public long? Evaluate(TimeSpan late, int thresholdMs, DateTimeOffset now)
    {
        if (now < _suppressUntil) return null;
        if (late >= ResumeLimit) return null; // スリープ・休止からの復帰とみなす
        if (late.TotalMilliseconds < thresholdMs) return null;
        return (long)late.TotalMilliseconds;
    }
}

/// <summary>自動保存の歯止め（詳細設計 §6.3）。</summary>
public sealed class AutoSaveGate
{
    private DateTimeOffset? _lastSave;
    private DateOnly _countDate;
    private int _countToday;

    public enum Decision { Pass, CoolingDown, DailyLimit }

    public Decision Check(DetectionSettings s, DateTimeOffset now)
    {
        if (_lastSave is { } last && now - last < TimeSpan.FromMinutes(s.CooldownMinutes)) return Decision.CoolingDown;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var count = today == _countDate ? _countToday : 0;
        if (count >= s.MaxAutoSavesPerDay) return Decision.DailyLimit;
        return Decision.Pass;
    }

    /// <summary>自動保存を実行したことを記録する。</summary>
    public void Record(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (today != _countDate)
        {
            _countDate = today;
            _countToday = 0;
        }
        _countToday++;
        _lastSave = now;
    }
}

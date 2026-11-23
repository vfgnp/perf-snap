using PerfSnap.Core;
using PerfSnap.Platform;
using PerfSnap.Recording;
using PerfSnap.Storage;

namespace PerfSnap.Tests;

public class StallJudgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void しきい値未満の遅れは検知しない()
    {
        var j = new StallJudge();
        Assert.Null(j.Evaluate(TimeSpan.FromMilliseconds(1499), 1500, T0));
    }

    [Fact]
    public void しきい値以上の遅れを検知し遅れをミリ秒で返す()
    {
        var j = new StallJudge();
        Assert.Equal(2140, j.Evaluate(TimeSpan.FromMilliseconds(2140), 1500, T0));
    }

    [Fact]
    public void 六十秒以上の遅れはスリープからの復帰とみなして無視する()
    {
        var j = new StallJudge();
        Assert.Null(j.Evaluate(TimeSpan.FromSeconds(60), 1500, T0));
    }

    [Fact]
    public void 電源の変化から十秒間は判定しない()
    {
        var j = new StallJudge();
        j.NotifyPowerChange(T0);
        Assert.Null(j.Evaluate(TimeSpan.FromSeconds(3), 1500, T0.AddSeconds(9)));
        Assert.NotNull(j.Evaluate(TimeSpan.FromSeconds(3), 1500, T0.AddSeconds(11)));
    }
}

public class AutoSaveGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));
    private static DetectionSettings S(int cooldown = 10, int max = 20) => new() { CooldownMinutes = cooldown, MaxAutoSavesPerDay = max };

    [Fact]
    public void 初回は通す()
    {
        Assert.Equal(AutoSaveGate.Decision.Pass, new AutoSaveGate().Check(S(), T0));
    }

    [Fact]
    public void 間隔が空くまでは止める()
    {
        var g = new AutoSaveGate();
        g.Record(T0);
        Assert.Equal(AutoSaveGate.Decision.CoolingDown, g.Check(S(), T0.AddMinutes(9)));
        Assert.Equal(AutoSaveGate.Decision.Pass, g.Check(S(), T0.AddMinutes(10)));
    }

    [Fact]
    public void 一日の上限で止め翌日は通す()
    {
        var g = new AutoSaveGate();
        g.Record(T0);
        g.Record(T0.AddMinutes(1));
        Assert.Equal(AutoSaveGate.Decision.DailyLimit, g.Check(S(cooldown: 1, max: 2), T0.AddMinutes(5)));
        Assert.Equal(AutoSaveGate.Decision.Pass, g.Check(S(cooldown: 1, max: 2), T0.AddDays(1)));
    }
}

public class HotkeyParserTests
{
    [Theory]
    [InlineData(new[] { "Ctrl", "Alt", "Shift" }, "P", true)]
    [InlineData(new[] { "ctrl" }, "F12", true)]
    [InlineData(new string[0], "P", false)]        // 修飾キーなしは不可
    [InlineData(new[] { "Ctrl" }, "Banana", false)]
    [InlineData(new[] { "Hyper" }, "P", false)]
    public void ホットキーの表記を解釈する(string[] mods, string key, bool expected)
    {
        var ok = HotkeyParser.TryParse(new HotkeySettings { Modifiers = mods.ToList(), Key = key }, out _, out _);
        Assert.Equal(expected, ok);
    }
}

public class CommandLineTests
{
    [Fact]
    public void wprの開始コマンドは同梱プロファイルを指定しinstancenameを最後に付ける()
    {
        var args = WprRecorder.StartArgs(Profiles.InputLatency, @"C:\a\PerfSnap.wprp", fileMode: false);
        Assert.Equal(["-start", @"C:\a\PerfSnap.wprp!PerfSnap_InputLatency", "-instancename", "PerfSnap"], args);
    }

    [Fact]
    public void wprのファイルモードはfilemodeを付けinstancenameは最後のまま()
    {
        var args = WprRecorder.StartArgs(Profiles.Detailed, @"C:\a\PerfSnap.wprp", fileMode: true);
        Assert.Equal("-filemode", args[^3]);
        Assert.Equal(["-instancename", "PerfSnap"], args[^2..]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void wprの保存はシンボル生成を省き圧縮は設定に従う(bool compress)
    {
        var args = WprRecorder.StopArgs(@"C:\out\trace.etl", "PerfSnap hotkey", compress);
        List<string> expected = ["-stop", @"C:\out\trace.etl", "PerfSnap hotkey", "-skipPdbGen"];
        if (compress) expected.Add("-compress");
        expected.AddRange(["-instancename", "PerfSnap"]);
        Assert.Equal(expected, args);
    }

    [Fact]
    public void 同梱プロファイルはバッファ数を埋め込んだ正しいXMLになる()
    {
        var xml = System.Xml.Linq.XDocument.Parse(WprpFile.Render(256));
        var buffers = xml.Descendants("Buffers").Select(b => int.Parse(b.Attribute("Value")!.Value)).ToList();
        Assert.Equal([192, 64], buffers); // 合計 256 MB（システム 75%）
        var ids = xml.Descendants("Profile").Select(p => p.Attribute("Id")!.Value).ToList();
        foreach (var p in Profiles.All)
        {
            Assert.Contains($"{p.WprpName}.Verbose.Memory", ids);
            Assert.Contains($"{p.WprpName}.Verbose.File", ids);
        }
    }

    [Fact]
    public void バッファの合計は範囲内に収める()
    {
        Assert.Equal((48, 16), WprpFile.SplitBuffers(10));      // 下限 64 MB
        Assert.Equal((1536, 512), WprpFile.SplitBuffers(9999)); // 上限 2048 MB
    }

    [Fact]
    public void logmanの作成コマンドは間隔を時分秒で渡す()
    {
        var args = CounterRecorder.CreateArgs(new CounterSettings { IntervalSeconds = 75, MaxSizeMB = 256 }, @"C:\x\counters.txt", @"D:\out");
        Assert.Contains("00:01:15", args);
        Assert.Equal("256", args[args.IndexOf("-max") + 1]);
        Assert.Equal(@"D:\out\counters\PerfSnap_Light", args[args.IndexOf("-o") + 1]);
        Assert.Equal("bincirc", args[args.IndexOf("-f") + 1]);
    }

    [Fact]
    public void 自動起動のXMLは電源と時間の制限を外し最上位の特権で実行する()
    {
        var xml = AutoStartManager.BuildTaskXml(@"C:\Tools\Perf&Snap\PerfSnap.exe", @"PC\user");
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains(@"C:\Tools\Perf&amp;Snap\PerfSnap.exe", xml); // XML として正しくエスケープする
        System.Xml.Linq.XDocument.Parse(xml.Replace("encoding=\"UTF-16\"", ""));
    }
}

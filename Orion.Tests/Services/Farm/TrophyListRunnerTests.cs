using Orion.Services.Farm;
using Xunit;

namespace Orion.Tests.Services.Farm;

public class TrophyListRunnerTests
{
    private const uint Hide = 5001;
    private const uint Fang = 5002;
    private const uint Wing = 5003;

    /// <summary>A Trophy Hunt that runs until the test ends it, and refuses the items it is told to.</summary>
    private sealed class FakeHunt : ITrophyHunt
    {
        public readonly List<(uint ItemId, int Target)> Started = [];
        public readonly HashSet<uint> Refuses = [];
        public bool IsRunning { get; set; }
        public string StatusLine => "hunting";
        public bool Stopped { get; private set; }

        public string? Start(uint itemId, string name, int targetCount)
        {
            if (Refuses.Contains(itemId))
                return "no mob is known to drop it";
            Started.Add((itemId, targetCount));
            IsRunning = true;
            return null;
        }

        public void Stop(string reason)
        {
            IsRunning = false;
            Stopped = true;
        }
    }

    private readonly FakeHunt _hunt = new();
    private readonly Dictionary<uint, int> _bag = new();
    private readonly List<string> _said = [];
    private readonly List<bool> _ended = [];
    private bool _dead;
    private bool _otherBusy;

    private TrophyListRunner Runner()
    {
        var runner = new TrophyListRunner(_hunt, id => _bag.GetValueOrDefault(id), () => _dead, () => _otherBusy, _said.Add);
        runner.Ended += _ended.Add;
        return runner;
    }

    private static TrophyList List(string name, params (uint Id, int Target)[] items)
    {
        var list = new TrophyList { Name = name };
        foreach (var (id, target) in items)
            list.Add(id, $"item {id}", target);
        return list;
    }

    /// <summary>The hunt in hand reached its count and stopped itself.</summary>
    private void Fill(uint itemId, int count)
    {
        _bag[itemId] = count;
        _hunt.IsRunning = false;
    }

    [Fact]
    public void Short_TakesTheHighestTargetOfAnItem_InFirstSeenOrder_WithoutWhatTheBagHolds()
    {
        _bag[Fang] = 3;
        var lists = new[] { List("a", (Hide, 5), (Fang, 3)), List("b", (Wing, 2), (Hide, 9)) };

        var want = TrophyListRunner.Short(lists, id => _bag.GetValueOrDefault(id));

        Assert.Equal([new TrophyWant(Hide, $"item {Hide}", 9), new TrophyWant(Wing, $"item {Wing}", 2)], want);
    }

    [Fact]
    public void Short_LeavesOutAListThatIsNotEnabled()
    {
        var off = List("off", (Hide, 5));
        off.Enabled = false;

        Assert.Empty(TrophyListRunner.Short([off], _ => 0));
    }

    [Fact]
    public void Begin_HuntsEachShortItemInTurn_AndEndsCompleted()
    {
        var runner = Runner();

        Assert.Null(runner.Begin([List("a", (Hide, 5), (Fang, 2))]));
        Assert.Equal(Hide, runner.CurrentItem);
        Assert.Equal("item 1 of 2", runner.Progress);

        runner.Tick(); // still hunting: nothing moves on
        Assert.Single(_hunt.Started);

        Fill(Hide, 5);
        runner.Tick();
        Assert.Equal(Fang, runner.CurrentItem);
        Assert.Equal("item 2 of 2", runner.Progress);

        Fill(Fang, 2);
        runner.Tick();

        Assert.False(runner.IsRunning);
        Assert.Equal([(Hide, 5), (Fang, 2)], _hunt.Started);
        Assert.Equal([true], _ended);
    }

    [Fact]
    public void Begin_WithNothingShort_DoesNotStart()
    {
        _bag[Hide] = 5;
        var runner = Runner();

        Assert.Equal("nothing on the list is short", runner.Begin([List("a", (Hide, 5))]));
        Assert.False(runner.IsRunning);
        Assert.Empty(_ended);
    }

    [Fact]
    public void Begin_IsRefused_WhileAnotherRunIsGoing()
    {
        _otherBusy = true;

        Assert.NotNull(Runner().Begin([List("a", (Hide, 5))]));
        Assert.Empty(_hunt.Started);
    }

    [Fact]
    public void AnItemThatCannotBeStarted_IsSkipped_AndTheNextIsHunted()
    {
        _hunt.Refuses.Add(Hide);
        var runner = Runner();

        Assert.Null(runner.Begin([List("a", (Hide, 5), (Fang, 2))]));

        Assert.Equal(Fang, runner.CurrentItem);
        Assert.Contains(_said, line => line.Contains($"skipping item {Hide}"));
    }

    [Fact]
    public void WhenNoItemCanBeStarted_BeginSaysSo_AndTheRunIsNotCompleted()
    {
        _hunt.Refuses.Add(Hide);
        var runner = Runner();

        Assert.NotNull(runner.Begin([List("a", (Hide, 5))]));

        Assert.False(runner.IsRunning);
        Assert.Equal([false], _ended);
    }

    [Fact]
    public void AHuntThatStopsShort_IsSkipped_AndTheListCarriesOn()
    {
        var runner = Runner();
        runner.Begin([List("a", (Hide, 5), (Fang, 2))]);

        _hunt.IsRunning = false; // left the zone, or the travel failed: the bag is still short
        runner.Tick();

        Assert.Equal(Fang, runner.CurrentItem);
        Fill(Fang, 2);
        runner.Tick();
        Assert.Equal([true], _ended); // one filled, one skipped
    }

    [Fact]
    public void TheCharacterDying_EndsTheWholeRun()
    {
        var runner = Runner();
        runner.Begin([List("a", (Hide, 5), (Fang, 2))]);

        _dead = true;
        _hunt.IsRunning = false;
        runner.Tick();

        Assert.False(runner.IsRunning);
        Assert.Single(_hunt.Started);
        Assert.Equal([false], _ended);
    }

    [Fact]
    public void Stop_StopsTheHuntInHand_AndIsNotACompletedRun()
    {
        var runner = Runner();
        runner.Begin([List("a", (Hide, 5), (Fang, 2))]);

        runner.Stop("stopped by user");

        Assert.True(_hunt.Stopped);
        Assert.False(runner.IsRunning);
        Assert.Null(runner.CurrentItem);
        Assert.Equal([false], _ended);
    }

    [Fact]
    public void AnItemFilledWhileAnotherWasHunted_IsNotHuntedAgain()
    {
        var runner = Runner();
        runner.Begin([List("a", (Hide, 5), (Fang, 2))]);

        _bag[Fang] = 2; // the hide's mobs dropped fangs too
        Fill(Hide, 5);
        runner.Tick();

        Assert.False(runner.IsRunning);
        Assert.Single(_hunt.Started);
        Assert.Equal([true], _ended);
    }
}

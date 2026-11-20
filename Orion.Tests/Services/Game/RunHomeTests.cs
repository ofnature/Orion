using Orion.Services.Game;

namespace Orion.Tests.Services.Game;

public class RunHomeTests
{
    /// <summary>The commands are load-bearing strings read out of Lifestream 2.5.4.16 and the game's general actions (via Demeter).</summary>
    [Fact]
    public void Every_destination_maps_to_the_command_that_goes_there()
    {
        Assert.Null(RunHomeCommands.For(RunHome.Stay));
        Assert.Equal("/generalaction Return", RunHomeCommands.For(RunHome.Return));
        Assert.Equal("/li inn", RunHomeCommands.For(RunHome.Inn));
        Assert.Equal("/li auto", RunHomeCommands.For(RunHome.Estate));
        Assert.Equal("/li fc", RunHomeCommands.For(RunHome.FreeCompany));
        Assert.Equal("/li home", RunHomeCommands.For(RunHome.Private));
        Assert.Equal("/li apt", RunHomeCommands.For(RunHome.Apartment));
        Assert.All(Enum.GetValues<RunHome>(), h => Assert.NotEmpty(RunHomeCommands.Label(h)));
    }

    [Fact]
    public void Only_staying_and_Return_do_without_Lifestream()
    {
        Assert.False(RunHomeCommands.NeedsLifestream(RunHome.Stay));
        Assert.False(RunHomeCommands.NeedsLifestream(RunHome.Return));
        Assert.All(
            new[] { RunHome.Inn, RunHome.Estate, RunHome.FreeCompany, RunHome.Private, RunHome.Apartment },
            h => Assert.True(RunHomeCommands.NeedsLifestream(h)));
    }
}

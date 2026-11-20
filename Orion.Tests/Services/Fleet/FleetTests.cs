using Orion.Services.Fleet;

namespace Orion.Tests.Services.Fleet;

/// <summary>Odysseus's fleet tests, on Orion's status record.</summary>
public class FleetTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static FleetStatus Status(string name, string statusLine = "Fighting Little Ladybug") => new()
    {
        SenderId = $"{name}@Zodiark", Character = name, World = "Zodiark", Running = true, Mode = "Hunt Log",
        Goal = "Little Ladybug", Progress = "1 / 3", StatusLine = statusLine, Zone = "Middle La Noscea",
    };

    [Fact]
    public void Status_round_trips_and_ignores_unknown_fields()
    {
        var json = Status("Aletheia").ToJson();
        var back = FleetStatus.FromJson(json)!;
        Assert.Equal("Aletheia@Zodiark", back.SenderId);
        Assert.True(back.Running);
        Assert.Equal("Hunt Log", back.Mode);
        Assert.Equal("1 / 3", back.Progress);

        // A newer peer adds a field we do not know: still parses (extend-only rule).
        var newer = json.TrimEnd('}') + ",\"FutureField\":42}";
        Assert.NotNull(FleetStatus.FromJson(newer));
    }

    [Fact]
    public void Garbage_and_anonymous_frames_are_dropped_not_thrown()
    {
        Assert.Null(FleetStatus.FromJson("not json"));
        Assert.Null(FleetStatus.FromJson("{}"));
        Assert.Null(FleetStatus.FromJson("{\"SenderId\":\"\"}"));
    }

    [Fact]
    public void Roster_judges_liveness_by_our_clock_at_receipt()
    {
        var roster = new FleetRoster();
        roster.Update(Status("Kore"), T0);
        roster.Update(Status("Nyx"), T0.AddSeconds(-30));
        roster.Update(Status("Eos"), T0.AddMinutes(-4));

        var peers = roster.Peers(T0, staleAfter: TimeSpan.FromSeconds(10));
        Assert.Equal(3, peers.Count);
        Assert.Equal(PeerLiveness.Online, peers.Single(p => p.Status.Character == "Kore").Liveness);
        Assert.Equal(PeerLiveness.Stale, peers.Single(p => p.Status.Character == "Nyx").Liveness);
        Assert.Equal(PeerLiveness.Stale, peers.Single(p => p.Status.Character == "Eos").Liveness);
    }

    [Fact]
    public void Peers_gone_long_enough_are_dropped_from_the_list()
    {
        var roster = new FleetRoster();
        roster.Update(Status("Selene"), T0.AddMinutes(-6));
        Assert.Empty(roster.Peers(T0, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, roster.Count);
    }

    [Fact]
    public void A_newer_frame_from_the_same_sender_replaces_the_old_one()
    {
        var roster = new FleetRoster();
        roster.Update(Status("Kore", "Going to Little Ladybug"), T0.AddSeconds(-5));
        roster.Update(Status("Kore", "Fighting Little Ladybug"), T0);
        var peer = Assert.Single(roster.Peers(T0, TimeSpan.FromSeconds(10)));
        Assert.Equal("Fighting Little Ladybug", peer.Status.StatusLine);
        Assert.Equal(TimeSpan.Zero, peer.Age);
    }

    [Fact]
    public void Rows_come_back_sorted_by_character()
    {
        var roster = new FleetRoster();
        roster.Update(Status("Nyx"), T0);
        roster.Update(Status("Aletheia"), T0);
        roster.Update(Status("Kore"), T0);
        Assert.Equal(new[] { "Aletheia", "Kore", "Nyx" }, roster.Peers(T0, TimeSpan.FromSeconds(10)).Select(p => p.Status.Character));
    }
}

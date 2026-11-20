using System.Numerics;
using Orion.Services.Travel;

namespace Orion.Tests;

public class TravellerTests
{
    private static readonly Vector3 Node = new(100, 0, 100);

    private static TravelStatus Run(Traveller travel, FakeTravelWorld world, int ticks = 120, double step = 0.5)
    {
        for (var i = 0; i < ticks && travel.Tick() == TravelStatus.Running; i++)
            world.Advance(step);
        return travel.Status;
    }

    /// <summary>Tick until the fake's move count reaches <paramref name="moves"/>.</summary>
    private static void TickUntilMoves(Traveller travel, FakeTravelWorld world, int moves, int max = 40)
    {
        for (var i = 0; i < max && world.MoveCount < moves && travel.Status == TravelStatus.Running; i++)
        {
            travel.Tick();
            world.Advance(0.5);
        }
    }

    [Fact]
    public void Another_zone_is_reached_by_Lifestream_to_the_nearest_attuned_aetheryte_then_Ariadne()
    {
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnMove = true };
        world.Attuned[1] = (1187, new Vector3(900, 0, 900));
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));

        Assert.Contains("Teleport 2", world.Calls);
        Assert.DoesNotContain("Teleport 1", world.Calls);
        Assert.True(world.Calls.IndexOf("Teleport 2") < world.Calls.FindIndex(c => c.StartsWith("Move", StringComparison.Ordinal)));
        Assert.Equal(Node, world.LastMoveTarget);
    }

    [Fact]
    public void Far_in_the_same_zone_it_teleports_to_a_nearer_aetheryte_unless_told_not_to()
    {
        var far = new Vector3(1000, 0, 1000);
        var world = new FakeTravelWorld { TerritoryId = 1187, ArriveOnMove = true };
        world.Attuned[2] = (1187, new Vector3(990, 0, 990));

        var travel = new Traveller(world);
        travel.Begin(1187, far, fly: false, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.Contains("Teleport 2", world.Calls);

        var walker = new FakeTravelWorld { TerritoryId = 1187, ArriveOnMove = true };
        walker.Attuned[2] = (1187, new Vector3(990, 0, 990));
        var walk = new Traveller(walker);
        walk.Begin(1187, far, fly: false, land: false, teleport: false);
        Assert.Equal(TravelStatus.Done, Run(walk, walker));
        Assert.DoesNotContain(walker.Calls, c => c.StartsWith("Teleport", StringComparison.Ordinal));
        Assert.Equal(far, walker.LastMoveTarget);
    }

    [Fact]
    public void A_zone_with_nothing_attuned_fails_and_says_so()
    {
        var world = new FakeTravelWorld { TerritoryId = 129 };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: true);
        Assert.Equal(TravelStatus.Failed, Run(travel, world));
        Assert.Contains("no aetheryte", travel.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Teleport", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zone_with_nothing_attuned_is_a_route_failure_not_the_points()
    {
        var world = new FakeTravelWorld { TerritoryId = 129 };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: true);
        Run(travel, world);
        Assert.Equal(TravelFailure.Route, travel.Failure);
    }

    // Limsa's Upper Decks (128) has no crystal: the Lower Decks crystal (8, in 129), then the
    // aethernet to the Upper Decks shard, then the walk.
    private static readonly Vector3 UpperDecksSpot = new(-20, 40, 160);

    private static FakeTravelWorld Limsa(uint startIn = 1187)
    {
        var world = new FakeTravelWorld { TerritoryId = startIn, ArriveOnMove = true, CanMountHere = false };
        world.Attuned[8] = (129, new Vector3(-84, 20, 0));
        world.Districts[128] = (8, "The Aftcastle", new Vector3(16, 40, 70));
        return world;
    }

    [Fact]
    public void A_district_with_no_crystal_is_the_city_crystal_then_the_aethernet_then_the_walk()
    {
        var world = Limsa();
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        Assert.Equal(TravelStatus.Done, Run(travel, world));

        var teleport = world.Calls.IndexOf("Teleport 8");
        var hop = world.Calls.IndexOf("Aethernet The Aftcastle");
        var move = world.Calls.FindIndex(c => c.StartsWith("Move", StringComparison.Ordinal));
        Assert.True(teleport >= 0 && teleport < hop && hop < move, string.Join(" | ", world.Calls));
        Assert.Equal(UpperDecksSpot, world.LastMoveTarget);
        Assert.Equal(128u, world.TerritoryId);
    }

    [Fact]
    public void After_the_city_crystal_it_hops_rather_than_teleporting_there_again()
    {
        // Limsa, 2026-11-14: routing again after the landing heard "not at a shard" and teleported
        // to the same crystal three times.
        var world = Limsa();
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        for (var i = 0; i < 20 && world.TerritoryId != 129; i++) { travel.Tick(); world.Advance(0.5); }
        world.AtShard = false;   // Lifestream has not caught up with the landing yet
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.Equal(1, world.Calls.Count(c => c == "Teleport 8"));
        Assert.Contains("Aethernet The Aftcastle", world.Calls);
    }

    [Fact]
    public void A_hop_refused_off_the_crystal_walks_up_to_it_and_asks_again()
    {
        var world = Limsa();
        world.TeleportLandOffset = new Vector3(8, 0, 0);
        world.HopNeedsWithin = 3f;
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        var walk = world.Calls.IndexOf("Move -84,20,0 fly=False");
        Assert.True(walk >= 0, string.Join(" | ", world.Calls));
        Assert.True(walk < world.Calls.LastIndexOf("Aethernet The Aftcastle"));
        Assert.Equal(128u, world.TerritoryId);
        Assert.Contains(world.Logged, m => m.Contains("walking up to it"));
    }

    [Fact]
    public void Already_at_a_shard_in_the_same_city_it_only_hops()
    {
        var world = Limsa(startIn: 129);
        world.AtShard = true;
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Teleport", StringComparison.Ordinal));
        Assert.Contains("Aethernet The Aftcastle", world.Calls);
    }

    [Fact]
    public void A_hop_that_goes_nowhere_is_asked_again_by_name_then_fails_as_a_route_failure()
    {
        var world = Limsa();
        world.HopGoesNowhere = true;
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        Assert.Equal(TravelStatus.Failed, Run(travel, world));
        Assert.Contains("Aethernet The Aftcastle by name", world.Calls);
        Assert.Equal(TravelFailure.Route, travel.Failure);
        Assert.Contains("never started", travel.FailReason);
    }

    [Fact]
    public void A_refused_hop_is_a_route_failure()
    {
        var world = Limsa();
        world.HopAccepted = false;
        var travel = new Traveller(world);

        travel.Begin(128, UpperDecksSpot, fly: true, land: true);
        Assert.Equal(TravelStatus.Failed, Run(travel, world));
        Assert.Equal(TravelFailure.Route, travel.Failure);
        Assert.Contains("refused", travel.FailReason);
    }

    [Fact]
    public void The_aetheryte_list_is_warmed_before_the_teleport_is_asked()
    {
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnMove = true };
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Run(travel, world);
        Assert.True(world.Calls.IndexOf("RefreshAetherytes") < world.Calls.IndexOf("Teleport 2"));
    }

    [Fact]
    public void A_swallowed_teleport_is_asked_for_again()
    {
        // The gather runner switches job and then teleports; the cast falls into the action lock
        // and Lifestream's "yes" goes nowhere.
        var world = new FakeTravelWorld { TerritoryId = 129, ArriveOnTeleport = false, ArriveOnMove = true };
        world.Attuned[2] = (1187, new Vector3(120, 0, 120));
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        for (var i = 0; i < 20 && world.Calls.Count(c => c == "Teleport 2") < 2; i++) { travel.Tick(); world.Advance(0.5); }

        Assert.Equal(2, world.Calls.Count(c => c == "Teleport 2"));
        Assert.Contains(world.Logged, m => m.Contains("never started"));
    }

    [Fact]
    public void Where_the_currents_are_done_Ariadne_is_asked_to_fly()
    {
        var world = new FakeTravelWorld { CanFlyHere = true, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));

        Assert.True(world.LastMoveFly);
        Assert.DoesNotContain("Mount", world.Calls);   // Ariadne calls the mount for a flight
    }

    [Fact]
    public void Without_currents_a_long_ride_is_mounted_for_and_walked()
    {
        var world = new FakeTravelWorld { CanFlyHere = false, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.Contains("Mount", world.Calls);
        Assert.False(world.LastMoveFly);
    }

    [Fact]
    public void A_short_leg_is_walked_without_mounting()
    {
        var world = new FakeTravelWorld { ArriveOnMove = true, PlayerPosition = Node + new Vector3(10, 0, 0) };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: true, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.DoesNotContain("Mount", world.Calls);
    }

    [Fact]
    public void A_stop_distance_is_handed_to_Ariadne_as_its_range()
    {
        var world = new FakeTravelWorld { ArriveOnMove = true, PlayerPosition = Node + new Vector3(10, 0, 0) };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false, stopDistance: Traveller.DefaultStopDistance);
        Run(travel, world);
        Assert.Equal(Traveller.DefaultStopDistance, world.LastMoveRange);
    }

    [Fact]
    public void The_travel_waits_for_Ariadne_to_finish()
    {
        var world = new FakeTravelWorld { PlayerPosition = Node + new Vector3(20, 0, 0) };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        TickUntilMoves(travel, world, 1);

        for (var i = 0; i < 20; i++) { travel.Tick(); world.Advance(0.5); }
        Assert.Equal(TravelStatus.Running, travel.Status);
        Assert.Equal(1, world.MoveCount);   // one ask; Ariadne does its own re-pathing

        world.FinishMove("goal reached", Node);
        Assert.Equal(TravelStatus.Done, Run(travel, world, ticks: 4));
    }

    [Fact]
    public void Closest_reachable_point_near_the_node_counts_as_there()
    {
        var world = new FakeTravelWorld { PlayerPosition = Node + new Vector3(20, 0, 0) };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        TickUntilMoves(travel, world, 1);

        world.FinishMove("closest reachable point, 3.2y short", Node + new Vector3(3.2f, 0, 0));
        Assert.Equal(TravelStatus.Done, Run(travel, world, ticks: 4));
    }

    [Fact]
    public void Stuck_is_asked_again_then_flown_then_given_up_with_Ariadnes_words()
    {
        var world = new FakeTravelWorld { CanFlyHere = true, PlayerPosition = Node + new Vector3(20, 0, 0) };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);

        TickUntilMoves(travel, world, 1);
        world.FinishMove("stuck (18y short)");
        TickUntilMoves(travel, world, 2);
        Assert.False(world.LastMoveFly);

        world.FinishMove("stuck (18y short)");
        TickUntilMoves(travel, world, 3);
        Assert.True(world.LastMoveFly);   // the air, last
        Assert.Contains(world.Logged, m => m.Contains("flying this leg"));

        world.FinishMove("no path (startOffSurface)");
        Assert.Equal(TravelStatus.Failed, Run(travel, world, ticks: 6));
        Assert.Contains("no path (startOffSurface)", travel.FailReason);
        Assert.Equal(TravelFailure.Point, travel.Failure);   // the point's fault, not the route's
    }

    [Fact]
    public void Keys_pressed_by_the_player_end_the_travel()
    {
        var world = new FakeTravelWorld { PlayerPosition = Node + new Vector3(20, 0, 0) };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        TickUntilMoves(travel, world, 1);

        world.FinishMove("stopped");
        Assert.Equal(TravelStatus.Failed, Run(travel, world, ticks: 4));
        Assert.Contains("stopped", travel.FailReason);
    }

    [Fact]
    public void Without_a_mesh_it_waits_then_says_Ariadne_is_missing()
    {
        var world = new FakeTravelWorld { NavReady = false, PlayerPosition = Node + new Vector3(20, 0, 0) };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);

        Assert.Equal(TravelStatus.Failed, Run(travel, world, ticks: 100));
        Assert.Contains("Ariadne", travel.FailReason);
        Assert.Equal(0, world.MoveCount);
    }

    [Fact]
    public void Landing_waits_for_both_feet_on_the_ground()
    {
        var world = new FakeTravelWorld { CanFlyHere = true, ArriveOnMove = true, HoldsMount = true };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: true, land: true);

        for (var i = 0; i < 20; i++) { travel.Tick(); world.Advance(0.5); }
        Assert.Equal(TravelStatus.Running, travel.Status);   // still coming down
        Assert.Contains("Dismount", world.Calls);

        world.IsMounted = false;
        world.IsInFlight = false;
        Assert.Equal(TravelStatus.Done, Run(travel, world, ticks: 6));
    }

    [Fact]
    public void A_descent_that_never_lands_is_rerouted_over_a_floor_beside_the_point()
    {
        var floor = Node + new Vector3(4, 0, 0);
        // Hovering three yalms over the point, against a ledge: the dismount never touches down.
        var world = new FakeTravelWorld
        {
            CanFlyHere = true, HoldsMount = true, IsMounted = true, IsInFlight = true,
            PlayerPosition = Node + new Vector3(0, 3, 0),
            NearestReachableFn = (_, _) => floor,
        };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: true, land: true);
        TickUntilMoves(travel, world, 1);
        world.FinishMove("goal reached");   // Ariadne's flight ends over the point

        for (var i = 0; i < 30 && !world.Logged.Any(m => m.Contains("not landing")); i++) { travel.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Logged, m => m.Contains("not landing"));

        TickUntilMoves(travel, world, 2, max: 6);
        Assert.Equal(floor, world.LastMoveTarget);
        Assert.True(world.LastMoveFly);
    }

    [Fact]
    public void A_fight_on_the_way_stops_the_move_gets_off_and_asks_again_after()
    {
        var world = new FakeTravelWorld { CanFlyHere = false };
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        TickUntilMoves(travel, world, 1);
        Assert.True(world.IsMounted);   // a long ground ride

        world.InCombat = true;
        for (var i = 0; i < 400; i++) { travel.Tick(); world.Advance(0.5); }   // long past every clock
        Assert.Equal(TravelStatus.Running, travel.Status);
        Assert.Contains("Stop", world.Calls);
        Assert.Contains("Dismount", world.Calls);
        Assert.False(world.IsMounted);

        // Over: back in the saddle and the point handed to Ariadne again.
        world.InCombat = false;
        world.ArriveOnMove = true;
        Assert.Equal(TravelStatus.Done, Run(travel, world));
        Assert.Equal(2, world.MoveCount);
        Assert.Contains(world.Logged, m => m.Contains("mounting up"));
    }

    [Fact]
    public void With_Daedalus_switched_off_nobody_fights_so_nothing_holds()
    {
        var world = new FakeTravelWorld { InCombat = true, DaedalusDisabledByUser = true, ArriveOnMove = true };
        var travel = new Traveller(world);

        travel.Begin(1187, Node, fly: false, land: false);
        Assert.Equal(TravelStatus.Done, Run(travel, world));
    }

    [Fact]
    public void Cancel_stops_the_move_and_any_hop()
    {
        var world = new FakeTravelWorld();
        var travel = new Traveller(world);
        travel.Begin(1187, Node, fly: false, land: false);
        for (var i = 0; i < 6; i++) { travel.Tick(); world.Advance(0.5); }

        travel.Cancel();
        Assert.Equal(TravelStatus.Idle, travel.Status);
        Assert.Contains("Stop", world.Calls);
        Assert.Contains("AbortTravel", world.Calls);
    }
}

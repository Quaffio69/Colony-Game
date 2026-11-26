using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Net;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Real sockets with wall-clock waits (a resync must arrive within seconds): run alone, not beside heavy world
/// generation in other test classes, which starved them into timing out.</summary>
[CollectionDefinition("Network", DisableParallelization = true)]
public class NetworkCollection;

/// <summary>A host and clients over loopback TCP: lockstep keeps their worlds identical.</summary>
[Collection("Network")]
public class NetTests
{
    private static readonly GameSettings Small = new(Seed: 21, Width: 64, Depth: 64, Height: 40, Colonists: 3, Colonies: 2);

    /// <summary>Joins in the background while the host keeps polling (the welcome needs the host's attention).</summary>
    private static LockstepClient Join(LockstepHost host, string name = "Guest", string key = "")
    {
        var joining = Task.Run(() => LockstepClient.Connect("127.0.0.1", host.Port, name, key: key));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!joining.IsCompleted && clock.ElapsedMilliseconds < 10000)
        {
            host.Poll();
            Thread.Sleep(2);
        }
        return joining.GetAwaiter().GetResult();
    }

    /// <summary>One "frame": the host takes input, runs some ticks and broadcasts; the client takes the turn and catches up.</summary>
    private static void Frame(ColonySim hostSim, LockstepHost host, ColonySim clientSim, LockstepClient client, int ticks = 5)
    {
        host.Poll();
        for (int i = 0; i < ticks; i++)
        {
            hostSim.Step();
            host.AfterTick();
        }
        host.Broadcast();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Wait for this turn to arrive (loopback: a millisecond or so), then run to it.
        while (client.ConfirmedTick < hostSim.Tick && clock.ElapsedMilliseconds < 5000)
        {
            client.Poll();
            Thread.Sleep(1);
        }
        client.Advance(clientSim, 1000);
    }

    [Fact]
    public void HostAndClient_StayIdentical_WhileBothGiveOrders()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0);
        using var client = Join(host);
        Assert.Equal(1, client.Faction);
        var clientSim = client.BuildWorld();
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));

        var mine = hostSim.Colonists.First(c => c.Faction == 0).Position;
        var theirs = hostSim.Colonists.First(c => c.Faction == 1).Position;
        for (int f = 0; f < 120; f++)
        {
            if (f == 10) hostSim.Commands.Execute(new DesignateBox(mine.Offset(2, -1, -1), mine.Offset(4, 1, -1), DesignationKind.Dig));
            if (f == 20) client.Send(new DesignateBox(theirs.Offset(2, -1, -1), theirs.Offset(4, 1, -1), DesignationKind.Dig));
            if (f == 30) client.Send(new DesignatePlants(DesignationKind.Chop, 0, 0, 63, 63));
            Frame(hostSim, host, clientSim, client);
        }
        Assert.Null(client.DesyncTick);
        Assert.Equal(hostSim.Tick, clientSim.Tick);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));
        Assert.Contains(hostSim.Commands.Log, e => e.Command.Player == 1); // the client's orders reached the host, as its colony
        Assert.True(hostSim.Commands.Log.Count >= 3);
    }

    [Fact]
    public void ALateJoiner_ReplaysTheGameSoFar_AndStaysInStep()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0);
        var home = hostSim.Colonists[0].Position;
        for (int i = 0; i < 300; i++)
        {
            if (i == 50) hostSim.Commands.Execute(new DesignateBox(home.Offset(2, -2, -1), home.Offset(5, 2, -1), DesignationKind.Dig));
            if (i == 120) hostSim.Commands.Execute(new AddStockpile(home.Offset(-4, -4, -1), home.Offset(-2, -2, -1)));
            hostSim.Step();
        }
        using var client = Join(host, "Late");
        var clientSim = client.BuildWorld();
        Assert.Equal(hostSim.Tick, clientSim.Tick);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));
        for (int f = 0; f < 60; f++) Frame(hostSim, host, clientSim, client);
        Assert.Null(client.DesyncTick);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));
    }

    [Fact]
    public void ADesyncedClient_Stops_AndReloadsTheGameFromTheHost()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0);
        using var client = Join(host);
        var clientSim = client.BuildWorld();
        for (int f = 0; f < 10; f++) Frame(hostSim, host, clientSim, client);

        // Something only the client's world has: the next hash check catches it.
        clientSim.Items.Drop(clientSim.Colonists[0].Position, Items.ItemType.Stone, 7);
        for (int f = 0; f < 30 && client.DesyncTick == null; f++) Frame(hostSim, host, clientSim, client);
        Assert.NotNull(client.DesyncTick);
        long stoppedAt = clientSim.Tick;
        Frame(hostSim, host, clientSim, client);
        Assert.Equal(stoppedAt, clientSim.Tick); // it doesn't play on

        // The host sends the game again; the client swaps to it and plays on in step.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!client.ResyncReady && clock.ElapsedMilliseconds < 5000)
        {
            host.Poll();
            client.Poll();
            Thread.Sleep(1);
        }
        Assert.True(client.ResyncReady);
        clientSim = client.TakeResync();
        Assert.Null(client.DesyncTick);
        Assert.Equal(1, client.Resyncs);
        hostSim.Commands.Execute(new DesignateBox(hostSim.Colonists[0].Position.Offset(2, -1, -1), hostSim.Colonists[0].Position.Offset(4, 1, -1), DesignationKind.Dig));
        for (int f = 0; f < 60; f++) Frame(hostSim, host, clientSim, client);
        Assert.Null(client.DesyncTick);
        Assert.Equal(hostSim.Tick, clientSim.Tick);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));
    }

    [Fact]
    public void AClient_CanOnlyActForItsOwnColony()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0);
        using var client = Join(host);
        var clientSim = client.BuildWorld();
        var hostsColonist = hostSim.Colonists.First(c => c.Faction == 0);
        int before = hostsColonist.Priority(Work.Mining);
        client.Send(new SetWorkPriority(hostsColonist.Id, Work.Mining, 0) { Player = 0 }); // pretends to be the host
        for (int f = 0; f < 5; f++) Frame(hostSim, host, clientSim, client);
        Assert.Equal(before, hostsColonist.Priority(Work.Mining));
        Assert.Contains(hostSim.Commands.Log, e => e.Command is SetWorkPriority { Player: 1 });
    }

    [Fact]
    public void AFullGame_TurnsNewcomersAway()
    {
        var settings = Small with { Colonies = 1 };
        var hostSim = settings.Create();
        using var host = new LockstepHost(hostSim, settings, port: 0);
        var e = Assert.Throws<InvalidOperationException>(() => Join(host));
        Assert.Contains("full", e.Message);
    }

    [Fact]
    public void TheStateHash_SeesADifference()
    {
        var a = Small.Create();
        var b = Small.Create();
        Assert.Equal(StateHash.Compute(a), StateHash.Compute(b));
        b.Commands.Execute(new AddStockpile(new TilePos(10, 10, 0), new TilePos(40, 40, 39)));
        Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));
    }

    // ---- Coming back: colonies remember their players ------------------------------------------------------

    /// <summary>A join the host turns down: the reason it gives.</summary>
    private static string Refused(LockstepHost host, string name, string key)
    {
        var e = Assert.ThrowsAny<Exception>(() => Join(host, name, key));
        return (e is AggregateException a ? a.InnerException! : e).Message;
    }

    /// <summary>Leaves, and waits for the host to notice.</summary>
    private static void Leave(LockstepHost host, LockstepClient client, int colony)
    {
        client.Dispose();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (host.IsOnline(colony) && clock.ElapsedMilliseconds < 5000)
        {
            host.Poll();
            Thread.Sleep(2);
        }
        Assert.False(host.IsOnline(colony));
    }

    [Fact]
    public void APlayerWhoLeaves_GetsTheirOwnColonyBack_AndNobodyElseTakesIt()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0, "Hana", "host-key");
        Assert.Equal("Hana", hostSim.Factions[0].Player);
        var ann = Join(host, "Ann", "ann-key");
        Assert.Equal(1, ann.Faction);
        Assert.Equal("Ann", hostSim.Factions[1].Player);
        Assert.Equal(NetPlayer.KeyHash("ann-key"), hostSim.Factions[1].PlayerKey);
        Assert.NotEqual("ann-key", hostSim.Factions[1].PlayerKey); // only a digest is kept
        Leave(host, ann, 1);

        // Someone else can't take Ann's colony while she is away.
        Assert.Contains("held for Ann", Refused(host, "Bob", "bob-key"));

        // Ann is back (under another name, same key): her colony, and the claim follows her new name.
        using var back = Join(host, "Annie", "ann-key");
        Assert.Equal(1, back.Faction);
        var backSim = back.BuildWorld();
        Assert.Equal("Annie", backSim.Factions[1].Player);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(backSim));
    }

    [Fact]
    public void ASavedOnlineGame_HostedAgain_SeatsEachPlayerInTheirColony()
    {
        var settings = Small with { Colonies = 3 };
        var first = settings.Create();
        byte[] saved;
        using (var host = new LockstepHost(first, settings, port: 0, "Hana", "host-key"))
        {
            var ann = Join(host, "Ann", "ann-key");
            var bob = Join(host, "Bob", "bob-key");
            Assert.Equal((1, 2), (ann.Faction, bob.Faction));
            for (int i = 0; i < 50; i++) first.Step();
            saved = Snapshot.Take(first, settings); // the host saves the game
            ann.Dispose();
            bob.Dispose();
        }

        // Another evening: the save is hosted again, and the players come back in the other order.
        var again = Snapshot.Load(saved);
        Assert.Equal(("Hana", "Ann", "Bob"), (again.Factions[0].Player, again.Factions[1].Player, again.Factions[2].Player));
        using var host2 = new LockstepHost(again, settings, port: 0, "Hana", "host-key");
        using var bob2 = Join(host2, "Bob", "bob-key");
        Assert.Equal(2, bob2.Faction);
        Assert.Contains("held for Ann", Refused(host2, "Cid", "cid-key"));

        // Ann can't make it: the host frees her colony, and a newcomer takes it.
        Assert.False(host2.Free(2)); // Bob is here
        Assert.True(host2.Free(1));
        Assert.False(again.Factions[1].Claimed);
        using var cid = Join(host2, "Cid", "cid-key");
        Assert.Equal(1, cid.Faction);
        Assert.Equal("Cid", again.Factions[1].Player);
    }

    [Fact]
    public void AColonyFromBeforeKeys_GoesByName_AndTwoCopiesOnOneComputerAreTwoPlayers()
    {
        var hostSim = (Small with { Colonies = 3 }).Create();
        hostSim.Commands.Execute(new ClaimColony(2, "Old Timer", "")); // a save from before keys: a name only
        using var host = new LockstepHost(hostSim, Small with { Colonies = 3 }, port: 0, "Hana", "shared-key");
        using var old = Join(host, "old timer", "new-key");
        Assert.Equal(2, old.Faction);
        Assert.Equal(NetPlayer.KeyHash("new-key"), hostSim.Factions[2].PlayerKey); // its key from now on

        // A second copy of the game on the host's computer (the same key): a player of its own, not refused.
        using var twin = Join(host, "Twin", "shared-key");
        Assert.Equal(1, twin.Faction);
        Assert.Equal("", hostSim.Factions[1].PlayerKey);
    }

    [Fact]
    public void AGuest_CantHandOutColonies()
    {
        var hostSim = Small.Create();
        using var host = new LockstepHost(hostSim, Small, port: 0, "Hana", "host-key");
        using var client = Join(host, "Ann", "ann-key");
        var clientSim = client.BuildWorld();
        client.Send(new ClaimColony(0, "Ann", "x")); // trying to take the host's colony
        for (int f = 0; f < 20; f++) Frame(hostSim, host, clientSim, client);
        Assert.Equal("Hana", hostSim.Factions[0].Player);
        Assert.Equal("Hana", clientSim.Factions[0].Player);
        Assert.Equal(StateHash.Compute(hostSim), StateHash.Compute(clientSim));
    }
}

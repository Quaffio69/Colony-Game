using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Net;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>A host and clients over loopback TCP: lockstep keeps their worlds identical.</summary>
public class NetTests
{
    private static readonly GameSettings Small = new(Seed: 21, Width: 64, Depth: 64, Height: 40, Colonists: 3, Colonies: 2);

    /// <summary>Joins in the background while the host keeps polling (the welcome needs the host's attention).</summary>
    private static LockstepClient Join(LockstepHost host, string name = "Guest")
    {
        var joining = Task.Run(() => LockstepClient.Connect("127.0.0.1", host.Port, name));
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
}

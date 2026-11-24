using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Net;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class SnapshotTests
{
    private static readonly GameSettings Busy = new(Seed: 8, Width: 96, Depth: 96, Height: 48, Colonists: 6, Colonies: 2);

    /// <summary>Two colonies at work: digging, felling, hauling, a workshop being built, a home, walls, a roof.</summary>
    private static ColonySim BusyGame()
    {
        var sim = Busy.Create();
        var a = sim.Colonists.First(c => c.Faction == 0).Position;
        var b = sim.Colonists.First(c => c.Faction == 1).Position;
        int ga = a.Z - 1, gb = b.Z - 1;
        var q = sim.Commands;
        q.Execute(new DesignateBox(a.Offset(3, -2, -1), a.Offset(7, 2, -2), DesignationKind.Dig));
        q.Execute(new SetOrderPriority(a.Offset(3, -2, -1), a.Offset(5, 2, -2), 1)); // part of it first
        q.Execute(new DesignatePlants(DesignationKind.Chop, a.X - 20, a.Y - 20, a.X + 20, a.Y + 20));
        q.Execute(new AddStockpile(new TilePos(a.X - 6, a.Y - 6, ga - 3), new TilePos(a.X - 3, a.Y - 3, ga + 3)));
        q.Execute(new PaintRoom("carpentry", new TilePos(a.X - 6, a.Y + 3, ga - 3), new TilePos(a.X - 1, a.Y + 7, ga + 3)));
        q.Execute(new PaintRoom(Catalog.Home.Id, new TilePos(a.X + 2, a.Y + 4, ga - 3), new TilePos(a.X + 5, a.Y + 7, ga + 3)));
        q.Execute(new PlaceOutline(new TilePos(a.X + 2, a.Y + 4, ga), new TilePos(a.X + 5, a.Y + 7, ga), 0));
        q.Execute(new Covers(StructureKind.Roof, new TilePos(a.X + 2, a.Y + 4, ga), new TilePos(a.X + 5, a.Y + 7, ga), 1, false));
        q.Execute(new DesignatePlants(DesignationKind.Chop, b.X - 15, b.Y - 15, b.X + 15, b.Y + 15) { Player = 1 });
        q.Execute(new DesignateBox(b.Offset(-4, -2, -1), b.Offset(-2, 2, -1), DesignationKind.Dig) { Player = 1 });
        q.Execute(new AddStockpile(new TilePos(b.X + 3, b.Y + 3, gb - 3), new TilePos(b.X + 6, b.Y + 6, gb + 3)) { Player = 1 });
        // Trade depots on both sides (the deal comes last, so the snapshot catches its deliveries under way).
        q.Execute(new AddTradeDepot(new TilePos(a.X + 3, a.Y - 7, ga - 3), new TilePos(a.X + 5, a.Y - 5, ga + 3)));
        q.Execute(new AddTradeDepot(new TilePos(b.X - 6, b.Y + 3, gb - 3), new TilePos(b.X - 4, b.Y + 5, gb + 3)) { Player = 1 });
        for (int i = 0; i < 400; i++) sim.Step();
        // Parts in the workshop (blueprints: haulers bring logs, builders build).
        if (sim.Rooms.All.FirstOrDefault(r => r.Type.Id == "carpentry") is { } shop)
        {
            var cells = shop.Cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
            foreach (var part in new[] { shop.Type.Stations[0], Catalog.Part("input_crate"), Catalog.Part("output_crate") })
                foreach (var c in cells)
                    if (q.Execute(new PlacePart(part.Id, c, false, 1)).StartsWith("Placed")) break;
        }
        for (int i = 0; i < 400; i++) sim.Step();
        q.Execute(new ProposeTrade(1, new Trade.TradeTerms([new Trade.TradeTerm(0, "berries", 80), new Trade.TradeTerm(1, "bread", 40)])));
        q.Execute(new AcceptTrade(sim.Trades.All[0].Id) { Player = 1 });
        for (int i = 0; i < 150; i++) sim.Step();
        return sim;
    }

    [Fact]
    public void ASnapshot_LoadsIdentical_AndPlaysOnIdentically()
    {
        var sim = BusyGame();
        // The game is really busy: rooms with parts, walls planned or built, piles, colonists mid-task.
        Assert.True(sim.Rooms.All.Count >= 2, $"rooms {sim.Rooms.All.Count}");
        Assert.True(sim.Rooms.All.Sum(r => r.Parts.Count) >= 2, "parts");
        Assert.True(sim.Constructions.Count + sim.World.AllEdges().Count() > 0, "walls");
        Assert.True(sim.Items.Count > 3, $"piles {sim.Items.Count}");
        Assert.True(sim.Colonists.Count(c => c.Task != ColonistTask.None) >= 3, "busy colonists");
        var deal = sim.Trades.All[0];
        Assert.Equal(Trade.TradeState.Accepted, deal.State); // agreed, deliveries under way
        Assert.True(deal.Lines.Sum(l => l.Delivered + l.InTransit) > 0, "traders at work");
        var data = Snapshot.Take(sim, Busy);
        var loaded = Snapshot.Load(data);
        Assert.Equal(sim.Tick, loaded.Tick);
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
        Assert.Equal(sim.Factions.Count, loaded.Factions.Count);
        for (int i = 1; i <= 3000; i++)
        {
            sim.Step();
            loaded.Step();
            if (i % 250 == 0) Assert.True(StateHash.Compute(sim) == StateHash.Compute(loaded), $"diverged by tick {sim.Tick}");
        }
        Assert.True(sim.Commands.Log.Count > 5); // the original had a real game going
    }

    [Fact]
    public void ASnapshotOfASnapshot_IsTheSame()
    {
        var sim = BusyGame();
        var first = Snapshot.Take(sim, Busy);
        var loaded = Snapshot.Load(first);
        var again = Snapshot.Take(loaded, Busy);
        Assert.Equal(StateHash.Compute(Snapshot.Load(first)), StateHash.Compute(Snapshot.Load(again)));
    }

    [Fact]
    public void ASnapshot_IsSmall()
    {
        var sim = BusyGame();
        var data = Snapshot.Take(sim, Busy);
        Assert.True(data.Length < 400_000, $"{data.Length} bytes");
    }
}

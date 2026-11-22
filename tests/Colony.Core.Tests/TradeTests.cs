using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.Trade;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Trade between colonies: offers and counters in turns, binding acceptance, traders delivering to depots.</summary>
public class TradeTests
{
    /// <summary>Two colonies on flat ground: Westhold (0) round x 10, Easthold (1) round x 70, each with a stockpile and a depot.</summary>
    private static ColonySim TwoColonies(bool depots = true)
    {
        var w = new VoxelWorld(96, 32, 12);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 96; x++)
            w.Set(x, y, z, TileType.Stone);
        var sim = new ColonySim(w) { NeedsEnabled = false };
        sim.AddFaction("Easthold", new TilePos(70, 16, 4));
        for (int i = 0; i < 2; i++)
        {
            sim.AddColonist($"West {i}", new TilePos(10 + i, 16, 4), faction: 0);
            sim.AddColonist($"East {i}", new TilePos(70 + i, 16, 4), faction: 1);
        }
        var q = sim.Commands;
        q.Execute(new AddStockpile(new TilePos(4, 4, 3), new TilePos(7, 7, 3)));
        q.Execute(new AddStockpile(new TilePos(80, 4, 3), new TilePos(83, 7, 3)) { Player = 1 });
        if (depots)
        {
            q.Execute(new AddTradeDepot(new TilePos(14, 20, 3), new TilePos(16, 22, 3)));
            q.Execute(new AddTradeDepot(new TilePos(64, 20, 3), new TilePos(66, 22, 3)) { Player = 1 });
        }
        sim.Items.Drop(new TilePos(5, 5, 4), ItemType.Plank, 40);   // Westhold's planks
        sim.Items.Drop(new TilePos(81, 5, 4), ItemType.Stone, 25);  // Easthold's stone
        return sim;
    }

    private static TradeTerms Terms(params TradeTerm[] lines) => new(lines);

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    [Fact]
    public void Offers_GoInTurns_CounterThenAccept()
    {
        var sim = TwoColonies();
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 20), new TradeTerm(1, "stone", 30))));
        var deal = Assert.Single(sim.Trades.All);
        Assert.Equal(TradeState.Offered, deal.State);
        Assert.Equal(1, deal.Turn);
        Assert.Contains(sim.Events, e => e.Faction == 1 && e.Text.Contains("offers a trade"));

        // Not Westhold's turn: it can't accept its own offer.
        Assert.StartsWith("Waiting", q.Execute(new AcceptTrade(deal.Id)));
        // Easthold counters: less stone.
        q.Execute(new CounterTrade(deal.Id, Terms(new TradeTerm(0, "planks", 20), new TradeTerm(1, "stone", 15))) { Player = 1 });
        Assert.Equal(0, deal.Turn);
        Assert.Equal(1, deal.Round);
        Assert.StartsWith("Waiting", q.Execute(new AcceptTrade(deal.Id) { Player = 1 }));
        q.Execute(new AcceptTrade(deal.Id));
        Assert.Equal(TradeState.Accepted, deal.State);
        Assert.Equal(15, deal.From(1).Single().Count);
        // Binding: neither side can back out now.
        Assert.Equal("An agreed trade is binding", q.Execute(new RejectTrade(deal.Id) { Player = 1 }));
    }

    [Fact]
    public void Offers_CanBeDeclinedOrWithdrawn_AndOutsidersCantTouchThem()
    {
        var sim = TwoColonies();
        sim.AddFaction("Third");
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 5))));
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(1, "stone", 5))));
        var (first, second) = (sim.Trades.All[0], sim.Trades.All[1]);
        Assert.Equal("No such offer", q.Execute(new AcceptTrade(first.Id) { Player = 2 }));
        Assert.Equal("Offer declined", q.Execute(new RejectTrade(first.Id) { Player = 1 }));
        Assert.Equal(TradeState.Declined, first.State);
        Assert.Equal("Offer withdrawn", q.Execute(new RejectTrade(second.Id)));
        Assert.Equal(TradeState.Withdrawn, second.State);
        Assert.Equal("Choose another colony to trade with", q.Execute(new ProposeTrade(0, Terms(new TradeTerm(0, "planks", 5)))));
        Assert.Equal("An offer needs something in it", q.Execute(new ProposeTrade(1, Terms())));
    }

    [Fact]
    public void Accepting_NeedsADepotOnEachReceivingSide()
    {
        var sim = TwoColonies(depots: false);
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 5), new TradeTerm(1, "stone", 5))));
        var deal = sim.Trades.All[0];
        Assert.StartsWith("Zone a trade depot", q.Execute(new AcceptTrade(deal.Id) { Player = 1 }));
        q.Execute(new AddTradeDepot(new TilePos(64, 20, 3), new TilePos(66, 22, 3)) { Player = 1 });
        Assert.EndsWith("has no trade depot to deliver to yet", q.Execute(new AcceptTrade(deal.Id) { Player = 1 }));
        q.Execute(new AddTradeDepot(new TilePos(14, 20, 3), new TilePos(16, 22, 3)));
        q.Execute(new AcceptTrade(deal.Id) { Player = 1 });
        Assert.Equal(TradeState.Accepted, deal.State);
    }

    [Fact]
    public void Traders_CarryTheGoodsToEachOthersDepot_AndTheBuyerStoresThem()
    {
        var sim = TwoColonies();
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 30), new TradeTerm(1, "stone", 20))));
        var deal = sim.Trades.All[0];
        q.Execute(new AcceptTrade(deal.Id) { Player = 1 });

        bool sawTrader = false;
        for (int t = 0; t < 12_000 && deal.State != TradeState.Done; t++)
        {
            sim.Step();
            sawTrader |= sim.Colonists.Any(c => c.CurrentWork == Work.Trading && c.Task == ColonistTask.TradeDeliver);
        }
        Assert.True(sawTrader, "traders carried the goods");
        Assert.Equal(TradeState.Done, deal.State);
        Assert.All(deal.Lines, l => Assert.Equal(l.Count, l.Delivered));
        Assert.Contains(sim.Events, e => e.Faction == 0 && e.Text.Contains("complete"));

        // Each colony now owns what it bought (in its depot, or already hauled on to its stockpile), and the seller kept the rest.
        Run(sim, 3000);
        int Units(ItemType type, int faction) => Trades.Stores(sim, faction)[type.Index];
        Assert.Equal(30, Units(ItemType.Plank, 1));
        Assert.Equal(10, Units(ItemType.Plank, 0));
        Assert.Equal(20, Units(ItemType.Stone, 0));
        Assert.Equal(5, Units(ItemType.Stone, 1));
        // The buyer's haulers emptied its depot into its stockpile (depots keep room for deliveries).
        Assert.DoesNotContain(sim.Items.All, e => sim.Stockpiles.IsDepot(e.Key));
    }

    [Fact]
    public void Deliveries_Wait_ForGoodsTheSellerDoesntHaveYet()
    {
        var sim = TwoColonies();
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 50))));
        var deal = sim.Trades.All[0];
        q.Execute(new AcceptTrade(deal.Id) { Player = 1 });
        Run(sim, 6000);
        var line = deal.Lines[0];
        Assert.Equal(40, line.Delivered); // all it had
        Assert.Equal(TradeState.Accepted, deal.State);
        sim.Items.Drop(new TilePos(6, 6, 4), ItemType.Plank, 10); // more made
        Run(sim, 4000);
        Assert.Equal(TradeState.Done, deal.State);
    }

    [Fact]
    public void HaulersNeverStoreGoodsInADepot_AndOnlyItsOwnerUsesWhatLiesThere()
    {
        var sim = TwoColonies();
        sim.Items.Drop(new TilePos(20, 16, 4), ItemType.Log, 10); // loose, near Westhold's depot
        Run(sim, 2000);
        Assert.DoesNotContain(sim.Items.All, e => sim.Stockpiles.IsDepot(e.Key));
        var depotCell = sim.Stockpiles.DepotCells.First(c => sim.Stockpiles.OwnerOf(c) == 1);
        sim.Items.Drop(depotCell, Catalog.Item("bread"), 5);
        Assert.False(Needs.Usable(sim, depotCell, 0));
        Assert.True(Needs.Usable(sim, depotCell, 1));
        Assert.False(Trades.Sendable(sim, depotCell, Catalog.Item("bread"), 0));
    }

    [Fact]
    public void ATradersWorkPriority_SetsWhoTrades()
    {
        var sim = TwoColonies();
        foreach (var c in sim.Colonists.Where(c => c.Faction == 0)) c.SetPriority(Work.Trading, 0);
        var q = sim.Commands;
        q.Execute(new ProposeTrade(1, Terms(new TradeTerm(0, "planks", 10))));
        q.Execute(new AcceptTrade(sim.Trades.All[0].Id) { Player = 1 });
        Run(sim, 3000);
        Assert.Equal(0, sim.Trades.All[0].Lines[0].Delivered); // nobody in Westhold trades
        sim.Commands.Execute(new SetWorkPriority(sim.Colonists.First(c => c.Faction == 0).Id, Work.Trading, 1));
        Run(sim, 4000);
        Assert.Equal(TradeState.Done, sim.Trades.All[0].State);
    }
}

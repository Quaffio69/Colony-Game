using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Items;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Stockpiles with settings: grouping, filters, priority, reach, caps, reserves and workshop access.</summary>
public class StockpileTests
{
    private static ColonySim Flat(int size = 48, bool needs = false)
    {
        var w = new VoxelWorld(size, size, 12);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w) { NeedsEnabled = needs };
    }

    private static TilePos G(int x, int y) => new(x, y, 3); // a ground tile: its stockpile cell is the one above
    private static TilePos C(int x, int y) => new(x, y, 4);

    private static Stockpile Zone(ColonySim sim, int x0, int y0, int x1, int y1)
    {
        sim.Stockpiles.AddFloorBox(G(x0, y0), G(x1, y1));
        return sim.Stockpiles.ZoneAt(C(x0, y0))!;
    }

    /// <summary>Units actually lying in the stockpile (not counting what is on its way).</summary>
    private static int Stored(ColonySim sim, Stockpile zone, ItemType type) =>
        sim.Stockpiles.CellsOf(zone).Sum(c => sim.Items.TryGet(c, out var p) && p.Type == type ? p.Count : 0);

    private static void Run(ColonySim sim, int ticks, Func<bool>? until = null)
    {
        for (int i = 0; i < ticks && until?.Invoke() != true; i++) sim.Step();
    }

    [Fact]
    public void ZonedBoxes_AreStockpiles_TouchingOnesJoin()
    {
        var sim = Flat();
        var a = Zone(sim, 2, 2, 4, 4);
        Assert.Equal(9, a.CellCount);
        Assert.Equal($"Stockpile {a.Id}", a.Name);
        sim.Stockpiles.AddFloorBox(G(5, 2), G(6, 4)); // touching: extends it
        Assert.Same(a, sim.Stockpiles.ZoneAt(C(6, 3)));
        Assert.Equal(15, a.CellCount);
        var b = Zone(sim, 20, 20, 22, 22); // apart: a new one
        Assert.NotSame(a, b);
        Assert.Equal(2, sim.Stockpiles.Zones().Count());
        sim.Stockpiles.RemoveBox(G(20, 20), G(22, 22));
        Assert.Null(sim.Stockpiles.ZoneById(b.Id));
        sim.Stockpiles.AddFloorBox(G(30, 30), G(31, 31), depot: true);
        Assert.True(sim.Stockpiles.Contains(C(30, 30)));
        Assert.Null(sim.Stockpiles.ZoneAt(C(30, 30))); // a trade depot is no stockpile
    }

    [Fact]
    public void AStockpile_TakesOnlyWhatItAccepts_AndRefusedGoodsAreHauledOut()
    {
        var sim = Flat();
        sim.AddColonist("Hauler", C(10, 10));
        var near = Zone(sim, 12, 10, 14, 12);
        var far = Zone(sim, 30, 30, 32, 32);
        sim.Stockpiles.Configure(near, StockpileOption.Item, 0, text: "stone");
        Assert.False(near.Accepts(ItemType.Stone));
        sim.Items.Drop(C(8, 8), ItemType.Stone, 10);
        Run(sim, 3000, () => Stored(sim, far, ItemType.Stone) == 10);
        Assert.Equal(10, Stored(sim, far, ItemType.Stone));
        Assert.Equal(0, Stored(sim, near, ItemType.Stone));

        // Now the far one refuses stone and the near one takes it: the stored stone moves.
        sim.Stockpiles.Configure(far, StockpileOption.Category, 0, text: ItemType.Stone.Def.Category);
        sim.Stockpiles.Configure(near, StockpileOption.Everything, 1);
        Run(sim, 4000, () => Stored(sim, near, ItemType.Stone) == 10);
        Assert.Equal(10, Stored(sim, near, ItemType.Stone));
        Assert.Equal(0, Stored(sim, far, ItemType.Stone));
    }

    [Fact]
    public void Priority_FillsTheBestStockpileFirst_AndMovesGoodsUp()
    {
        var sim = Flat();
        sim.AddColonist("Hauler", C(10, 10));
        var near = Zone(sim, 12, 10, 14, 12);
        var far = Zone(sim, 30, 30, 32, 32);
        sim.Stockpiles.Configure(far, StockpileOption.Priority, 1); // far, but filled first
        sim.Items.Drop(C(8, 8), ItemType.Stone, 10);
        Run(sim, 3000, () => Stored(sim, far, ItemType.Stone) == 10);
        Assert.Equal(10, Stored(sim, far, ItemType.Stone));

        // Goods already in a lower-priority stockpile move up to a better one with room.
        sim.Items.Drop(C(13, 11), ItemType.Plank, 8);
        Assert.Equal(8, Stored(sim, near, ItemType.Plank));
        Run(sim, 4000, () => Stored(sim, far, ItemType.Plank) == 8);
        Assert.Equal(8, Stored(sim, far, ItemType.Plank));
        Assert.Equal(0, Stored(sim, near, ItemType.Plank));

        // Equal priorities: nothing moves back and forth.
        sim.Stockpiles.Configure(far, StockpileOption.Priority, Stockpile.DefaultPriority);
        sim.Items.Drop(C(12, 10), ItemType.Log, 4);
        Run(sim, 2000);
        Assert.Equal(4, Stored(sim, near, ItemType.Log));
    }

    [Fact]
    public void Reach_LimitsHowFarAStockpileDrawsGoodsFrom()
    {
        var sim = Flat();
        sim.AddColonist("Hauler", C(10, 10));
        var zone = Zone(sim, 2, 2, 4, 4);
        Assert.Equal(Stockpile.DefaultReach, zone.Reach);
        sim.Stockpiles.Configure(zone, StockpileOption.Reach, 10);
        sim.Items.Drop(C(30, 30), ItemType.Stone, 5); // ~38 tiles off
        sim.Items.Drop(C(8, 8), ItemType.Plank, 5); // ~7 tiles off
        Run(sim, 3000, () => Stored(sim, zone, ItemType.Plank) == 5);
        Assert.Equal(5, Stored(sim, zone, ItemType.Plank));
        Run(sim, 1500);
        Assert.Equal(0, Stored(sim, zone, ItemType.Stone));

        sim.Stockpiles.Configure(zone, StockpileOption.Reach, 0); // unlimited
        Run(sim, 4000, () => Stored(sim, zone, ItemType.Stone) == 5);
        Assert.Equal(5, Stored(sim, zone, ItemType.Stone));
    }

    [Fact]
    public void ACap_KeepsAtMostThatManyOfEachItem()
    {
        var sim = Flat();
        sim.AddColonist("Hauler", C(10, 10));
        var zone = Zone(sim, 12, 10, 16, 14);
        sim.Stockpiles.Configure(zone, StockpileOption.Cap, 12);
        sim.Items.Drop(C(8, 8), ItemType.Stone, 30);
        sim.Items.Drop(C(8, 9), ItemType.Plank, 5);
        Run(sim, 5000);
        Assert.Equal(12, Stored(sim, zone, ItemType.Stone));
        Assert.Equal(5, Stored(sim, zone, ItemType.Plank));
        Assert.All(sim.Items.All, kv => Assert.Equal(0, kv.Value.Incoming));
    }

    [Fact]
    public void AReserve_IsNotEatenFrom()
    {
        var food = Catalog.Items.First(i => i.Nutrition > 0).Type;
        var sim = Flat(needs: true);
        var c = sim.AddColonist("Hungry", C(10, 10));
        var zone = Zone(sim, 12, 10, 13, 11);
        sim.Stockpiles.Configure(zone, StockpileOption.Reserve, 1);
        sim.Items.Drop(C(12, 10), food, 10);
        c.Satiety = 0;
        Run(sim, 400);
        Assert.Equal(10, Stored(sim, zone, food));
        Assert.False(Needs.Usable(sim, C(12, 10)));
        Assert.True(Needs.Owned(sim, C(12, 10)));

        sim.Stockpiles.Configure(zone, StockpileOption.Reserve, 0);
        Run(sim, 400);
        Assert.True(Stored(sim, zone, food) < 10, "open again: eaten from");
    }

    [Fact]
    public void WorkshopAccess_DecidesWhichWorkshopsDrawFromAStockpile()
    {
        var sim = Flat();
        sim.Rooms.InstantBuild = true;
        var logs = Zone(sim, 2, 2, 4, 4);
        sim.Items.Drop(C(3, 3), ItemType.Log, 10);
        sim.Rooms.Paint(RoomType.Carpentry, G(20, 20), G(27, 24));
        sim.Rooms.Place(PartType.Sawbench, new TilePos(21, 21, 4), false);
        sim.Rooms.Place(PartType.InputCrate, new TilePos(20, 20, 4), false);
        sim.Rooms.Place(PartType.OutputCrate, new TilePos(27, 24, 4), false);
        var mill = sim.Rooms.All.Single();
        for (int i = 0; i < 2; i++) sim.AddColonist($"C{i}", C(10 + i, 12));
        int Crate() => sim.Items.TryGet(new TilePos(20, 20, 4), out var p) && p.Type == ItemType.Log ? p.Count : 0;

        sim.Commands.Execute(new ConfigureStockpile(logs.Id, StockpileOption.Access, (int)WorkshopAccess.Only)); // a whitelist naming nobody
        Run(sim, 2500);
        Assert.Equal(0, Crate());
        Assert.Equal(10, Stored(sim, logs, ItemType.Log));

        sim.Commands.Execute(new ConfigureStockpile(logs.Id, StockpileOption.Workshop, mill.Id, 1));
        Assert.True(logs.Lets(mill.Id));
        Run(sim, 4000, () => Crate() > 0 || Stored(sim, logs, ItemType.Log) < 10);
        Assert.True(Stored(sim, logs, ItemType.Log) < 10, "the listed workshop draws its logs");

        sim.Commands.Execute(new ConfigureStockpile(logs.Id, StockpileOption.Access, (int)WorkshopAccess.Except)); // now a blacklist naming it
        Assert.False(logs.Lets(mill.Id));
        Assert.True(logs.Lets(mill.Id + 1));
    }

    [Fact]
    public void Settings_GoThroughCommands_AndSurviveASnapshot()
    {
        var settings = new Net.GameSettings(Seed: 3, Width: 64, Depth: 64, Height: 40, Colonists: 2, Colonies: 1);
        var sim = settings.Create();
        var at = sim.Colonists[0].Position;
        sim.Commands.Execute(new AddStockpile(at.Offset(2, 2, -1), at.Offset(5, 5, -1)));
        var zone = sim.Stockpiles.Zones().Single();
        var q = sim.Commands;
        Assert.Equal("Stockpile renamed Pantry", q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Name, Text: "  Pantry  ")));
        q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Priority, 2));
        q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Cap, 50));
        q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Reserve, 1));
        q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Item, 0, Text: "stone"));
        Assert.Equal("No such stockpile", q.Execute(new ConfigureStockpile(999, StockpileOption.Priority, 1)));
        Assert.Equal("That stockpile belongs to another colony", q.Execute(new ConfigureStockpile(zone.Id, StockpileOption.Priority, 1) { Player = 1 }));
        Assert.Equal(2, zone.Priority);

        for (int i = 0; i < 100; i++) sim.Step();
        var loaded = Net.Snapshot.Load(Net.Snapshot.Take(sim, settings));
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
        var back = loaded.Stockpiles.ZoneById(zone.Id)!;
        Assert.Equal(("Pantry", 2, 50, true, false, zone.CellCount), (back.Name, back.Priority, back.Cap, back.Reserve, back.Accepts(ItemType.Stone), back.CellCount));
        for (int i = 0; i < 300; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(Net.StateHash.Compute(sim), Net.StateHash.Compute(loaded));
    }
}

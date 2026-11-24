using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.Procedural;
using Colony.Core.World;

namespace Colony.Core.Tests;

public class CommandTests
{
    private static readonly TilePos A = new(3, 4, 5), B = new(9, 10, 6);

    public static IEnumerable<object[]> EveryCommand() => new Command[]
    {
        new DesignateBox(A, B, DesignationKind.UpDownStair), new DesignateStairs(A, B), new SettleGame(), new SetDrafted(3, true), new SetDrafted(-1, false), new SquadMove(B), new SquadAttack(7), new DebugSpawnNest(A, 20, true), new DesignatePlants(DesignationKind.Chop, 1, 2, 3, 4),
        new CancelPlants(DesignationKind.Gather, 4, 3, 2, 1), new CancelOrders(A, B), new AddStockpile(A, B), new AddFarm(PlantKind.Glowcap, A, B),
        new PlaceWalls(A, B), new CancelWalls(A, B), new DebugCompleteAll(), new PaintRoom("carpentry", A, B), new EraseRoom(A, B),
        new PlacePart("carpentry.saw", A, true, 3), new RemoveParts(A, B), new CycleRecipe(A, true), new PlaceStructure(StructureKind.Pump, A, 3),
        new RemoveStructures(A, B), new PlaceEdge(StructureKind.Door, A, EdgeInfo.North, 2), new RemoveEdge(A, EdgeInfo.East),
        new EdgeLine(StructureKind.Window, A, EdgeInfo.East, 12, 4, true), new PlaceOutline(A, B, 3), new PlaceHouse(A, B, 3, 2, 1, 4), new ClearWalls(A, B),
        new Covers(StructureKind.Roof, A, B, 2, false), new SetWorkPriority(7, Work.Hauling, 1) { Player = 3 }, new SetHomePolicy(A, 2),
        new SetPolicyTier(1, 4, 3), new SetPolicyPerResident(0, 2, 1.25f), new SetPolicyShared(3, true), new AddPolicy(),
        new PlaceRacks(A, B, 2), new Demolish(A, B), new AddTradeDepot(A, B) { Player = 1 },
        new ProposeTrade(2, new Trade.TradeTerms([new Trade.TradeTerm(0, "planks", 20), new Trade.TradeTerm(2, "iron_ingot", 3)])),
        new CounterTrade(5, new Trade.TradeTerms([new Trade.TradeTerm(1, "bread", 9)])) { Player = 1 }, new AcceptTrade(5), new RejectTrade(6) { Player = 2 }, new SetMachineLimit(A, 50), new SetOrderPriority(A, B, 1) { Player = 2 },
    }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public void EveryCommand_SurvivesTheWire(Command command)
    {
        var back = CommandCodec.Decode(CommandCodec.Encode(command));
        Assert.Equal(command, back);
        Assert.Equal(command.Player, back.Player);
    }

    /// <summary>What a colony looks like, compactly: anything a lost or misordered command would change.</summary>
    private static string Fingerprint(ColonySim sim)
    {
        var parts = new List<string> { $"tick {sim.Tick}", $"orders {sim.Designations.Count}", $"zones {sim.Stockpiles.Count}", $"rooms {sim.Rooms.All.Count}" };
        foreach (var c in sim.Colonists) parts.Add($"{c.Id}@{c.Position}:{c.Task}:{c.Satiety}");
        parts.Add(string.Join(",", sim.Items.Totals()));
        long tiles = 17;
        var w = sim.World;
        for (int z = 0; z < w.Height; z++)
        for (int y = 0; y < w.Depth; y++)
        for (int x = 0; x < w.Width; x++)
            tiles = tiles * 31 + (int)w.Get(x, y, z);
        parts.Add($"tiles {tiles}");
        return string.Join(" | ", parts);
    }

    [Fact]
    public void AGame_ReplaysExactly_FromItsCommandLog()
    {
        static ColonySim Make() => ColonySim.Generate(new WorldGenSettings(Seed: 7, Width: 64, Depth: 64, Height: 40), colonists: 4);
        var sim = Make();
        var home = sim.Colonists[0].Position;
        int g = home.Z - 1;
        for (int i = 0; i < 200; i++) sim.Step();
        sim.Commands.Submit(new DesignateBox(new TilePos(home.X + 3, home.Y - 2, g), new TilePos(home.X + 6, home.Y + 2, g), DesignationKind.Dig));
        sim.Commands.Submit(new AddStockpile(new TilePos(home.X - 4, home.Y - 4, g), new TilePos(home.X - 2, home.Y - 2, g)));
        for (int i = 0; i < 300; i++) sim.Step();
        sim.Commands.Execute(new DesignatePlants(DesignationKind.Chop, 0, 0, 63, 63));
        sim.Commands.Execute(new SetWorkPriority(sim.Colonists[1].Id, Work.Mining, 1));
        sim.Commands.Execute(new SetWorkPriority(sim.Colonists[2].Id, Work.Hauling, 0));
        for (int i = 0; i < 1500; i++) sim.Step();
        Assert.Equal(5, sim.Commands.Log.Count);
        Assert.True(sim.Designations.Count < 40, "the colonists did some of the work"); // so the replay has something to match

        var bytes = CommandCodec.EncodeLog(sim.Commands.Log);
        var replayed = Make();
        CommandQueue.Replay(replayed, CommandCodec.DecodeLog(bytes), sim.Tick);
        Assert.Equal(Fingerprint(sim), Fingerprint(replayed));
    }

    [Fact]
    public void QueuedCommands_ApplyBeforeTheNextTick_AndAreLoggedWithIt()
    {
        var sim = ColonySim.Generate(new WorldGenSettings(Seed: 3, Width: 48, Depth: 48, Height: 32), colonists: 0);
        for (int i = 0; i < 10; i++) sim.Step();
        var c = sim.Colonists.Count > 0 ? sim.Colonists[0].Position : new TilePos(24, 24, sim.World.SurfaceLevel(24, 24));
        string? message = null;
        sim.Commands.Applied += (_, m) => message = m;
        sim.Commands.Submit(new DesignateBox(new TilePos(20, 20, 1), new TilePos(22, 22, 1), DesignationKind.Dig));
        Assert.Equal(0, sim.Designations.Count); // queued, not yet applied
        sim.Step();
        Assert.Equal(9, sim.Designations.Count);
        Assert.Equal("Dig: designated 9 tiles", message);
        Assert.Equal(10, sim.Commands.Log[0].Tick); // applied before tick 11
    }
}

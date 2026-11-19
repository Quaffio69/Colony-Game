using Colony.Core.Agents;
using Colony.Core.Commands;
using Colony.Core.Net;
using Colony.Core.Orders;

namespace Colony.Core.Tests;

public class SaveTests
{
    private static readonly GameSettings Small = new(Seed: 5, Width: 64, Depth: 64, Height: 40, Colonists: 4, Colonies: 2);

    [Fact]
    public void ASavedGame_LoadsBackIdentical()
    {
        var sim = Small.Create();
        var home = sim.Colonists[0].Position;
        for (int i = 0; i < 900; i++)
        {
            if (i == 100) sim.Commands.Execute(new DesignateBox(home.Offset(2, -2, -1), home.Offset(5, 2, -1), DesignationKind.Dig));
            if (i == 300) sim.Commands.Execute(new DesignatePlants(DesignationKind.Chop, 0, 0, 63, 63) { Player = 1 });
            if (i == 500) sim.Commands.Execute(new SetWorkPriority(sim.Colonists[1].Id, Work.Hauling, 1));
            sim.Step();
        }
        sim.Commands.Execute(new AddStockpile(home.Offset(-4, -4, -1), home.Offset(-2, -2, -1))); // at the save tick itself

        var file = new MemoryStream();
        SaveGame.Of(sim, Small, "Test colony", player: 1).Write(file);
        file.Position = 0;
        var header = SaveGame.Read(file, headerOnly: true);
        Assert.Equal(("Test colony", 1, 900L), (header.Name, header.Player, header.Tick));
        Assert.Equal(Small, header.Settings);

        file.Position = 0;
        var progress = new List<float>();
        var loaded = SaveGame.Read(file).Load(progress.Add);
        Assert.Equal(sim.Tick, loaded.Tick);
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
        Assert.Equal(1f, progress[^1]);
        Assert.True(progress.Count >= 3);

        // And it plays on the same as the original.
        for (int i = 0; i < 300; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
    }

    [Fact]
    public void NotASave_IsRefused()
    {
        var junk = new MemoryStream(new byte[] { 3, 1, 2, 3, 4, 5 });
        Assert.ThrowsAny<Exception>(() => SaveGame.Read(junk));
    }

    [Fact]
    public void AnOldLogSave_StillLoads_ByReplay()
    {
        var sim = Small.Create();
        var home = sim.Colonists[0].Position;
        for (int i = 0; i < 400; i++)
        {
            if (i == 50) sim.Commands.Execute(new DesignateBox(home.Offset(2, -2, -1), home.Offset(5, 2, -1), DesignationKind.Dig));
            sim.Step();
        }
        var old = new SaveGame(Small, sim.Tick, sim.Commands.Log.ToList(), "Old", 0, 0); // version 1: no snapshot
        var file = new MemoryStream();
        old.Write(file);
        file.Position = 0;
        var loaded = SaveGame.Read(file).Load();
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(loaded));
    }
}

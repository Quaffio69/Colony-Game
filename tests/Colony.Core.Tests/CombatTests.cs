using Colony.Core.Agents;
using Colony.Core.Combat;
using Colony.Core.Commands;
using Colony.Core.Industry;
using Colony.Core.Net;
using Colony.Core.World;

namespace Colony.Core.Tests;

/// <summary>Health, gear, drafting and the giant ants: docile until provoked, defending, raiding, spreading.</summary>
public class CombatTests
{
    private static ColonySim Flat(int size = 96)
    {
        var w = new VoxelWorld(size, size, 12);
        for (int z = 0; z <= 3; z++)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            w.Set(x, y, z, TileType.Stone);
        return new ColonySim(w) { NeedsEnabled = false };
    }

    private static void Run(ColonySim sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Step();
    }

    private static TilePos At(int x, int y) => new(x, y, 4);

    [Fact]
    public void Gear_ComesFromTheCatalog_AndEachWeaponHasItsStrength()
    {
        var spear = Gear.Of(Catalog.Item("spear"));
        var bow = Gear.Of(Catalog.Item("bow"));
        var axe = Gear.Of(Catalog.Item("war_axe"));
        var sword = Gear.Of(Catalog.Item("sword"));
        Assert.Equal(2, spear.Reach);           // strikes first
        Assert.True(spear.Pierce > sword.Pierce); // gets through armour
        Assert.True(bow.Ranged);
        Assert.Equal(3, axe.Cleave);            // swarms
        Assert.True(sword.Cooldown < spear.Cooldown && sword.Cooldown < axe.Cooldown); // quick
        Assert.True(Catalog.Item("war_axe").Def.TwoHanded && Catalog.Item("bow").Def.TwoHanded && !Catalog.Item("sword").Def.TwoHanded);
        Assert.True(Gear.Score(Catalog.Item("plate_armor")) > Gear.Score(Catalog.Item("padded_jerkin")));
        // Every weapon kills an ant in one blow; bare hands take two.
        foreach (var w in new[] { spear, bow, axe, sword }) Assert.True(w.Damage >= CreatureKinds.GiantAnt.MaxHealth);
        Assert.True(Gear.Fists.Damage * 2 >= CreatureKinds.GiantAnt.MaxHealth && Gear.Fists.Damage < CreatureKinds.GiantAnt.MaxHealth);
    }

    [Fact]
    public void Armour_TakesOffEveryHit_ShieldsBlockSome()
    {
        Assert.Equal(3, Gear.Resolve(3, 0, 0, 0, 0.9f, 0.9f));
        Assert.Equal(1, Gear.Resolve(3, 0, 2, 0, 0.9f, 0.9f));     // chitin against an ant's bite
        Assert.Equal(0, Gear.Resolve(3, 0, 4, 0, 0.9f, 0.9f));     // plate shrugs it off...
        Assert.Equal(1, Gear.Resolve(3, 0, 4, 0, 0.9f, 0.1f));     // ...mostly
        Assert.Equal(1, Gear.Resolve(3, 2, 4, 0, 0.9f, 0.9f));     // partly pierced
        Assert.Equal(3, Gear.Resolve(3, 4, 4, 0, 0.9f, 0.9f));     // pierced
        Assert.Equal(0, Gear.Resolve(30, 0, 0, 40, 0.2f, 0.9f));   // blocked outright
        Assert.Equal(30, Gear.Resolve(30, 0, 0, 40, 0.5f, 0.9f));
    }

    [Fact]
    public void Bows_NeedAClearLine()
    {
        var sim = Flat(24);
        Assert.True(Gear.LineOfSight(sim.World, At(2, 5), At(10, 5)));
        sim.World.Set(6, 5, 4, TileType.Stone);
        Assert.False(Gear.LineOfSight(sim.World, At(2, 5), At(10, 5)));
        Assert.True(Gear.InReach(At(2, 5), At(10, 5), 9));
        Assert.False(Gear.InReach(At(2, 5), At(4, 5), 1));
        Assert.True(Gear.InReach(At(2, 5), At(4, 6), 2));
    }

    [Fact]
    public void Ants_LeaveColonistsAlone_UntilProvoked()
    {
        var sim = Flat();
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(50, 50), 24);
        var c = sim.AddColonist("Walker", At(44, 50));
        Run(sim, 2000);
        Assert.True(nest.Awake);
        Assert.True(sim.Wildlife.Creatures.Count > 0, "foragers come out while someone is near");
        Assert.Equal(Colonist.MaxHealth, c.Health);
        Assert.Equal(0, nest.HostileMask(sim.Tick));
        Assert.True(sim.Wildlife.Creatures.All(a => a.Mode is CreatureMode.Wander or CreatureMode.Return));
    }

    [Fact]
    public void Nests_Sleep_WhenNobodyIsNear()
    {
        var sim = Flat();
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(80, 80), 24);
        var c = sim.AddColonist("Walker", At(76, 80));
        Run(sim, 600);
        Assert.True(sim.Wildlife.Creatures.Count > 0);
        // Gone far away: the nest falls asleep and its foragers go back underground.
        var far = sim.AddColonist("Far", At(2, 2));
        sim.Depart(c);
        Run(sim, 100);
        Assert.False(nest.Awake);
        Assert.Empty(sim.Wildlife.Creatures);
        Assert.True(nest.Total >= 24);
        Assert.NotNull(far);
    }

    [Fact]
    public void FullNests_FoundNewOnes()
    {
        var sim = Flat(200);
        sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(100, 100), 39);
        Run(sim, Needs.Day * 4);
        Assert.True(sim.Wildlife.Nests.Count >= 2, "a full nest sends founders off");
        var a = sim.Wildlife.Nests[0].Entrance;
        var b = sim.Wildlife.Nests[1].Entrance;
        Assert.True(Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)) >= 20);
    }

    [Fact]
    public void StrikingTheirNest_BringsTheSwarm_AndAnArmedSquadDestroysIt()
    {
        var sim = Flat();
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(60, 50), 40);
        var squad = new List<Colonist>();
        for (int i = 0; i < 6; i++)
        {
            var c = sim.AddColonist($"Soldier {i}", At(40, 46 + i));
            c.PutOn(sim, Catalog.Item(i % 2 == 0 ? "war_axe" : "sword"));
            c.PutOn(sim, Catalog.Item("chitin_armor"));
            if (i % 2 == 1) c.PutOn(sim, Catalog.Item("wooden_shield"));
            squad.Add(c);
        }
        sim.Commands.Execute(new SetDrafted(-1, true));
        sim.Commands.Execute(new SquadAttack(nest.Id));
        int lowest = Colonist.MaxHealth;
        bool swarmed = false;
        for (int i = 0; i < 8000 && sim.Wildlife.NestById(nest.Id) != null; i++)
        {
            sim.Step();
            swarmed |= sim.Wildlife.Creatures.Count(a => a.Fighting) >= 5;
            foreach (var c in squad) lowest = Math.Min(lowest, c.Health);
        }
        Assert.True(swarmed, "striking the mound brings the ants out");
        Assert.True(lowest < Colonist.MaxHealth, "and they bite back");
        Assert.Null(sim.Wildlife.NestById(nest.Id));
        Assert.True(sim.Wildlife.Slain >= 10);
        Assert.Contains(sim.Events, e => e.Text.Contains("destroyed a nest"));
        // Chitin is what's left of them.
        Assert.True(sim.Items.Totals()[Catalog.Item("chitin").Index] > 0);
        // The leftovers fight on or wander off; within a day the ground is clear.
        Run(sim, Needs.Day);
        Assert.Empty(sim.Wildlife.Creatures);
    }

    [Fact]
    public void AnUnarmedColonist_IsStruckDown_ThenRecovers()
    {
        // A lone colonist punching a nest: the swarm downs it; struck down, the ants leave it be; it gets back up.
        bool downed = false, recovered = false;
        for (int seed = 0; seed < 6 && !recovered; seed++)
        {
            var sim = Flat();
            var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(60, 50), 30 + seed);
            var c = sim.AddColonist("Fool", At(54, 50));
            sim.Commands.Execute(new SetDrafted(c.Id, true));
            sim.Commands.Execute(new SquadAttack(nest.Id));
            for (int i = 0; i < 3000 && !c.Downed; i++) sim.Step();
            if (!c.Downed) continue;
            downed = true;
            sim.Commands.Execute(new SetDrafted(c.Id, false));
            Run(sim, 200);
            Assert.True(sim.Wildlife.Creatures.All(a => a.TargetId != c.Id), "nobody keeps biting a fallen colonist");
            for (int i = 0; i < 2000 && c.Downed; i++) sim.Step();
            if (sim.Colonists.Contains(c))
            {
                Assert.False(c.Downed);
                Assert.True(c.Health >= Colonist.StandUpHealth);
                recovered = true;
            }
            else Assert.Contains(sim.Events, e => e.Text.Contains("died"));
        }
        Assert.True(downed);
        Assert.True(recovered);
    }

    [Fact]
    public void AColonistBitten_FightsBack_OrFleesWhenHurt()
    {
        var sim = Flat();
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(60, 50), 10);
        var c = sim.AddColonist("Worker", At(55, 50));
        Run(sim, 400);
        var ant = sim.Wildlife.Creatures.First();
        sim.Wildlife.Hurt(sim, ant, 1, c); // provoked
        bool fought = false;
        int before = c.Health;
        for (int i = 0; i < 400; i++)
        {
            sim.Step();
            fought |= c.Task == ColonistTask.Fight;
            // Badly hurt when this tick began (blows land after it acts): it runs rather than fights.
            if (before < Colonist.FleeHealth && !c.Downed && c.Health > 0) Assert.NotEqual(ColonistTask.Fight, c.Task);
            before = c.Health;
        }
        Assert.True(fought || c.Downed);
        Assert.True(nest.HostileTo(0, sim.Tick));
        // Calms down two days after the last provocation.
        sim.Depart(c);
        Run(sim, Wildlife.CalmTicks + 10);
        Assert.False(nest.HostileTo(0, sim.Tick));
    }

    [Fact]
    public void ColoniesSpreadingIntoTheirTerritory_AreRaided()
    {
        var sim = Flat();
        sim.Factions[0].Landing = At(20, 50);
        var people = Enumerable.Range(0, 3).Select(i => sim.AddColonist($"Settler {i}", At(20 + i, 50))).ToList();
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, At(70, 50), 36);
        Run(sim, 400);
        Assert.Equal(0, nest.Crowded);
        // A stockpile reaching towards the nest: its land now meets their territory.
        sim.Commands.Execute(new AddStockpile(new TilePos(46, 48, 3), new TilePos(50, 52, 3)));
        Run(sim, 400);
        Assert.Equal(1, nest.Crowded);
        Assert.Contains(sim.Events, e => e.Text.Contains("Expect raids"));
        bool raided = false;
        for (int i = 0; i < Needs.Day && !raided; i++)
        {
            sim.Step();
            raided = sim.Wildlife.Creatures.Any(a => a.Mode == CreatureMode.Raid);
        }
        Assert.True(raided);
        Assert.Contains(sim.Events, e => e.Text.Contains("raid"));
        Run(sim, 1500);
        Assert.True(people.Any(p => p.Health < Colonist.MaxHealth || p.Downed || !sim.Colonists.Contains(p)), "the raiders reach the colony");
    }

    [Fact]
    public void Drafted_FetchTheBestGear()
    {
        var sim = Flat(32);
        var c = sim.AddColonist("Recruit", At(10, 10));
        sim.Items.Drop(At(14, 10), Catalog.Item("sword"), 1);
        sim.Items.Drop(At(14, 12), Catalog.Item("war_axe"), 1);
        sim.Items.Drop(At(12, 14), Catalog.Item("plate_armor"), 1);
        sim.Items.Drop(At(12, 16), Catalog.Item("padded_jerkin"), 1);
        sim.Items.Drop(At(16, 14), Catalog.Item("iron_shield"), 1);
        sim.Commands.Execute(new SetDrafted(c.Id, true));
        Run(sim, 900);
        Assert.Equal(Catalog.Item("war_axe"), c.Weapon); // the better weapon for a crowd
        Assert.Equal(Catalog.Item("plate_armor"), c.Armour);
        Assert.Null(c.Shield);                            // no shield with a two-handed axe
        Assert.Equal(1, sim.Items.Totals()[Catalog.Item("sword").Index]); // the lesser pieces are left lying about
        Assert.Equal(ColonistTask.Rally, c.Task);
        // Sent somewhere, it marches there; stood down, it goes back to work keeping its gear.
        sim.Commands.Execute(new SquadMove(At(25, 25)));
        Run(sim, 400);
        Assert.Equal(At(25, 25), c.Position);
        sim.Commands.Execute(new SetDrafted(c.Id, false));
        Run(sim, 5);
        Assert.NotEqual(ColonistTask.Rally, c.Task);
        Assert.Equal(Catalog.Item("war_axe"), c.Weapon);
    }

    [Fact]
    public void Death_DropsGear_AndLeavesTheFamily()
    {
        var sim = Flat(32);
        var a = sim.AddColonist("A", At(10, 10));
        var b = sim.AddColonist("B", At(11, 10));
        a.Spouse = b;
        b.Spouse = a;
        a.PutOn(sim, Catalog.Item("spear"));
        sim.QueueDeath(a, "A has died");
        sim.Step();
        Assert.DoesNotContain(a, sim.Colonists);
        Assert.Null(b.Spouse);
        Assert.Equal(1, sim.Deaths);
        Assert.True(sim.Items.Totals()[Catalog.Item("spear").Index] == 1);
    }

    [Fact]
    public void Commands_OnlyDraftYourOwnColonists()
    {
        var sim = Flat(32);
        sim.AddFaction("Ironvale");
        var theirs = sim.AddColonist("Theirs", At(5, 5), faction: 1);
        Assert.Contains("another colony", sim.Commands.Execute(new SetDrafted(theirs.Id, true)));
        Assert.False(theirs.Drafted);
        sim.Commands.Execute(new SetDrafted(-1, true) { Player = 1 });
        Assert.True(theirs.Drafted);
    }

    /// <summary>A generated world with a fight on: the snapshot loads mid-battle and plays on identically.</summary>
    [Fact]
    public void ASnapshotMidBattle_PlaysOnIdentically()
    {
        var settings = new GameSettings(Seed: 3, Width: 96, Depth: 96, Height: 48, Colonists: 6, Colonies: 1);
        var sim = settings.Create();
        var home = sim.Colonists[0].Position;
        TilePos? site = null;
        for (int r = 14; r < 30 && site is null; r++)
        {
            var p = new TilePos(home.X + r, home.Y, sim.World.SurfaceLevel(home.X + r, home.Y) + 1);
            if (Navigation.Movement.IsStandable(sim.World, p) && sim.Connectivity.Connected(p, home)) site = p;
        }
        Assert.NotNull(site);
        var nest = sim.Wildlife.AddNest(sim, CreatureKinds.GiantAnt, site!.Value, 30);
        foreach (var c in sim.Colonists.Take(3)) c.PutOn(sim, Catalog.Item("spear"));
        sim.Commands.Execute(new SetDrafted(-1, true));
        sim.Commands.Execute(new SquadAttack(nest.Id));
        for (int i = 0; i < 2000 && sim.Wildlife.Creatures.Count(a => a.Fighting) < 3; i++) sim.Step();
        Assert.True(sim.Wildlife.Creatures.Count(a => a.Fighting) > 0, "a fight is on");

        var copy = Snapshot.Load(Snapshot.Take(sim, settings));
        Assert.Equal(StateHash.Compute(sim), StateHash.Compute(copy));
        for (int i = 1; i <= 2000; i++)
        {
            sim.Step();
            copy.Step();
            if (i % 100 == 0) Assert.True(StateHash.Compute(sim) == StateHash.Compute(copy), $"apart at tick {sim.Tick}");
        }
        Assert.True(sim.Wildlife.Slain > 0);
    }
}

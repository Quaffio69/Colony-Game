using System;
using System.Collections.Generic;
using System.Linq;
using Colony.Core;
using Colony.Core.Agents;
using Colony.Core.Industry;
using Colony.Core.Orders;
using Colony.Core.World;
using Godot;

namespace ColonyGame.UI;

/// <summary>
/// What is at a tile, laid out for the hover card and Inspect: an icon and title (the thing a click would select),
/// a short subtitle, a few fact lines (with materials: "Plank wall north, east", "Flagstone floor", "Saw horse of
/// logs") and a hint for what a click or key does there.
/// </summary>
public sealed class InfoCard
{
    public string Glyph = "·";
    public Color Color = Colors.White;
    public string Title = "";
    public string Sub = "";
    public readonly List<(string Glyph, Color Color, string Text, string Tone)> Lines = new();
    public string Hint = "";

    public void Add(string glyph, Color color, string text, string tone = "Text") => Lines.Add((glyph, color, text, tone));

    /// <summary>Changes when anything shown does (so the card only redraws then).</summary>
    public string Key => $"{Glyph}|{Title}|{Sub}|{Hint}|{string.Join("|", Lines.Select(l => l.Text))}";
}

public static class WorldInfo
{
    private static Color Tok(string t) => GlyphTheme.Tok(t);

    /// <summary>The card for a hovered (solid) tile: what stands on it first, then the tile's own facts.</summary>
    public static InfoCard For(GameUi ui, TilePos tile)
    {
        var sim = ui.Sim;
        var cell = tile.Offset(0, 0, 1);
        var card = new InfoCard();
        var pick = ui.Main.Selection.PickFor(tile, ui.Me);
        string ground = GroundName(sim, tile);
        bool showsSite = false, showsPile = false, showsFarm = false;
        switch (pick)
        {
            case PickColonist pc:
            {
                var c = pc.Colonist;
                (card.Glyph, card.Color, card.Title) = (c.IsAutomaton ? "◘" : "☺", GlyphTheme.Person(c.Id), c.Name);
                card.Sub = ColonistsWindow.Doing(c) + (c.Faction != ui.Me ? $" · {sim.Factions[c.Faction].Name}" : "");
                string food = c.IsAutomaton ? "" : c.Starving ? " · starving" : c.Hungry ? " · hungry" : " · fed";
                string mood = c.IsAutomaton ? "" : $" · mood {c.Happiness:P0}";
                card.Add("♥", Tok("bad"), c.Downed ? "struck down" : $"health {c.Health}/{Colonist.MaxHealth}{food}{mood}", c.Downed || c.Starving ? "Bad" : "Text");
                if (c.IsMachine)
                {
                    card.Add("☼", Tok("warn"), $"fuel {c.Fuel * 100 / Colonist.FuelCapacity}%" + (c.OutOfFuel ? " · out: a driver needs charcoal in reach" : c.LowOnFuel ? " · low: its driver will stoke it" : ""),
                        c.OutOfFuel ? "Bad" : "Dim");
                    card.Add("☺", Tok("hi"), c.Driver is { } d ? $"driven by {d.Name}" : c.ComingToDrive is { } w ? $"{w.Name} is coming to drive it" : "parked: a miner or hauler drives it when the work pays", "Dim");
                }
                else if (c.Vehicle is { } v)
                    card.Add("☼", Tok("warn"), $"driving {v.Name} (fuel {v.Fuel * 100 / Colonist.FuelCapacity}%)", "Dim");
                if (c.CarryingCount > 0) card.Add("•", ItemLook.Color(c.CarryingType), $"carrying {c.CarryingCount} {c.CarryingType.Name.ToLowerInvariant()}", "Dim");
                card.Hint = "click: details · Colonists (C)";
                break;
            }
            case PickRoom pr:
            {
                var room = pr.Room;
                string owner = room.Faction != ui.Me ? $" · {sim.Factions[room.Faction].Name}" : "";
                if (room.Type.IsHome && sim.Factions[room.Faction].Housing.HomeOf(room) is { } home)
                {
                    (card.Glyph, card.Color, card.Title) = ("⌂", Tok("furn"), home.Name);
                    card.Sub = home.Policy.Name.ToLowerInvariant() + owner;
                    card.Add("☺", Tok("accent"), $"{G.Plural(home.Residents.Count, "resident")}, {G.Plural(home.Beds, "bed")}", "Dim");
                    card.Hint = room.Faction == ui.Me ? "click: details · F: next housing policy" : "click: details";
                }
                else
                {
                    (card.Glyph, card.Color, card.Title) = ("□", RoomView.RoomColor(room.Type) with { A = 1 }, room.Name);
                    card.Sub = "workshop" + owner;
                    card.Add("•", Tok("dim"), Rooms.Status(room, sim), "Dim");
                    card.Hint = "click: details";
                }
                break;
            }
            case PickZone { Farm: true } pz:
                (card.Glyph, card.Color, card.Title) = ("τ", Tok("farm"), "Farm field");
                if (sim.Farms.TryGet(pz.Cell, out var crop)) card.Sub = Colony.Core.Nature.PlantInfo.Name(crop).ToLowerInvariant();
                card.Hint = "click: details";
                showsFarm = true;
                break;
            case PickZone pz:
                bool depot = sim.Stockpiles.IsDepot(pz.Cell);
                var store = sim.Stockpiles.ZoneAt(pz.Cell);
                (card.Glyph, card.Color, card.Title) = (depot ? "$" : "≡", Tok(depot ? "accent" : "item"), depot ? "Trade depot" : store?.Name ?? "Stockpile");
                if (store != null) card.Sub = $"priority {store.Priority}" + (store.Reserve ? " · reserve" : "");
                if (sim.Factions.Count > 1) card.Sub = sim.Factions[sim.Stockpiles.OwnerOf(pz.Cell)].Name + (card.Sub.Length > 0 ? " · " + card.Sub : "");
                card.Hint = "click: details";
                break;
            case PickSite ps:
            {
                var bp = ps.Blueprint;
                (card.Glyph, card.Color, card.Title, card.Sub) = ("+", Tok("warn"), Tools.BuiltName(bp), "planned");
                card.Add("…", Tok("warn"), Waiting(ui, bp.Cost.Material, bp.Cost.Count, Constructions.MaterialsOnSite(bp, sim.Items)), "Dim");
                showsSite = true;
                card.Hint = "click: details · Shift+X: cancel";
                break;
            }
            case PickPile pp when sim.Items.TryGet(pp.Cell, out var pile):
                (card.Glyph, card.Color, card.Title, card.Sub) = (ItemLook.Glyph(pile.Type), ItemLook.Color(pile.Type), pile.Type.Name, $"{pile.Count} here");
                showsPile = true;
                card.Hint = "";
                break;
            case PickNest pn:
            {
                var n = pn.Nest;
                bool hostile = n.HostileTo(ui.Me, sim.Tick);
                (card.Glyph, card.Color, card.Title) = ("♠", Tok("bad"), $"{n.Kind.Name} nest");
                card.Sub = hostile ? "hostile" : "calm";
                card.Add("♠", Tok("bad"), $"{n.Total} ants ({n.Out} out) · {(hostile ? "they attack your people" : "leave them be")}", hostile ? "Bad" : "Dim");
                card.Hint = "G then click it: attack";
                break;
            }
            default:
                (card.Glyph, card.Color, card.Title, card.Sub) = ("·", Tok("dim"), ground, $"level {Level(sim, tile.Z)}");
                break;
        }
        if (pick is not PickTile) card.Add("·", Tok("faint"), $"on {ground.ToLowerInvariant()}, level {Level(sim, tile.Z)}", "Faint");
        AddBuilt(ui, card, cell);
        AddFacts(ui, card, tile, cell, showsSite, showsPile, showsFarm);
        return card;
    }

    /// <summary>Floor, roof and the walls, doors and windows round the cell, each with its material.</summary>
    private static void AddBuilt(GameUi ui, InfoCard card, TilePos cell)
    {
        var world = ui.Sim.World;
        if (!world.InBounds(cell)) return;
        int floor = world.FloorAt(cell), roof = world.RoofAt(cell);
        if (floor > 0) card.Add("_", Tools.Rgb(FloorMaterials.All[floor].Color), Tools.FloorName(floor));
        if (roof > 0) card.Add("▲", Tools.Rgb(RoofMaterials.All[roof].Color), Tools.RoofName(roof));
        // The four edges of the cell (each cell owns its east and north edges; west and south belong to the neighbours).
        var sides = new (string Dir, TilePos Cell, int Side)[]
        {
            ("north", cell, EdgeInfo.North), ("east", cell, EdgeInfo.East),
            ("south", cell.Offset(0, -1, 0), EdgeInfo.North), ("west", cell.Offset(-1, 0, 0), EdgeInfo.East),
        };
        var built = new List<(string Name, string Dir, byte Info)>();
        var planned = new List<(string Name, string Dir)>();
        foreach (var (dir, c, side) in sides)
        {
            if (!world.InBounds(c)) continue;
            byte info = world.Edge(c, side);
            if (Tools.EdgeName(info) is { } name) built.Add((name, dir, info));
            if (ui.Sim.Constructions.EdgeAt(c, side) is { } bp) planned.Add((Tools.BuiltName(bp), dir));
        }
        foreach (var g in built.GroupBy(b => b.Name))
        {
            byte first = g.First().Info;
            var color = EdgeInfo.Kind(first) == EdgeKind.Wall ? Tools.WallColor(EdgeInfo.Material(first)) : Tok(EdgeInfo.Kind(first) == EdgeKind.Door ? "wood" : "water2");
            card.Add(EdgeInfo.Kind(first) == EdgeKind.Door ? "◘" : EdgeInfo.Kind(first) == EdgeKind.Window ? "°" : "#", color, $"{g.Key} {string.Join(", ", g.Select(b => b.Dir))}");
        }
        foreach (var g in planned.GroupBy(p => p.Name))
            card.Add("+", Tok("warn"), $"{g.Key} {string.Join(", ", g.Select(p => p.Dir))} (planned)", "Dim");
    }

    /// <summary>Plants, water, waterworks, crops, parts, plans, piles and ants at the cell.</summary>
    /// <summary>A level as the player reads it: counted from level zero (the lowest ground on the map), negative below.</summary>
    public static string Level(ColonySim sim, int z) => (z - sim.World.ZeroLevel).ToString();

    /// <summary>What a tile is called: natural rock by its kind, unexplored rock (fog of war) by nothing more.</summary>
    public static string GroundName(ColonySim sim, TilePos tile) =>
        sim.World.IsHidden(tile.X, tile.Y, tile.Z) ? "Unexplored rock" : sim.World.TileName(tile);

    private static void AddFacts(GameUi ui, InfoCard card, TilePos tile, TilePos cell, bool showsSite, bool showsPile, bool showsFarm)
    {
        var sim = ui.Sim;
        if (sim.Designations.TryGet(tile, out var order) || sim.Designations.TryGet(cell, out order))
        {
            var at = sim.Designations.Contains(tile) ? tile : cell;
            int pri = sim.Designations.PriorityOf(at);
            string what = Colony.Core.Orders.Designations.IsPlantOrder(order) ? order.ToString().ToLowerInvariant() : order == Colony.Core.Orders.DesignationKind.Dig ? "dig" : "carve stairs";
            card.Add("▼", Tok("ore"), $"ordered: {what}" + (pri != Colony.Core.Orders.Designations.DefaultPriority ? $", priority {pri}" : ""), "Dim");
        }
        if (sim.Flora.TryGet(cell, out var plant))
        {
            bool ripe = Colony.Core.Nature.PlantInfo.IsGatherable(plant);
            card.Add("♣", Tok("pine"), Colony.Core.Nature.PlantInfo.Name(plant.Kind) + (ripe ? " (ripe)" : ""));
        }
        if (sim.Water.MassAt(cell) >= Colony.Core.Fluids.Water.MinMass)
            card.Add("≈", Tok("water"), $"water {sim.Water.Level(cell)}/7" + (sim.Water.Pressure(cell) is var pr and > 0.05f ? $", pressed by {pr:0.0}" : ""));
        if (sim.Mist.At(cell) is var mist and >= Colony.Core.Fluids.Mist.Felt)
            card.Add("~", Tok("water2"), (mist >= Colony.Core.Fluids.Mist.Full ? "thick waterfall mist" : "waterfall mist") + " · lifts the mood of people who linger", "Dim");
        if (sim.Waterworks.At(cell) is { } thing)
            card.Add("☼", Tok("water"), thing switch
            {
                Colony.Core.Fluids.WaterWheel ww => $"Water wheel making {ww.Power:0.#} power",
                Colony.Core.Fluids.Pump pp => $"Pump moving {pp.Pumped} a step",
                _ => "Waterworks",
            });
        if (!showsFarm && sim.Farms.TryGet(cell, out var crop))
            card.Add("τ", Tok("farm"), $"{Colony.Core.Nature.PlantInfo.Name(crop)} farm plot");
        if ((sim.Rooms.PartAt(cell) ?? sim.Rooms.PartAt(tile)) is { } part)
        {
            string made = $"{part.Name} of {part.Cost.Material.Name.ToLowerInvariant()}";
            if (!part.Built)
                card.Add("+", Tok("warn"), $"{made} (planned) · {Waiting(ui, part.Cost.Material, part.Cost.Count, part.Cells.Sum(c => sim.Items.TryGet(c, out var p) && p.Type == part.Cost.Material ? p.Count : 0))}", "Dim");
            else if (part.Recipe is { } making)
            {
                card.Add("÷", Tok("warn"), $"{made}, making {making.Name.ToLowerInvariant()}");
                if (part.Room.Faction == ui.Me) card.Hint = (card.Hint.Length > 0 ? card.Hint + " · " : "") + "F: next recipe";
            }
            else card.Add(Tools.PartGlyph(part.Type), Tok("dim"), made);
        }
        if (!showsSite && (sim.Constructions.At(cell) ?? sim.Constructions.CoverAt(cell, false) ?? sim.Constructions.CoverAt(cell, true)) is { } site)
            card.Add("+", Tok("warn"), $"{Tools.BuiltName(site)} (planned) · {Waiting(ui, site.Cost.Material, site.Cost.Count, Constructions.MaterialsOnSite(site, sim.Items))}", "Dim");
        if (!showsPile && sim.Items.TryGet(cell, out var pile) && pile.Count > 0)
            card.Add(ItemLook.Glyph(pile.Type), ItemLook.Color(pile.Type), $"{pile.Count} {pile.Type.Name.ToLowerInvariant()}");
        int ants = sim.Wildlife.Near(cell, 1).Count();
        if (ants > 0) card.Add("♠", Tok("bad"), G.Plural(ants, "giant ant"), "Bad");
    }

    /// <summary>"0/2 logs here · no logs to bring: chop trees (T)", or "... waiting for a builder".</summary>
    public static string Waiting(GameUi ui, Colony.Core.Items.ItemType material, int needed, int delivered)
    {
        string mat = material.Name.ToLowerInvariant();
        string s = $"{Math.Min(delivered, needed)}/{needed} {mat} here";
        if (delivered >= needed) return s + " · waiting for a builder (Construction work)";
        if (ui.Count(material) - delivered > 0) return s + " · haulers will bring the rest";
        string from = material == Colony.Core.Items.ItemType.Log ? "chop trees (T)" : material == Colony.Core.Items.ItemType.Plank ? "saw logs at a saw bench (Carpentry workshop)"
            : material == Colony.Core.Items.ItemType.Stone ? "dig rock (N)" : "make some";
        return s + $" · no {mat} to bring: {from}";
    }
}

using Colony.Core.Industry;

namespace Colony.Core.Items;

/// <summary>
/// A kind of item: an index into the <see cref="Catalog"/> (loaded from <c>data/tech-tree.json</c>), so new
/// products need no code. Items are counted, never individual objects (see <see cref="ItemPiles"/>). The
/// fields below are the items the simulation itself refers to (yields of digging, felling and farming).
/// </summary>
public readonly record struct ItemType(ushort Index)
{
    public ItemDef Def => Catalog.Items[Index];
    public string Id => Def.Id;
    public string Name => Def.Name;
    public override string ToString() => Def.Id;

    public static ItemType Log => Known.Log;
    public static ItemType Plank => Known.Plank;
    public static ItemType Stone => Known.Stone;
    public static ItemType Clay => Known.Clay;
    public static ItemType Sand => Known.Sand;
    public static ItemType CopperOre => Known.CopperOre;
    public static ItemType TinOre => Known.TinOre;
    public static ItemType IronOre => Known.IronOre;
    public static ItemType SilverOre => Known.SilverOre;
    public static ItemType GoldOre => Known.GoldOre;
    public static ItemType Quartz => Known.Quartz;
    public static ItemType Sulfur => Known.Sulfur;
    public static ItemType Aetherite => Known.Aetherite;
    public static ItemType Berries => Known.Berries;
    public static ItemType Fiber => Known.Fiber;
    public static ItemType Grain => Known.Grain;
    public static ItemType Herbs => Known.Herbs;
    public static ItemType Glowcaps => Known.Glowcaps;
    public static ItemType Charcoal => Known.Charcoal;

    /// <summary>The rough gem a gem pocket leaves (garnet, sapphire, ruby, emerald, diamond).</summary>
    public static ItemType RoughGem(World.TileType gem) => gem switch
    {
        World.TileType.Garnet => Known.Garnet,
        World.TileType.Sapphire => Known.Sapphire,
        World.TileType.Ruby => Known.Ruby,
        World.TileType.Emerald => Known.Emerald,
        World.TileType.Diamond => Known.Diamond,
        _ => throw new ArgumentOutOfRangeException(nameof(gem)),
    };

    // Resolved on first use, never while the catalog itself is loading.
    private static class Known
    {
        public static readonly ItemType Log = Catalog.Item("logs");
        public static readonly ItemType Plank = Catalog.Item("planks");
        public static readonly ItemType Stone = Catalog.Item("stone");
        public static readonly ItemType Clay = Catalog.Item("clay");
        public static readonly ItemType Sand = Catalog.Item("sand");
        public static readonly ItemType CopperOre = Catalog.Item("copper_ore");
        public static readonly ItemType TinOre = Catalog.Item("tin_ore");
        public static readonly ItemType IronOre = Catalog.Item("iron_ore");
        public static readonly ItemType SilverOre = Catalog.Item("silver_ore");
        public static readonly ItemType GoldOre = Catalog.Item("gold_ore");
        public static readonly ItemType Quartz = Catalog.Item("quartz");
        public static readonly ItemType Sulfur = Catalog.Item("sulfur");
        public static readonly ItemType Aetherite = Catalog.Item("aetherite");
        public static readonly ItemType Berries = Catalog.Item("berries");
        public static readonly ItemType Fiber = Catalog.Item("fiber");
        public static readonly ItemType Grain = Catalog.Item("grain");
        public static readonly ItemType Herbs = Catalog.Item("herbs");
        public static readonly ItemType Glowcaps = Catalog.Item("glowcaps");
        public static readonly ItemType Charcoal = Catalog.Item("charcoal");
        public static readonly ItemType Garnet = Catalog.Item("rough_garnet");
        public static readonly ItemType Sapphire = Catalog.Item("rough_sapphire");
        public static readonly ItemType Ruby = Catalog.Item("rough_ruby");
        public static readonly ItemType Emerald = Catalog.Item("rough_emerald");
        public static readonly ItemType Diamond = Catalog.Item("rough_diamond");
    }
}

/// <summary>What producing an item does: a stackable good, a promoted colonist, or a new worker.</summary>
public enum ItemKind : byte
{
    Good,
    /// <summary>An education level: "making" one promotes a colonist (see <see cref="Agents.Colonist.Education"/>).</summary>
    Education,
    /// <summary>A worker (automaton): "making" one adds a colonist.</summary>
    Worker,
    /// <summary>Heavy machinery (excavator, power wagon): "making" one rolls a machine out (<see cref="Agents.MachineKind"/>).</summary>
    Machine,
}

/// <summary>Static facts about an item, from the catalog.</summary>
public sealed class ItemDef
{
    public required ItemType Type { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public ItemKind Kind { get; init; }
    /// <summary>Pile shape the game draws (e.g. "logs", "ingots", "sacks").</summary>
    public string Model { get; init; } = "heap";
    /// <summary>sRGB colour as 0xRRGGBB.</summary>
    public uint Color { get; init; } = 0xff00ff;
    public int StackLimit { get; init; } = 40;
    /// <summary>Kg a unit (catalog "weight", else by category).</summary>
    public float Weight { get; init; } = 1;
    /// <summary>Units one colonist carries a trip: <see cref="Balance.CarryKg"/> over the weight, at least one.</summary>
    public int CarryLimit => Math.Max(1, (int)(Balance.CarryKg / Weight + 0.001f));
    public string? Source { get; init; }
    public IReadOnlyList<string> Uses { get; init; } = [];
    /// <summary>The colonists' need this item meets (a "need: X" use): "food", "dress", "comfort"...; null if none.</summary>
    public string? Need { get; init; }
    /// <summary>Meals of nutrition one unit gives (foods only; a colonist eats about three a day).</summary>
    public int Nutrition { get; init; }
    /// <summary>The home fixture slot this item fills ("bed", "furniture", "tableware", "lighting", "decor"), or null.</summary>
    public string? Fixture { get; init; }
    /// <summary>The fixture's quality tier (1 humble, 2 comfortable, 3 fine); 0 if not a fixture.</summary>
    public int FixtureTier { get; init; }
    /// <summary>Days a fixture lasts in a home before it wears out and is replaced (upkeep).</summary>
    public int Wear { get; init; }
    /// <summary>Where a light or decor piece goes in a home: "table", "wall", "floor" or "corner" (null: by slot).</summary>
    public string? Place { get; init; }
    /// <summary>What a fixture is mostly made of: wood, stone, metal, glass, cloth or clay (colonists may like one).</summary>
    public string? Material { get; init; }
    /// <summary>Equipment slot: "weapon", "body" or "shield" (null: not gear). See <see cref="Combat.Gear"/>.</summary>
    public string? Gear { get; init; }
    /// <summary>Weapons: damage per hit, ticks between attacks, reach in tiles (over 2: ranged), extra foes hit, armour ignored.</summary>
    public int Damage { get; init; }
    public int Cooldown { get; init; }
    public int Reach { get; init; }
    public int Cleave { get; init; }
    public int Pierce { get; init; }
    /// <summary>Needs both hands (no shield with it).</summary>
    public bool TwoHanded { get; init; }
    /// <summary>Body armour: damage taken off every hit.</summary>
    public int Protect { get; init; }
    /// <summary>Shields: percent of hits blocked outright.</summary>
    public int Block { get; init; }
    /// <summary>1 + the highest tier among the inputs of the recipe that makes it (raw = 0).</summary>
    public int Tier { get; internal set; }
    public bool IsRaw => Category == "raw";
}

public static class ItemInfo
{
    public static int TypeCount => Catalog.Items.Count;

    public static string Name(ItemType t) => t.Def.Name;

    /// <summary>Most units one tile's pile can hold.</summary>
    public static int StackLimit(ItemType t) => t.Def.StackLimit;

    /// <summary>Units one colonist carries per trip.</summary>
    public static int CarryLimit(ItemType t) => t.Def.CarryLimit;
}

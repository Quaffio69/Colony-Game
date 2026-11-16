using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Colony.Core.Items;

namespace Colony.Core.Industry;

/// <summary>
/// The production tree: every item, workshop, station and recipe, loaded once from <c>data/tech-tree.json</c>
/// (embedded in this assembly). Indices follow file order, so the catalog is deterministic. Load errors
/// (unknown ids, bad counts, a station without recipes) throw with the offending entry named.
/// </summary>
public static class Catalog
{
    private static readonly Data D = Load();

    public static IReadOnlyList<ItemDef> Items => D.Items;
    public static IReadOnlyList<RoomType> Workshops => D.Workshops;
    public static IReadOnlyList<PartType> Parts => D.Parts;
    public static IReadOnlyList<Recipe> Recipes => D.Recipes;
    public static IReadOnlyList<string> EducationLevels => D.Levels;

    public static ItemType Item(string id) =>
        D.ItemById.TryGetValue(id, out var t) ? t : throw new KeyNotFoundException($"No item '{id}' in the catalog");
    public static bool TryItem(string id, out ItemType t) => D.ItemById.TryGetValue(id, out t);
    public static RoomType Workshop(string id) =>
        D.WorkshopById.TryGetValue(id, out var w) ? w : throw new KeyNotFoundException($"No workshop '{id}' in the catalog");
    public static PartType Part(string id) =>
        D.PartById.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"No part '{id}' in the catalog");
    /// <summary>The recipe that makes an item, or null for raw resources.</summary>
    public static Recipe? RecipeFor(ItemType t) => D.RecipeByOutput.GetValueOrDefault(t);

    public static int Education(string level)
    {
        int i = D.Levels.IndexOf(level);
        return i >= 0 ? i : throw new KeyNotFoundException($"No education level '{level}'");
    }

    public static string EducationName(int level) => D.Levels[Math.Clamp(level, 0, D.Levels.Count - 1)];

    /// <summary>Parts every room can take, in build-menu order.</summary>
    public static IEnumerable<PartType> GenericParts => D.Parts.Where(p => p.Workshop is null);

    private sealed class Data
    {
        public readonly List<ItemDef> Items = new();
        public readonly List<RoomType> Workshops = new();
        public readonly List<PartType> Parts = new();
        public readonly List<Recipe> Recipes = new();
        public readonly List<string> Levels = new();
        public readonly Dictionary<string, ItemType> ItemById = new();
        public readonly Dictionary<string, RoomType> WorkshopById = new();
        public readonly Dictionary<string, PartType> PartById = new();
        public readonly Dictionary<ItemType, Recipe> RecipeByOutput = new();
    }

    private static Data Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tech-tree.json")
                           ?? throw new InvalidDataException("tech-tree.json is not embedded in Colony.Core");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var d = new Data();

        foreach (var lv in root.GetProperty("educationLevels").EnumerateArray()) d.Levels.Add(lv.GetString()!);

        foreach (var e in root.GetProperty("items").EnumerateArray())
        {
            string id = Str(e, "id");
            if (d.ItemById.ContainsKey(id)) throw Bad($"item '{id}' is defined twice");
            if (d.Items.Count > ushort.MaxValue) throw Bad("too many items");
            var type = new ItemType((ushort)d.Items.Count);
            string category = Str(e, "category");
            bool isRaw = category == "raw";
            d.Items.Add(new ItemDef
            {
                Type = type,
                Id = id,
                Name = Str(e, "name"),
                Category = category,
                Kind = Opt(e, "kind") switch
                {
                    null => ItemKind.Good,
                    "education" => ItemKind.Education,
                    "worker" => ItemKind.Worker,
                    var k => throw Bad($"item '{id}': unknown kind '{k}'"),
                },
                Model = Opt(e, "model") ?? "crates",
                Color = Opt(e, "color") is { } hex ? ParseColor(hex, id) : 0xff00ff,
                StackLimit = Int(e, "stack") ?? (isRaw ? 25 : 40),
                CarryLimit = Int(e, "carry") ?? (isRaw ? 5 : 6),
                Source = Opt(e, "source"),
                Uses = e.TryGetProperty("uses", out var uses) ? uses.EnumerateArray().Select(u => u.GetString()!).ToList() : [],
            });
            d.ItemById[id] = type;
        }

        foreach (var e in root.GetProperty("workshops").EnumerateArray())
        {
            string id = Str(e, "id");
            var w = new RoomType
            {
                Index = d.Workshops.Count,
                Id = id,
                Name = Str(e, "name"),
                About = Opt(e, "about") ?? "",
                Color = Opt(e, "color") is { } hex ? ParseColor(hex, id) : 0xffffff,
                Later = e.TryGetProperty("later", out var later) ? later.EnumerateArray().Select(u => u.GetString()!).ToList() : [],
            };
            d.Workshops.Add(w);
            d.WorkshopById[id] = w;
            foreach (var s in e.GetProperty("stations").EnumerateArray())
            {
                string sid = Str(s, "id");
                var build = s.TryGetProperty("build", out var b)
                    ? b.EnumerateArray().Select(m => ItemRef(d, m.GetString()!, $"station '{id}.{sid}' build")).ToList()
                    : new List<ItemType>();
                var part = new PartType
                {
                    Index = d.Parts.Count,
                    Id = $"{id}.{sid}",
                    Name = Str(s, "name"),
                    Role = PartRole.Machine,
                    Workshop = w,
                    Model = Opt(s, "model") ?? sid,
                    Build = build,
                    SizeNames = s.TryGetProperty("sizeNames", out var sn) ? sn.EnumerateArray().Select(n => n.GetString()!).ToList() : [],
                };
                AddPart(d, part);
                w.Stations.Add(part);
            }
        }

        // Parts any room can take.
        AddPart(d, new PartType { Index = d.Parts.Count, Id = "tool_rack", Name = "Tools", Role = PartRole.Auxiliary, Model = "tool_rack" });
        AddPart(d, new PartType { Index = d.Parts.Count, Id = "lantern", Name = "Lantern", Role = PartRole.Auxiliary, Model = "lantern" });
        AddPart(d, new PartType { Index = d.Parts.Count, Id = "input_crate", Name = "Input crate", Role = PartRole.Input, Model = "input_crate" });
        AddPart(d, new PartType { Index = d.Parts.Count, Id = "output_crate", Name = "Output crate", Role = PartRole.Output, Model = "output_crate" });

        // Recipes: parse, then compute tiers (needs every recipe) for default labour.
        var raw = new List<(JsonElement E, ItemType Output, List<Ingredient> Inputs, RoomType W, PartType S, int Edu)>();
        foreach (var e in root.GetProperty("recipes").EnumerateArray())
        {
            string outId = Str(e, "output");
            var output = ItemRef(d, outId, "recipe output");
            var w = d.WorkshopById.GetValueOrDefault(Str(e, "workshop")) ?? throw Bad($"recipe '{outId}': unknown workshop '{Str(e, "workshop")}'");
            string sid = Str(e, "station");
            var station = w.Stations.FirstOrDefault(s => s.Id == $"{w.Id}.{sid}") ?? throw Bad($"recipe '{outId}': workshop '{w.Id}' has no station '{sid}'");
            int edu = d.Levels.IndexOf(Str(e, "education"));
            if (edu < 0) throw Bad($"recipe '{outId}': unknown education '{Str(e, "education")}'");
            var inputs = new List<Ingredient>();
            foreach (var i in e.GetProperty("inputs").EnumerateArray())
            {
                string spec = i.GetString()!;
                int star = spec.IndexOf('*');
                string iid = star < 0 ? spec : spec[..star];
                int count = star < 0 ? 1 : int.Parse(spec[(star + 1)..], CultureInfo.InvariantCulture);
                if (count <= 0) throw Bad($"recipe '{outId}': bad count in '{spec}'");
                inputs.Add(new Ingredient(ItemRef(d, iid, $"recipe '{outId}' input"), count));
            }
            if (inputs.Count == 0) throw Bad($"recipe '{outId}' has no inputs");
            if (d.RecipeByOutput.ContainsKey(output) || raw.Any(r => r.Output == output)) throw Bad($"item '{outId}' has two recipes");
            if (d.Items[output.Index].IsRaw) throw Bad($"raw resource '{outId}' has a recipe");
            raw.Add((e, output, inputs, w, station, edu));
        }
        var byOutput = raw.ToDictionary(r => r.Output);
        var tiers = new Dictionary<ItemType, int>();
        int Tier(ItemType t, int depth)
        {
            if (tiers.TryGetValue(t, out int known)) return known;
            if (depth > 200) throw Bad($"recipe cycle through '{d.Items[t.Index].Id}'");
            int tier = byOutput.TryGetValue(t, out var r) ? 1 + r.Inputs.Max(i => Tier(i.Item, depth + 1)) : 0;
            tiers[t] = tier;
            return tier;
        }
        foreach (var item in d.Items)
        {
            item.Tier = Tier(item.Type, 0);
            if (!item.IsRaw && !byOutput.ContainsKey(item.Type)) throw Bad($"item '{item.Id}' is neither raw nor made by any recipe");
        }
        foreach (var (e, output, inputs, w, station, edu) in raw)
        {
            var recipe = new Recipe
            {
                Index = d.Recipes.Count,
                Output = output,
                OutCount = Int(e, "count") ?? 1,
                Inputs = inputs,
                WorkTicks = Int(e, "ticks") ?? 80 + 30 * tiers[output],
                Education = edu,
                Workshop = w,
                Station = station,
            };
            d.Recipes.Add(recipe);
            d.RecipeByOutput[output] = recipe;
            w.Recipes.Add(recipe);
            station.Recipes.Add(recipe);
        }
        foreach (var p in d.Parts)
            if (p.Role == PartRole.Machine && p.Recipes.Count == 0) throw Bad($"station '{p.Id}' has no recipes");
        return d;
    }

    private static void AddPart(Data d, PartType p)
    {
        if (!d.PartById.TryAdd(p.Id, p)) throw Bad($"part '{p.Id}' is defined twice");
        d.Parts.Add(p);
    }

    private static ItemType ItemRef(Data d, string id, string where) =>
        d.ItemById.TryGetValue(id, out var t) ? t : throw Bad($"{where}: unknown item '{id}'");

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.GetString() is { } s ? s : throw Bad($"missing '{name}' in {e}");
    private static string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetInt32() : null;

    private static uint ParseColor(string hex, string owner)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint c))
            throw Bad($"'{owner}': bad colour '{hex}'");
        return c;
    }

    private static InvalidDataException Bad(string message) => new($"tech-tree.json: {message}");
}

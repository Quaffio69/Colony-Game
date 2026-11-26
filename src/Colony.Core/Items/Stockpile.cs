using Colony.Core.World;

namespace Colony.Core.Items;

/// <summary>Which workshops may draw goods from a stockpile for their input crates.</summary>
public enum WorkshopAccess : byte
{
    /// <summary>Every workshop of its colony.</summary>
    All,
    /// <summary>Only the workshops listed (a whitelist).</summary>
    Only,
    /// <summary>Every workshop but those listed (a blacklist).</summary>
    Except,
}

/// <summary>What a <see cref="Commands.ConfigureStockpile"/> changes.</summary>
public enum StockpileOption : byte
{
    /// <summary>Its label (<c>Text</c>).</summary>
    Name,
    /// <summary>1 (filled first) to 5 (last) (<c>Value</c>).</summary>
    Priority,
    /// <summary>How far it draws loose goods from, in tiles (<c>Value</c>; 0 = no limit).</summary>
    Reach,
    /// <summary>At most this many of each item (<c>Value</c>; 0 = no cap).</summary>
    Cap,
    /// <summary>Kept for workshops and trade: colonists don't eat, burn, wear or furnish from it (<c>Value</c> 1/0).</summary>
    Reserve,
    /// <summary>Which workshops may draw from it (<c>Value</c>: a <see cref="WorkshopAccess"/>).</summary>
    Access,
    /// <summary>A workshop (room id in <c>Value</c>) on (<c>Arg</c> 1) or off (0) its list.</summary>
    Workshop,
    /// <summary>One item (id in <c>Text</c>) accepted (<c>Value</c> 1) or not (0).</summary>
    Item,
    /// <summary>Every item of a category (in <c>Text</c>) accepted (<c>Value</c> 1) or not (0).</summary>
    Category,
    /// <summary>Every item accepted (<c>Value</c> 1) or none (0).</summary>
    Everything,
}

/// <summary>
/// One stockpile: a group of zoned cells with its own settings. Zoning a box makes one; zoning against an existing one
/// (touching it, 8-way or a level up or down) extends it. Haulers fill stockpiles in priority order (1 first, then the
/// nearest), bring each only the goods it accepts (up to its cap of each, from within its reach), move goods up from a
/// lower-priority stockpile when a better one takes them, and haul out goods it no longer accepts. Workshops' input
/// crates draw only from stockpiles that let them; a reserve stockpile isn't eaten, burned, worn or furnished from.
/// Trade depots are not stockpiles (they keep their room for deliveries).
/// </summary>
public sealed class Stockpile
{
    public const int DefaultPriority = 3, LowestPriority = 5;
    /// <summary>Default haul-from distance, in tiles.</summary>
    public const int DefaultReach = 500;
    public const int MaxNameLength = 32;

    public int Id { get; }
    public int Faction { get; }
    public string Name { get; internal set; }
    /// <summary>1 (filled first) .. <see cref="LowestPriority"/> (last).</summary>
    public int Priority { get; internal set; } = DefaultPriority;
    /// <summary>How far away (straight-line tiles) it draws loose goods from; 0 = no limit.</summary>
    public int Reach { get; internal set; } = DefaultReach;
    /// <summary>At most this many of each item; 0 = no cap.</summary>
    public int Cap { get; internal set; }
    /// <summary>Kept for workshops and trade: colonists don't eat, burn, wear or furnish from it.</summary>
    public bool Reserve { get; internal set; }
    public WorkshopAccess Access { get; internal set; }

    private readonly HashSet<int> _workshops = new();
    /// <summary>The workshops (room ids) its <see cref="Access"/> list names, in id order.</summary>
    public IReadOnlyList<int> Workshops => _workshops.Order().ToList();

    private readonly HashSet<ushort> _denied = new();
    internal readonly HashSet<TilePos> Cells = new();
    public int CellCount => Cells.Count;

    public Stockpile(int id, int faction, string? name = null)
    {
        Id = id;
        Faction = faction;
        Name = name ?? $"Stockpile {id}";
    }

    public bool Accepts(ItemType type) => !_denied.Contains(type.Index);

    /// <summary>Whether the workshop (room id) may draw from it.</summary>
    public bool Lets(int roomId) => Access switch
    {
        WorkshopAccess.Only => _workshops.Contains(roomId),
        WorkshopAccess.Except => !_workshops.Contains(roomId),
        _ => true,
    };

    public bool ListsWorkshop(int roomId) => _workshops.Contains(roomId);

    /// <summary>Whether goods at <paramref name="from"/> are within its reach of the cell <paramref name="to"/>.</summary>
    public bool InReach(TilePos from, TilePos to)
    {
        if (Reach <= 0) return true;
        long dx = from.X - to.X, dy = from.Y - to.Y, dz = from.Z - to.Z;
        return dx * dx + dy * dy + dz * dz <= (long)Reach * Reach;
    }

    /// <summary>Units of <paramref name="type"/> it holds or has coming.</summary>
    public int Held(ItemType type, ItemPiles items)
    {
        int n = 0;
        foreach (var c in Cells)
            if (items.TryGet(c, out var p) && p.Type == type) n += p.Count + p.Incoming;
        return n;
    }

    /// <summary>How many more of <paramref name="type"/> it takes, given what it holds (int.MaxValue without a cap; 0 if refused).</summary>
    public int Takes(ItemType type, int held) => !Accepts(type) ? 0 : Cap <= 0 ? int.MaxValue : Math.Max(0, Cap - held);

    /// <summary>The filter, workshop list and name as one number, for <see cref="Net.StateHash"/> (no HashCode: it varies by process).</summary>
    public long Fingerprint
    {
        get
        {
            ulong h = 14695981039346656037UL;
            void Mix(long v) => h = (h ^ (ulong)v) * 1099511628211UL;
            foreach (ushort i in _denied.Order()) Mix(i);
            Mix(-1);
            foreach (int id in _workshops.Order()) Mix(id);
            Mix(-2);
            foreach (char c in Name) Mix(c);
            return (long)h;
        }
    }

    /// <summary>Applies a change; false if it changed nothing.</summary>
    internal bool Configure(StockpileOption option, int value, int arg, string text)
    {
        switch (option)
        {
            case StockpileOption.Name:
                string name = text.Trim();
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                if (name.Length == 0 || name == Name) return false;
                Name = name;
                return true;
            case StockpileOption.Priority:
                return Set(Priority, Math.Clamp(value, 1, LowestPriority), v => Priority = v);
            case StockpileOption.Reach:
                return Set(Reach, Math.Max(0, value), v => Reach = v);
            case StockpileOption.Cap:
                return Set(Cap, Math.Max(0, value), v => Cap = v);
            case StockpileOption.Reserve:
                if (Reserve == (value != 0)) return false;
                Reserve = value != 0;
                return true;
            case StockpileOption.Access:
                var access = (WorkshopAccess)Math.Clamp(value, 0, 2);
                if (Access == access) return false;
                Access = access;
                return true;
            case StockpileOption.Workshop:
                return arg != 0 ? _workshops.Add(value) : _workshops.Remove(value);
            case StockpileOption.Item:
                if (!Industry.Catalog.TryItem(text, out var item)) return false;
                return value != 0 ? _denied.Remove(item.Index) : _denied.Add(item.Index);
            case StockpileOption.Category:
            {
                bool changed = false;
                foreach (var def in Industry.Catalog.Items)
                    if (def.Category == text) changed |= value != 0 ? _denied.Remove(def.Type.Index) : _denied.Add(def.Type.Index);
                return changed;
            }
            case StockpileOption.Everything:
            {
                bool changed = false;
                foreach (var def in Industry.Catalog.Items)
                    changed |= value != 0 ? _denied.Remove(def.Type.Index) : _denied.Add(def.Type.Index);
                return changed;
            }
        }
        return false;
    }

    private static bool Set(int current, int value, Action<int> set)
    {
        if (current == value) return false;
        set(value);
        return true;
    }

    internal void SnapSave(BinaryWriter w)
    {
        w.Write(Id); w.Write(Faction); w.Write(Name); w.Write((byte)Priority); w.Write(Reach); w.Write(Cap); w.Write(Reserve); w.Write((byte)Access);
        w.Write(_workshops.Count);
        foreach (int id in _workshops.Order()) w.Write(id);
        w.Write(_denied.Count);
        foreach (ushort i in _denied.Order()) w.Write(i);
    }

    internal static Stockpile SnapLoad(BinaryReader r)
    {
        var s = new Stockpile(r.ReadInt32(), r.ReadInt32(), r.ReadString())
        {
            Priority = r.ReadByte(), Reach = r.ReadInt32(), Cap = r.ReadInt32(), Reserve = r.ReadBoolean(), Access = (WorkshopAccess)r.ReadByte(),
        };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) s._workshops.Add(r.ReadInt32());
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) s._denied.Add(r.ReadUInt16());
        return s;
    }
}

using Colony.Core.Agents;
using Colony.Core.Nature;
using Colony.Core.Orders;
using Colony.Core.World;

namespace Colony.Core.Commands;

/// <summary>A command and the tick it was applied before.</summary>
public readonly record struct LoggedCommand(long Tick, Command Command);

/// <summary>
/// Player commands for a sim. <see cref="Submit"/> queues; <see cref="Flush"/> applies everything queued before the
/// next tick (the sim also flushes at the start of every <see cref="ColonySim.Step"/>), in submission order, and logs
/// each with the tick it ran before. Replaying a log on the same starting world reproduces the game exactly
/// (<see cref="Replay"/>); lockstep multiplayer will schedule remote commands for a tick the same way.
/// </summary>
public sealed class CommandQueue
{
    private readonly ColonySim _sim;
    private readonly List<Command> _pending = new();
    private readonly List<LoggedCommand> _log = new();

    public CommandQueue(ColonySim sim) => _sim = sim;

    /// <summary>Every command applied so far, with its tick.</summary>
    public IReadOnlyList<LoggedCommand> Log => _log;
    /// <summary>Keep the log (on by default; a very long game could turn it off).</summary>
    public bool Recording { get; set; } = true;
    /// <summary>Fires for each applied command with its message (the UI shows the local player's).</summary>
    public event Action<Command, string>? Applied;

    public void Submit(Command command) => _pending.Add(command);

    /// <summary>Applies everything queued now (before the next tick). Returns the last message, if any.</summary>
    public string? Flush()
    {
        string? last = null;
        if (_pending.Count == 0) return null;
        var batch = _pending.ToArray();
        _pending.Clear();
        foreach (var c in batch) last = Run(c);
        return last;
    }

    /// <summary>Submits and applies at once (single player, or a command already agreed for this tick).</summary>
    public string Execute(Command command)
    {
        Flush();
        return Run(command);
    }

    private string Run(Command c)
    {
        string message = c.Apply(_sim);
        if (Recording) _log.Add(new LoggedCommand(_sim.Tick, c));
        Applied?.Invoke(c, message);
        return message;
    }

    /// <summary>
    /// Plays a recorded game on a fresh sim made the same way as the original: steps to each command's tick, applies
    /// it, and steps on to <paramref name="untilTick"/>.
    /// </summary>
    public static void Replay(ColonySim sim, IEnumerable<LoggedCommand> log, long untilTick)
    {
        foreach (var (tick, command) in log)
        {
            while (sim.Tick < tick) sim.Step();
            sim.Commands.Execute(command);
        }
        while (sim.Tick < untilTick) sim.Step();
    }
}

/// <summary>Binary form of commands (for the network and saved replays): a tag byte, the player, then the fields.</summary>
public static class CommandCodec
{
    private enum Tag : byte
    {
        DesignateBox = 1, DesignateStairs, DesignatePlants, CancelPlants, CancelOrders, AddStockpile, AddFarm, PlaceWalls,
        CancelWalls, DebugCompleteAll, PaintRoom, EraseRoom, PlacePart, RemoveParts, CycleRecipe, PlaceStructure,
        RemoveStructures, PlaceEdge, RemoveEdge, EdgeLine, PlaceOutline, ClearWalls, Covers, SetWorkPriority, SetHomePolicy,
        SetPolicyTier, SetPolicyPerResident, SetPolicyShared, AddPolicy, SettleGame, SetDrafted, SquadMove, SquadAttack, DebugSpawnNest, PlaceRacks,
    }

    public static byte[] EncodeLog(IEnumerable<LoggedCommand> log)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var list = log.ToList();
        w.Write(list.Count);
        foreach (var (tick, c) in list)
        {
            w.Write(tick);
            Write(w, c);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static List<LoggedCommand> DecodeLog(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadInt32();
        var list = new List<LoggedCommand>(n);
        for (int i = 0; i < n; i++)
        {
            long tick = r.ReadInt64();
            list.Add(new LoggedCommand(tick, Read(r)));
        }
        return list;
    }

    public static byte[] Encode(Command c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        Write(w, c);
        w.Flush();
        return ms.ToArray();
    }

    public static Command Decode(ReadOnlySpan<byte> data) => Read(new BinaryReader(new MemoryStream(data.ToArray())));

    private static void P(BinaryWriter w, TilePos p) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
    private static TilePos P(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    public static void Write(BinaryWriter w, Command c)
    {
        void Head(Tag t) { w.Write((byte)t); w.Write(c.Player); }
        switch (c)
        {
            case DesignateBox x: Head(Tag.DesignateBox); P(w, x.A); P(w, x.B); w.Write((byte)x.Kind); break;
            case DesignateStairs x: Head(Tag.DesignateStairs); P(w, x.A); P(w, x.B); break;
            case DesignatePlants x: Head(Tag.DesignatePlants); w.Write((byte)x.Kind); w.Write(x.X0); w.Write(x.Y0); w.Write(x.X1); w.Write(x.Y1); break;
            case CancelPlants x: Head(Tag.CancelPlants); w.Write((byte)x.Kind); w.Write(x.X0); w.Write(x.Y0); w.Write(x.X1); w.Write(x.Y1); break;
            case CancelOrders x: Head(Tag.CancelOrders); P(w, x.A); P(w, x.B); break;
            case AddStockpile x: Head(Tag.AddStockpile); P(w, x.A); P(w, x.B); break;
            case AddFarm x: Head(Tag.AddFarm); w.Write((int)x.Crop); P(w, x.A); P(w, x.B); break;
            case PlaceWalls x: Head(Tag.PlaceWalls); P(w, x.A); P(w, x.B); break;
            case CancelWalls x: Head(Tag.CancelWalls); P(w, x.A); P(w, x.B); break;
            case DebugCompleteAll: Head(Tag.DebugCompleteAll); break;
            case PaintRoom x: Head(Tag.PaintRoom); w.Write(x.RoomTypeId); P(w, x.A); P(w, x.B); break;
            case EraseRoom x: Head(Tag.EraseRoom); P(w, x.A); P(w, x.B); break;
            case PlacePart x: Head(Tag.PlacePart); w.Write(x.PartId); P(w, x.Origin); w.Write(x.Rotated); w.Write(x.Size); break;
            case RemoveParts x: Head(Tag.RemoveParts); P(w, x.A); P(w, x.B); break;
            case CycleRecipe x: Head(Tag.CycleRecipe); P(w, x.Cell); w.Write(x.Back); break;
            case PlaceStructure x: Head(Tag.PlaceStructure); w.Write((byte)x.Kind); P(w, x.Cell); w.Write(x.Facing); break;
            case RemoveStructures x: Head(Tag.RemoveStructures); P(w, x.A); P(w, x.B); break;
            case PlaceEdge x: Head(Tag.PlaceEdge); w.Write((byte)x.Piece); P(w, x.Cell); w.Write(x.Side); w.Write(x.Material); break;
            case RemoveEdge x: Head(Tag.RemoveEdge); P(w, x.Cell); w.Write(x.Side); break;
            case EdgeLine x: Head(Tag.EdgeLine); w.Write((byte)x.Piece); P(w, x.Start); w.Write(x.Side); w.Write(x.To); w.Write(x.Material); w.Write(x.Remove); break;
            case PlaceOutline x: Head(Tag.PlaceOutline); P(w, x.A); P(w, x.B); w.Write(x.Material); break;
            case ClearWalls x: Head(Tag.ClearWalls); P(w, x.A); P(w, x.B); break;
            case Covers x: Head(Tag.Covers); w.Write((byte)x.Kind); P(w, x.A); P(w, x.B); w.Write(x.Material); w.Write(x.Remove); break;
            case SetWorkPriority x: Head(Tag.SetWorkPriority); w.Write(x.ColonistId); w.Write((byte)x.Work); w.Write(x.Priority); break;
            case SetHomePolicy x: Head(Tag.SetHomePolicy); P(w, x.Cell); w.Write(x.Policy); break;
            case SetPolicyTier x: Head(Tag.SetPolicyTier); w.Write(x.Policy); w.Write(x.Slot); w.Write(x.Tier); break;
            case SetPolicyPerResident x: Head(Tag.SetPolicyPerResident); w.Write(x.Policy); w.Write(x.Slot); w.Write(x.PerResident); break;
            case SetPolicyShared x: Head(Tag.SetPolicyShared); w.Write(x.Policy); w.Write(x.Shared); break;
            case AddPolicy: Head(Tag.AddPolicy); break;
            case SettleGame: Head(Tag.SettleGame); break;
            case SetDrafted x: Head(Tag.SetDrafted); w.Write(x.ColonistId); w.Write(x.On); break;
            case SquadMove x: Head(Tag.SquadMove); P(w, x.Target); break;
            case SquadAttack x: Head(Tag.SquadAttack); w.Write(x.NestId); break;
            case DebugSpawnNest x: Head(Tag.DebugSpawnNest); P(w, x.At); w.Write(x.Population); w.Write(x.Angry); break;
            case PlaceRacks x: Head(Tag.PlaceRacks); P(w, x.A); P(w, x.B); w.Write(x.Facing); break;
            default: throw new NotSupportedException($"No codec for {c.GetType().Name}");
        }
    }

    public static Command Read(BinaryReader r)
    {
        var tag = (Tag)r.ReadByte();
        int player = r.ReadInt32();
        Command c = tag switch
        {
            Tag.DesignateBox => new DesignateBox(P(r), P(r), (DesignationKind)r.ReadByte()),
            Tag.DesignateStairs => new DesignateStairs(P(r), P(r)),
            Tag.DesignatePlants => new DesignatePlants((DesignationKind)r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
            Tag.CancelPlants => new CancelPlants((DesignationKind)r.ReadByte(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
            Tag.CancelOrders => new CancelOrders(P(r), P(r)),
            Tag.AddStockpile => new AddStockpile(P(r), P(r)),
            Tag.AddFarm => new AddFarm((PlantKind)r.ReadInt32(), P(r), P(r)),
            Tag.PlaceWalls => new PlaceWalls(P(r), P(r)),
            Tag.CancelWalls => new CancelWalls(P(r), P(r)),
            Tag.DebugCompleteAll => new DebugCompleteAll(),
            Tag.PaintRoom => new PaintRoom(r.ReadString(), P(r), P(r)),
            Tag.EraseRoom => new EraseRoom(P(r), P(r)),
            Tag.PlacePart => new PlacePart(r.ReadString(), P(r), r.ReadBoolean(), r.ReadInt32()),
            Tag.RemoveParts => new RemoveParts(P(r), P(r)),
            Tag.CycleRecipe => new CycleRecipe(P(r), r.ReadBoolean()),
            Tag.PlaceStructure => new PlaceStructure((StructureKind)r.ReadByte(), P(r), r.ReadInt32()),
            Tag.RemoveStructures => new RemoveStructures(P(r), P(r)),
            Tag.PlaceEdge => new PlaceEdge((StructureKind)r.ReadByte(), P(r), r.ReadInt32(), r.ReadInt32()),
            Tag.RemoveEdge => new RemoveEdge(P(r), r.ReadInt32()),
            Tag.EdgeLine => new EdgeLine((StructureKind)r.ReadByte(), P(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            Tag.PlaceOutline => new PlaceOutline(P(r), P(r), r.ReadInt32()),
            Tag.ClearWalls => new ClearWalls(P(r), P(r)),
            Tag.Covers => new Covers((StructureKind)r.ReadByte(), P(r), P(r), r.ReadInt32(), r.ReadBoolean()),
            Tag.SetWorkPriority => new SetWorkPriority(r.ReadInt32(), (Work)r.ReadByte(), r.ReadInt32()),
            Tag.SetHomePolicy => new SetHomePolicy(P(r), r.ReadInt32()),
            Tag.SetPolicyTier => new SetPolicyTier(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
            Tag.SetPolicyPerResident => new SetPolicyPerResident(r.ReadInt32(), r.ReadInt32(), r.ReadSingle()),
            Tag.SetPolicyShared => new SetPolicyShared(r.ReadInt32(), r.ReadBoolean()),
            Tag.AddPolicy => new AddPolicy(),
            Tag.SettleGame => new SettleGame(),
            Tag.SetDrafted => new SetDrafted(r.ReadInt32(), r.ReadBoolean()),
            Tag.SquadMove => new SquadMove(P(r)),
            Tag.SquadAttack => new SquadAttack(r.ReadInt32()),
            Tag.DebugSpawnNest => new DebugSpawnNest(P(r), r.ReadInt32(), r.ReadBoolean()),
            Tag.PlaceRacks => new PlaceRacks(P(r), P(r), r.ReadInt32()),
            _ => throw new InvalidDataException($"Unknown command tag {tag}"),
        };
        return c with { Player = player };
    }
}

using Colony.Core.Items;
using Colony.Core.Navigation;
using Colony.Core.World;

namespace Colony.Core.Combat;

/// <summary>
/// How something fights: damage per blow, ticks between blows, reach in tiles (more than 2 is a ranged weapon that needs
/// a clear line), how many foes one blow hits (cleave) and how much armour it ignores (pierce).
/// </summary>
public readonly record struct Attack(int Damage, int Cooldown, int Reach, int Cleave, int Pierce)
{
    public bool Ranged => Reach > 2;
    /// <summary>Rough damage per second against an unarmoured crowd (for picking the better weapon).</summary>
    public float Rating => Damage * (1 + 0.6f * (Cleave - 1)) * ColonySim.TicksPerSecond / (float)Cooldown + Reach * 0.4f;
}

/// <summary>
/// Equipment (catalog items with a "gear" slot): one weapon (spear, bow, war axe, sword: each good at something
/// different), and two pieces of armour: body armour takes a fixed amount off every hit (strong against swarms of weak
/// biters), a shield blocks a share of blows outright (strong against heavy hits; not with two-handed weapons).
/// </summary>
public static class Gear
{
    public const string WeaponSlot = "weapon", BodySlot = "body", ShieldSlot = "shield";

    /// <summary>Bare hands.</summary>
    public static readonly Attack Fists = new(4, 20, 1, 1, 0);

    public static Attack Of(ItemType? weapon) => weapon is { } w
        ? new Attack(w.Def.Damage, w.Def.Cooldown, w.Def.Reach, w.Def.Cleave, w.Def.Pierce)
        : Fists;

    public static bool IsGear(ItemType t) => t.Def.Gear != null;

    /// <summary>How good a piece is in its slot (higher is better).</summary>
    public static float Score(ItemType t) => t.Def.Gear switch
    {
        WeaponSlot => Of(t).Rating,
        BodySlot => t.Def.Protect,
        ShieldSlot => t.Def.Block,
        _ => 0,
    };

    /// <summary>
    /// One blow: blocked outright by a shield (<paramref name="block"/> percent), else the damage less armour (what the
    /// weapon doesn't pierce). A blow that armour soaks entirely still gets through a quarter of the time for 1.
    /// </summary>
    public static int Resolve(int damage, int pierce, int protect, int block, float blockRoll, float chipRoll)
    {
        if (blockRoll * 100f < block) return 0;
        int d = damage - Math.Max(0, protect - pierce);
        return d > 0 ? d : chipRoll < 0.25f ? 1 : 0;
    }

    /// <summary>A deterministic roll in [0, 1) for a blow (same on every peer).</summary>
    public static float Roll(long tick, int attacker, int target, int salt) =>
        Procedural.Noise.Hash(unchecked((int)tick), attacker, target, salt);

    /// <summary>Within striking distance: horizontally within reach, and a level apart at most (ranged: three).</summary>
    public static bool InReach(TilePos at, TilePos target, int reach) =>
        Math.Max(Math.Abs(at.X - target.X), Math.Abs(at.Y - target.Y)) <= reach &&
        Math.Abs(at.Z - target.Z) <= (reach > 2 ? 3 : 1);

    /// <summary>
    /// A clear line between two agents' eyes: no solid cell and no thin wall or window crossed on the way (sampled every
    /// third of a tile).
    /// </summary>
    public static bool LineOfSight(VoxelWorld w, TilePos a, TilePos b)
    {
        float ax = a.X + 0.5f, ay = a.Y + 0.5f, az = a.Z + 0.6f;
        float bx = b.X + 0.5f, by = b.Y + 0.5f, bz = b.Z + 0.6f;
        float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
        int steps = Math.Max(1, (int)(len * 3));
        var prev = a;
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            var p = new TilePos((int)MathF.Floor(ax + (bx - ax) * t), (int)MathF.Floor(ay + (by - ay) * t), (int)MathF.Floor(az + (bz - az) * t));
            if (p == prev) continue;
            if (!w.InBounds(p) || TileInfo.IsSolid(w.Get(p))) return false;
            if (p.Z == prev.Z && Math.Abs(p.X - prev.X) + Math.Abs(p.Y - prev.Y) == 1 && Movement.EdgeBlocks(w, prev, p)) return false;
            prev = p;
        }
        return true;
    }
}

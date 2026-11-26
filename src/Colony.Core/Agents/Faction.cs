using Colony.Core.World;

namespace Colony.Core.Agents;

/// <summary>
/// A colony: one player's people (or an NPC's) in the shared world. It owns its designations, stockpiles, farm plots,
/// rooms and blueprints (see <see cref="Owners"/>, <see cref="Industry.Room.Faction"/>,
/// <see cref="Orders.Blueprint.Faction"/>); its colonists only work, eat, sleep and store goods within it (loose piles
/// are anyone's). Each has its own <see cref="Society"/> (settlers, mood, families) and <see cref="Housing"/>.
/// Multiplayer is nothing more than several of these in one world.
/// </summary>
public sealed class Faction
{
    public int Id { get; }
    public string Name { get; set; }
    /// <summary>Where its founders landed and newcomers arrive.</summary>
    public TilePos? Landing { get; set; }
    public Society Society { get; }
    public Housing Housing { get; }
    internal bool MagisterArrived;

    /// <summary>
    /// Who plays it online ("" while nobody has: a free colony). Set by the host (<see cref="Commands.ClaimColony"/>)
    /// when a player takes it, and kept in saves, so a saved game hosted again gives each player back their own colony.
    /// </summary>
    public string Player { get; internal set; } = "";
    /// <summary>A hash of that player's key (<see cref="Net.NetPlayer.KeyHash"/>): who they are, whatever name they use.</summary>
    public string PlayerKey { get; internal set; } = "";
    public bool Claimed => Player.Length > 0;

    public Faction(int id, string name)
    {
        Id = id;
        Name = name;
        Society = new Society { Faction = id };
        Housing = new Housing { Faction = id };
    }
}

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

    public Faction(int id, string name)
    {
        Id = id;
        Name = name;
        Society = new Society { Faction = id };
        Housing = new Housing { Faction = id };
    }
}

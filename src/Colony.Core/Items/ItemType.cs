namespace Colony.Core.Items;

/// <summary>Kinds of material. Items are counted, never individual objects (see <see cref="ItemPiles"/>).</summary>
public enum ItemType : byte
{
    Stone,
    Ore,
    Log,
    Plank,
    /// <summary>Gathered from berry bushes; food once needs exist.</summary>
    Berries,
    /// <summary>Gathered from reeds and shrubs; a future input for rope and cloth.</summary>
    Fiber,
}

public static class ItemInfo
{
    public const int TypeCount = 6;

    public static string Name(ItemType t) => t switch
    {
        ItemType.Stone => "Stone",
        ItemType.Ore => "Ore",
        ItemType.Log => "Logs",
        ItemType.Plank => "Planks",
        ItemType.Berries => "Berries",
        ItemType.Fiber => "Fiber",
        _ => t.ToString(),
    };

    /// <summary>Most units one tile's pile can hold.</summary>
    public static int StackLimit(ItemType t) => t switch
    {
        ItemType.Log => 20,
        ItemType.Plank => 40,
        ItemType.Berries or ItemType.Fiber => 30,
        _ => 25,
    };

    /// <summary>Units one colonist carries per trip.</summary>
    public static int CarryLimit(ItemType t) => t switch
    {
        ItemType.Log => 3,
        ItemType.Plank => 8,
        ItemType.Berries or ItemType.Fiber => 10,
        _ => 5,
    };
}

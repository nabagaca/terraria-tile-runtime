namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// High-level merge families for terrain-style tile framing.
    /// The runtime translates these into Terraria's internal tile flags and sets.
    /// </summary>
    public enum TileMergeCategory
    {
        Dirt,
        Stone,
        MergeAll,
        ForcedDirt,
        Clouds
    }
}

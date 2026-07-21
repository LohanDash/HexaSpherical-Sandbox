using Godot;

namespace HexaSphericalSandbox;

public sealed record BlockDefinition(int Id, string Name, string ShortName, Color Color, string TexturePath);

public static class BlockCatalog
{
    // TexturePath is intentionally part of the stable definition now. When
    // textures arrive, rendering and UI can load them without changing saves.
    public static readonly BlockDefinition[] Blocks =
    [
        new(0, "Grass Block", "GRASS", new Color(0.18f, 0.58f, 0.22f), "res://Textures/Blocks/grass.png"),
        new(1, "Dirt Block", "DIRT", new Color(0.34f, 0.19f, 0.08f), "res://Textures/Blocks/dirt.png"),
        new(2, "Stone Block", "STONE", new Color(0.42f, 0.44f, 0.47f), "res://Textures/Blocks/stone.png"),
        new(3, "Sand Block", "SAND", new Color(0.82f, 0.72f, 0.38f), "res://Textures/Blocks/sand.png"),
        new(4, "Snow Block", "SNOW", new Color(0.9f, 0.95f, 1f), "res://Textures/Blocks/snow.png"),
        new(5, "Purple Block", "PURPLE", new Color(0.62f, 0.2f, 0.75f), "res://Textures/Blocks/purple.png")
    ];

    public static BlockDefinition Get(int id) => Blocks[Mathf.Clamp(id, 0, Blocks.Length - 1)];
}

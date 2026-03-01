# Shared Tile Runtime Usage

## Goal

Use `TerrariaModder.TileRuntime` from a regular mod so custom tile registration, texture loading, callbacks, and save/load support all come from the shared `tile-runtime` dependency mod.

This is the supported path for building mods against the shared tile runtime.

## Install The Runtime Dependency

Your mod manifest should declare a dependency on `tile-runtime`:

```json
{
  "dependencies": ["tile-runtime"]
}
```

At runtime, the dependency mod should be installed as:

```text
TerrariaModder/mods/tile-runtime/
  manifest.json
  TileRuntimeBootstrap.dll
  TerrariaModder.TileRuntime.dll
```

## Reference The Runtime Assembly

Your mod project should reference both `TerrariaModder.Core` and `TerrariaModder.TileRuntime`.

Typical project reference pattern:

```xml
<ItemGroup>
  <Reference Include="TerrariaModder.Core">
    <HintPath>$(TerrariaModderDeployRoot)\core\TerrariaModder.Core.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="TerrariaModder.TileRuntime">
    <HintPath>$(TerrariaModderDeployRoot)\mods\tile-runtime\TerrariaModder.TileRuntime.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

## Registration Pattern

Register tiles from `Initialize(ModContext context)` by calling `context.UseTileRuntime()`.

```csharp
using TerrariaModder.Core;
using TerrariaModder.TileRuntime;

public class Mod : IMod
{
    public string Id => "example-mod";
    public string Name => "Example Mod";
    public string Version => "1.0.0";

    public void Initialize(ModContext context)
    {
        var tiles = context.UseTileRuntime();

        tiles.RegisterTile("example-tile", new TileDefinition
        {
            DisplayName = "Example Tile",
            TexturePath = @"Assets\Tiles\example-tile.png",
            Width = 1,
            Height = 1,
            Solid = true,
            Brick = true,
            MergeCategories = new[] { TileMergeCategory.Dirt, TileMergeCategory.Stone },
            MergeWith = new[] { "Stone", "GrayBrick" },
            FrameImportant = false,
            MapColorR = 120,
            MapColorG = 180,
            MapColorB = 220
        });
    }

    public void OnWorldLoad() { }
    public void OnWorldUnload() { }
    public void Unload() { }
}
```

## Public API

The public entry points exposed by this repository are:

- `context.UseTileRuntime()`
- `TileRuntimeModContext.RegisterTile(string tileName, TileDefinition definition)`
- `TileRuntimeModContext.GetTiles()`
- `TileRuntimeApi.ResolveTile(string tileRef)`
- `TileRuntimeApi.TryGetTileType(string fullId, out int tileType)`
- `TileRuntimeApi.GetTilesForMod(string modId)`

`ResolveTile` accepts:

- a full runtime ID like `"example-mod:example-tile"`
- a numeric tile ID string like `"1"`
- a vanilla tile name like `"Dirt"` or `"Dirt Block"` if it exists in `Terraria.ID.TileID`

## Tile ID Lifecycle

Registration and ID assignment are separate stages.

1. Your mod registers tile definitions during `Initialize`.
2. The `tile-runtime` bootstrap mod freezes registrations during its `OnGameReady`.
3. Runtime tile IDs are assigned deterministically in sorted `modId:tileName` order.
4. Textures, tile flags, object data, and behavior patches are applied after IDs exist.

Implication:

- register tiles during `Initialize`
- do not rely on runtime tile IDs before the game-ready phase
- resolve the tile type lazily when another system needs it

## Texture Loading

`TexturePath` is resolved relative to the mod folder registered through `ModContext.ModFolder`.

Example:

- `TexturePath = @"Assets\Tiles\example-tile.png"`
- deployed file path: `TerrariaModder/mods/example-mod/Assets/Tiles/example-tile.png`

If the texture file is missing, the runtime logs a warning and injects a placeholder texture instead.

## TileDefinition Reference

`TileDefinition` currently exposes these groups of settings.

### Basic display and texture

- `DisplayName`
- `TexturePath`
- `MapColorR`
- `MapColorG`
- `MapColorB`

### Tile flags

- `Solid`
- `SolidTop`
- `Brick`
- `NoAttach`
- `Table`
- `Lighted`
- `LavaDeath`
- `FrameImportant`
- `NoFail`
- `Cut`
- `MergeCategories`
- `DisableSmartCursor`
- `MergeWith`

For 1x1 terrain-style blocks that should merge like dirt/stone/brick, use `FrameImportant = false`. `true` is appropriate for furniture and other tiles that use fixed sprite framing.

`MergeCategories` is the preferred API for built-in merge families. Use `MergeWith` for explicit tile-to-tile merges such as `"GrayBrick"` or another custom tile ID.

### Multi-tile object layout

- `Width`
- `Height`
- `OriginX`
- `OriginY`
- `CoordinateWidth`
- `CoordinatePadding`
- `CoordinateHeights`
- `StyleHorizontal`
- `StyleWrapLimit`
- `StyleMultiplier`

For `Width == 1` and `Height == 1`, the runtime does not register `TileObjectData`.

For multi-tiles, the runtime clones a vanilla `TileObjectData.Style{Width}x{Height}` template. In practice that means widths and heights should match sizes Terraria already supports with built-in style templates.

### Container settings

- `IsContainer`
- `RegisterAsBasicChest`
- `ContainerInteractable`
- `ContainerRequiresEmptyToBreak`
- `ContainerCapacity`
- `ContainerName`
- `DropItemId`

If `IsContainer` is true, the runtime creates and restores chest storage automatically and patches interaction range checks for the custom multi-tile.

### Callbacks

- `Func<object, int, int, bool> OnRightClick`
- `Action<int, int> OnPlace`
- `Action<int, int> OnBreak`

Callback semantics:

- `OnRightClick(player, x, y)` runs from the tile interaction hook
- return `true` from `OnRightClick` to consume the interaction
- `OnPlace(x, y)` receives the top-left tile coordinate for multi-tiles
- `OnBreak(x, y)` runs after the custom tile is removed

## Validation Rules

Registration fails if:

- `DisplayName` is missing
- `Width <= 0` or `Height <= 0`
- `CoordinateWidth <= 0`
- `CoordinatePadding < 0`
- `StyleMultiplier <= 0`
- `ContainerCapacity < 1`
- the same `modId:tileName` is registered twice
- registration happens after the runtime has frozen registrations

## Runtime Resolution In Other Systems

If another part of your mod needs the tile type, resolve it through the runtime instead of hard-coding IDs:

```csharp
int tileType = TileRuntimeApi.ResolveTile("example-mod:example-tile");
```

If you need to detect whether the tile has been assigned yet:

```csharp
if (TileRuntimeApi.TryGetTileType("example-mod:example-tile", out int tileType))
{
    // Use tileType
}
```

## Save And Load Behavior

Custom tiles are not written directly into the vanilla world format.

Instead, the runtime:

- extracts custom tiles before world save
- writes them to sidecar moddata files
- restores them after the vanilla save completes
- reloads them after world load

Container contents are also serialized separately for custom container tiles.

## Current Constraints

These are current implementation constraints, not generic modding rules:

- tile registration must happen before `tile-runtime` reaches `OnGameReady`
- multi-tile registration depends on Terraria having a matching `TileObjectData.Style{Width}x{Height}` template
- `DropItemId` uses `TerrariaModder.Core.Assets.ItemRegistry`, so only use it if the target item is resolvable there
- right-click callbacks receive the player as `object`, so cast only if your mod already references Terraria types
- if texture injection happens before graphics reflection is ready, the runtime retries during post-update until it succeeds

## Verification Checklist

After integrating the runtime into a mod:

1. Confirm the manifest includes `"tile-runtime"` as a dependency.
2. Confirm the mod registers tiles from `Initialize`.
3. Confirm the log shows runtime initialization, tile ID assignment, and texture injection.
4. Place and break each custom tile.
5. Save and reload a world containing the custom tile.
6. If using containers, verify open/close, stored items, and break protection behavior.

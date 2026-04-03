# Agent Instructions

This file provides guidance for AI coding agents working in the `terraria-tile-runtime` repository.

## Repository Overview

`terraria-tile-runtime` is a standalone dependency mod for TerrariaModder tile mods. It provides:

- Deterministic runtime tile ID assignment
- Tile texture injection
- `TileObjectData` registration for multi-tile objects
- Right-click, place, and break callbacks
- Custom container support
- Save/load persistence for custom tiles

The runtime ships as a single mod folder deployed into `TerrariaModder/mods/tile-runtime/`.

## Repository Structure

```text
src/
  TileRuntime/              # Shared support assembly (TerrariaModder.TileRuntime.dll)
  TileRuntimeBootstrap/     # Mod entry assembly (TileRuntimeBootstrap.dll)
ref/
  0Harmony/                 # Reference stub for Harmony (used when deployed DLL is absent)
  Terraria/                 # Reference stub for Terraria.exe
  TerrariaModder.Core/      # Reference stub for TerrariaModder.Core.dll
docs/
  shared-tile-runtime-usage.md   # Usage guide for mod authors
  example-mod.md                 # Full example mod walkthrough
  examples/
    ExampleTileMod/              # Compilable example mod project
Directory.Build.targets          # Shared MSBuild deploy targets
```

## Build

The projects target `net48` and depend on `Terraria.exe`, `TerrariaModder.Core.dll`, and `0Harmony.dll`. The `ref/` stubs allow the solution to compile in CI without those binaries installed.

Build the runtime library:

```powershell
dotnet build src/TileRuntime/TileRuntime.csproj -c Release
```

Build the bootstrap (mod entry) assembly:

```powershell
dotnet build src/TileRuntimeBootstrap/TileRuntimeBootstrap.csproj -c Release
```

Override deployment paths if needed:

```powershell
dotnet build src/TileRuntimeBootstrap/TileRuntimeBootstrap.csproj -c Release `
  /p:TerrariaInstallDir="D:\Games\Terraria" `
  /p:TerrariaModderDeployRoot="D:\Games\Terraria\TerrariaModder"
```

Build output goes to `build/plugins/` (excluded from source control via `.gitignore`).

## Code Conventions

- **Target framework**: `net48`, language version `latest`.
- **Namespace**: `TerrariaModder.TileRuntime` for public API types in `src/TileRuntime/`.
- **Assembly names**: `TerrariaModder.TileRuntime` (library) and `TileRuntimeBootstrap` (entry mod).
- Public API surface lives in `TileRuntimeApi.cs`, `TileRuntimeExtensions.cs`, and `TileRuntimeModContext.cs`. Keep the public surface minimal and stable.
- Patches use Harmony and live in files named `*Patches.cs`.
- Registration and ID assignment are two separate stages; never conflate them.
- Do not hard-code tile IDs. Always resolve via `TileRuntimeApi.ResolveTile` or `TileRuntimeApi.TryGetTileType`.

## Key Constraints

- Tile registration must happen before `tile-runtime` reaches `OnGameReady`.
- Multi-tile registration relies on Terraria having a matching `TileObjectData.Style{Width}x{Height}` template.
- `DropItemId` uses `TerrariaModder.Core.Assets.ItemRegistry`, so it only works for items resolvable there.
- Runtime tile IDs are assigned deterministically in sorted `modId:tileName` order after all mods have called `Initialize`.
- Textures are loaded after IDs are assigned; the loader retries during post-update if graphics reflection is not ready.

## Testing

There is no automated test suite. Validation is done manually:

1. Build both projects and deploy to a TerrariaModder installation.
2. Launch Terraria with TerrariaModder active and load a world.
3. Confirm the log shows runtime initialization, tile ID assignment, and texture injection.
4. Place and break each custom tile.
5. Save and reload the world.
6. For container tiles, verify open/close, stored items, and break-protection behaviour.

When writing or reviewing code, mentally trace through the Tile ID Lifecycle:
`Initialize` → freeze (`OnGameReady`) → ID assignment → texture/flag/object-data application.

## Documentation

Developer-facing docs are in `docs/`. Update them when changing the public API or registration semantics. The primary references are:

- `docs/shared-tile-runtime-usage.md` — usage guide for mod authors
- `docs/example-mod.md` — end-to-end example with code

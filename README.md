# Terraria Tile Runtime

Standalone repository for the shared `tile-runtime` dependency mod used by TerrariaModder tile mods.

## What It Does

`tile-runtime` lets mods register custom tile definitions at startup without requiring tile-specific patches inside `TerrariaModder.Core`.

The runtime owns:

- deterministic runtime tile ID assignment
- tile texture injection
- `TileObjectData` registration for multi-tile objects
- right-click, place, and break callbacks
- custom container support
- save/load persistence for custom tiles

## For Mod Authors

Start with these guides:

- [Shared Tile Runtime Usage](/C:/Users/Aiden/code/terraria-tile-runtime/docs/shared-tile-runtime-usage.md)
- [Example Mod](/C:/Users/Aiden/code/terraria-tile-runtime/docs/example-mod.md)

## Build

```powershell
dotnet build src/TileRuntime/TileRuntime.csproj -c Release
dotnet build src/TileRuntimeBootstrap/TileRuntimeBootstrap.csproj -c Release
```

The bootstrap project deploys the runtime mod into:

`C:\Program Files (x86)\Steam\steamapps\common\Terraria\TerrariaModder`

Override the default paths with MSBuild properties if needed:

```powershell
dotnet build src/TileRuntimeBootstrap/TileRuntimeBootstrap.csproj -c Release `
  /p:TerrariaInstallDir="D:\Games\Terraria" `
  /p:TerrariaModderDeployRoot="D:\Games\Terraria\TerrariaModder"
```

## Packaging

This ships as one mod folder:

```text
TerrariaModder/mods/tile-runtime/
  manifest.json
  TileRuntimeBootstrap.dll
  TerrariaModder.TileRuntime.dll
```

`TileRuntimeBootstrap.dll` is the mod entry assembly. `TerrariaModder.TileRuntime.dll` is the shared support assembly loaded alongside it.

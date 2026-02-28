# Terraria Tile Runtime

Standalone repository for the shared `tile-runtime` dependency mod used by TerrariaModder tile mods.

## Build

```powershell
dotnet build src/TileRuntime/TileRuntime.csproj -c Release
dotnet build src/TileRuntimeBootstrap/TileRuntimeBootstrap.csproj -c Release
```

The bootstrap project deploys the runtime mod directly into:

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

`TileRuntimeBootstrap` is the mod entry assembly. `TerrariaModder.TileRuntime.dll` is the support/runtime assembly loaded alongside it.

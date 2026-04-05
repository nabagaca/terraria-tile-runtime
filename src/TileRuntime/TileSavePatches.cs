using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Save/load interception for runtime-owned custom tiles.
    /// Strategy: extract custom tiles to sidecar moddata on save, restore on load.
    /// Prefix priority is set high so custom tile chests are removed before Core world-item save logic scans chests.
    /// </summary>
    internal static class TileSavePatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static bool _saveWorldPatchApplied;
        private static bool _loadWorldPatchApplied;
        private static bool _loggedLoadRestoreSkippedDueMissingSavePatch;

        private static readonly Dictionary<int, TileSnapshot> _extractedSnapshots = new Dictionary<int, TileSnapshot>();
        private static readonly Dictionary<int, Chest> _extractedContainerChests = new Dictionary<int, Chest>();
        private static readonly HashSet<string> _worldBackupDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string ContainerLocationPrefix = "container_";

        private class TileEntry
        {
            public int X;
            public int Y;
            public string TileId;
            public short FrameX;
            public short FrameY;
            public int Slope;
            public bool HalfBrick;
            public ushort Wall;
            public byte Liquid;
            public int LiquidType;
            public byte Color;
            public byte WallColor;
            public bool Actuator;
            public bool InActive;
        }

        private struct TileSnapshot
        {
            public ushort Type;
            public short FrameX;
            public short FrameY;
            public int Slope;
            public bool HalfBrick;
            public ushort Wall;
            public byte Liquid;
            public int LiquidType;
            public byte Color;
            public byte WallColor;
            public bool Actuator;
            public bool InActive;
            public bool Active;
        }

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tiles.save");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            PatchSaveWorld();
            if (_saveWorldPatchApplied)
            {
                PatchLoadWorld();
            }
            else
            {
                // Prevent stale sidecar replay if save extraction is unavailable.
                _log?.Warn("[TileRuntime.TileSavePatches] Skipping LoadWorld sidecar restore patch because SaveWorld patch is unavailable");
            }

            _applied = true;
            _log?.Info($"[TileRuntime.TileSavePatches] Applied (savePatch={_saveWorldPatchApplied}, loadPatch={_loadWorldPatchApplied})");
        }

        private static void PatchSaveWorld()
        {
            var worldFileType = typeof(Terraria.IO.WorldFile);

            // Terraria 1.4.5 signature: SaveWorld(bool resetTime, bool useTemps, bool canBeSkipped)
            var saveMethod = worldFileType.GetMethod("SaveWorld",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(bool), typeof(bool), typeof(bool) }, null);

            if (saveMethod == null)
            {
                _log?.Warn("[TileRuntime.TileSavePatches] WorldFile.SaveWorld not found");
                _saveWorldPatchApplied = false;
                return;
            }

            var prefix = new HarmonyMethod(typeof(TileSavePatches), nameof(SaveWorld_Prefix))
            {
                priority = Priority.First
            };
            var postfix = new HarmonyMethod(typeof(TileSavePatches), nameof(SaveWorld_Postfix))
            {
                priority = Priority.Last
            };

            _harmony.Patch(saveMethod, prefix: prefix, postfix: postfix);
            _saveWorldPatchApplied = true;
            _log?.Info("[TileRuntime.TileSavePatches] Patched WorldFile.SaveWorld(bool,bool,bool)");
        }

        private static void PatchLoadWorld()
        {
            var worldFileType = typeof(Terraria.IO.WorldFile);
            var loadMethod = worldFileType.GetMethod("LoadWorld",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            if (loadMethod == null)
            {
                _log?.Warn("[TileRuntime.TileSavePatches] WorldFile.LoadWorld() not found");
                _loadWorldPatchApplied = false;
                return;
            }

            var prefix = new HarmonyMethod(typeof(TileSavePatches), nameof(LoadWorld_Prefix))
            {
                priority = Priority.First
            };
            var postfix = new HarmonyMethod(typeof(TileSavePatches), nameof(LoadWorld_Postfix))
            {
                priority = Priority.Last
            };

            _harmony.Patch(loadMethod, prefix: prefix, postfix: postfix);
            _loadWorldPatchApplied = true;
            _log?.Info("[TileRuntime.TileSavePatches] Patched WorldFile.LoadWorld()");
        }

        private static void SaveWorld_Prefix()
        {
            _extractedSnapshots.Clear();
            _extractedContainerChests.Clear();

            if (TileRegistry.Count == 0) return;

            try
            {
                string worldPath = GetCurrentWorldPath();
                if (string.IsNullOrEmpty(worldPath)) return;

                var entries = new List<TileEntry>();
                var containerItems = new List<ModdataFile.ItemEntry>();
                var extractedContainers = new HashSet<int>();
                int containerCount = 0;
                int tileCount = 0;

                for (int x = 0; x < Main.maxTilesX; x++)
                {
                    for (int y = 0; y < Main.maxTilesY; y++)
                    {
                        var tile = Main.tile[x, y];
                        if (tile == null || !tile.active())
                            continue;

                        int tileType = tile.type;
                        if (!TileRegistry.IsCustomTile(tileType))
                            continue;

                        string fullId = TileRegistry.GetFullId(tileType);
                        var definition = TileRegistry.GetDefinition(tileType);
                        if (string.IsNullOrEmpty(fullId))
                            continue;

                        int key = ToKey(x, y);
                        _extractedSnapshots[key] = new TileSnapshot
                        {
                            Type = tile.type,
                            FrameX = tile.frameX,
                            FrameY = tile.frameY,
                            Slope = tile.slope(),
                            HalfBrick = tile.halfBrick(),
                            Wall = tile.wall,
                            Liquid = tile.liquid,
                            LiquidType = tile.liquidType(),
                            Color = tile.color(),
                            WallColor = tile.wallColor(),
                            Actuator = tile.actuator(),
                            InActive = tile.inActive(),
                            Active = tile.active()
                        };

                        entries.Add(new TileEntry
                        {
                            X = x,
                            Y = y,
                            TileId = fullId,
                            FrameX = tile.frameX,
                            FrameY = tile.frameY,
                            Slope = tile.slope(),
                            HalfBrick = tile.halfBrick(),
                            Wall = tile.wall,
                            Liquid = tile.liquid,
                            LiquidType = tile.liquidType(),
                            Color = tile.color(),
                            WallColor = tile.wallColor(),
                            Actuator = tile.actuator(),
                            InActive = tile.inActive()
                        });
                        
                        tileCount++;

                        if (definition != null && definition.IsContainer)
                        {
                            int topX = x;
                            int topY = y;
                            if (!CustomTileContainers.TryGetTopLeft(x, y, definition, out topX, out topY))
                            {
                                topX = x;
                                topY = y;
                            }

                            if (extractedContainers.Add(ToKey(topX, topY)))
                            {
                                ExtractContainerChest(topX, topY, definition, containerItems);
                                containerCount++;
                            }
                        }

                        tile.active(false);
                        tile.type = 0;
                        tile.frameX = 0;
                        tile.frameY = 0;
                        tile.liquid = 0;
                        tile.actuator(false);
                        tile.inActive(false);
                    }
                }

                _log?.Info($"[TileRuntime.TileSavePatches] SaveWorld_Prefix: Found {tileCount} custom tiles ({containerCount} containers with {containerItems.Count} items)");

                string path = GetTileModdataPath(worldPath);
                string containerPath = GetContainerModdataPath(worldPath);
                if (entries.Count == 0)
                {
                    TryDelete(path);
                    ModdataFile.Delete(containerPath);
                    return;
                }

                if (!WriteEntries(path, entries))
                {
                    RestoreAllSnapshots();
                    RestoreExtractedContainerChests();
                    return;
                }

                if (containerItems.Count == 0)
                {
                    ModdataFile.Delete(containerPath);
                }
                else if (!ModdataFile.Write(containerPath, containerItems))
                {
                    _log?.Error("[TileRuntime.TileSavePatches] Failed to write container items to sidecar");
                    RestoreAllSnapshots();
                    RestoreExtractedContainerChests();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileSavePatches] Save prefix error: {ex.Message}");
                RestoreAllSnapshots();
                RestoreExtractedContainerChests();
            }
        }

        private static void SaveWorld_Postfix()
        {
            if (_extractedSnapshots.Count == 0 && _extractedContainerChests.Count == 0)
                return;

            try
            {
                RestoreAllSnapshots();
                RestoreExtractedContainerChests();
            }
            finally
            {
                _extractedSnapshots.Clear();
                _extractedContainerChests.Clear();
            }
        }

        private static void LoadWorld_Prefix()
        {
            if (TileRegistry.Count == 0)
                return;

            try
            {
                string worldPath = GetCurrentWorldPath();
                if (string.IsNullOrEmpty(worldPath) || _worldBackupDone.Contains(worldPath))
                    return;

                string backupPath = worldPath + ".before-custom-tiles.bak";
                if (!File.Exists(backupPath) && File.Exists(worldPath))
                    File.Copy(worldPath, backupPath, overwrite: false);

                _worldBackupDone.Add(worldPath);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileSavePatches] World backup failed: {ex.Message}");
            }
        }

        private static void LoadWorld_Postfix()
        {
            if (!_saveWorldPatchApplied)
            {
                if (!_loggedLoadRestoreSkippedDueMissingSavePatch)
                {
                    _loggedLoadRestoreSkippedDueMissingSavePatch = true;

                    string worldPath = GetCurrentWorldPath();
                    string tileSidecarPath = string.IsNullOrEmpty(worldPath) ? null : GetTileModdataPath(worldPath);
                    string containerSidecarPath = string.IsNullOrEmpty(worldPath) ? null : GetContainerModdataPath(worldPath);
                    bool hasTileSidecar = !string.IsNullOrEmpty(tileSidecarPath) && File.Exists(tileSidecarPath);
                    bool hasContainerSidecar = !string.IsNullOrEmpty(containerSidecarPath) && File.Exists(containerSidecarPath);

                    _log?.Warn(
                        "[TileRuntime.TileSavePatches] LoadWorld sidecar restore is disabled because SaveWorld patch is unavailable. " +
                        $"world='{worldPath ?? "<unknown>"}', tileSidecarPresent={hasTileSidecar}, containerSidecarPresent={hasContainerSidecar}");
                }

                return;
            }

            if (TileRegistry.Count == 0)
                return;

            try
            {
                string worldPath = GetCurrentWorldPath();
                if (string.IsNullOrEmpty(worldPath))
                    return;

                var entries = ReadEntries(GetTileModdataPath(worldPath));
                if (entries.Count == 0)
                {
                    _log?.Info("[TileRuntime.TileSavePatches] No tiles to restore from sidecar");
                    return;
                }

                _log?.Info($"[TileRuntime.TileSavePatches] Restoring {entries.Count} custom tiles from sidecar");

                var containerDefsByTopLeft = new Dictionary<int, TileDefinition>();
                int restoredTileCount = 0;

                foreach (var entry in entries)
                {
                    if (entry.X < 0 || entry.X >= Main.maxTilesX || entry.Y < 0 || entry.Y >= Main.maxTilesY)
                        continue;

                    int runtimeType = TileRegistry.GetRuntimeType(entry.TileId);
                    if (runtimeType < 0)
                    {
                        _log?.Warn($"[TileRuntime.TileSavePatches] Unknown tile ID: {entry.TileId}");
                        continue;
                    }

                    var tile = Main.tile[entry.X, entry.Y] ?? new Tile();
                    Main.tile[entry.X, entry.Y] = tile;

                    tile.active(true);
                    tile.type = (ushort)runtimeType;
                    tile.frameX = entry.FrameX;
                    tile.frameY = entry.FrameY;
                    tile.wall = entry.Wall;
                    tile.liquid = entry.Liquid;
                    tile.liquidType((byte)entry.LiquidType);
                    tile.slope((byte)entry.Slope);
                    tile.halfBrick(entry.HalfBrick);
                    tile.color(entry.Color);
                    tile.wallColor(entry.WallColor);
                    tile.actuator(entry.Actuator);
                    tile.inActive(entry.InActive);
                    
                    restoredTileCount++;

                    var definition = TileRegistry.GetDefinition(runtimeType);
                    if (definition != null && definition.IsContainer)
                    {
                        int topX = entry.X;
                        int topY = entry.Y;
                        if (!CustomTileContainers.TryGetTopLeft(entry.X, entry.Y, definition, out topX, out topY))
                        {
                            topX = entry.X;
                            topY = entry.Y;
                        }

                        int key = ToKey(topX, topY);
                        if (!containerDefsByTopLeft.ContainsKey(key))
                            containerDefsByTopLeft[key] = definition;
                    }
                }

                _log?.Info($"[TileRuntime.TileSavePatches] Restored {restoredTileCount} tiles, {containerDefsByTopLeft.Count} container definitions");

                foreach (var kvp in containerDefsByTopLeft)
                {
                    FromKey(kvp.Key, out int topX, out int topY);
                    CustomTileContainers.EnsureContainerChest(topX, topY, kvp.Value, out int chestIndex);
                    _log?.Debug($"[TileRuntime.TileSavePatches] Ensured container chest at ({topX}, {topY}) [chestIndex={chestIndex}]");
                }

                RestoreContainerItems(worldPath, containerDefsByTopLeft);
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileSavePatches] Load postfix error: {ex.Message}");
            }
        }

        private static void ExtractContainerChest(int topX, int topY, TileDefinition definition, List<ModdataFile.ItemEntry> entries)
        {
            if (!CustomTileContainers.EnsureContainerChest(topX, topY, definition, out int chestIndex))
            {
                _log?.Warn($"[TileRuntime.TileSavePatches] Failed to ensure container chest at ({topX}, {topY})");
                return;
            }
            if (chestIndex < 0 || chestIndex >= Main.maxChests)
            {
                _log?.Warn($"[TileRuntime.TileSavePatches] Invalid chest index {chestIndex} at ({topX}, {topY})");
                return;
            }

            var chest = Main.chest[chestIndex];
            if (chest?.item == null)
            {
                _log?.Warn($"[TileRuntime.TileSavePatches] Chest at ({topX}, {topY}) has null or empty items (chestIndex={chestIndex})");
                return;
            }

            string location = MakeContainerLocation(topX, topY);
            int limit = chest.maxItems > 0 ? Math.Min(chest.maxItems, chest.item.Length) : chest.item.Length;
            int itemCount = 0;
            
            for (int slot = 0; slot < limit; slot++)
            {
                var item = chest.item[slot];
                if (item == null || item.IsAir || item.stack <= 0)
                    continue;

                string token = EncodeStoredItemType(item.type);
                if (string.IsNullOrEmpty(token))
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Failed to encode item type {item.type} at container ({topX}, {topY}) slot {slot}");
                    continue;
                }

                entries.Add(new ModdataFile.ItemEntry
                {
                    Location = location,
                    Slot = slot,
                    ItemId = token,
                    Stack = item.stack,
                    Prefix = item.prefix,
                    Favorited = item.favorited
                });
                
                itemCount++;
            }

            _log?.Info($"[TileRuntime.TileSavePatches] Extracted {itemCount} items from container at ({topX}, {topY}) [chestIndex={chestIndex}, type={definition.DisplayName}]");
            
            _extractedContainerChests[chestIndex] = chest;
            Main.chest[chestIndex] = null;
        }

        private static void RestoreContainerItems(string worldPath, Dictionary<int, TileDefinition> containerDefsByTopLeft)
        {
            // The container sidecar is an internal TileRuntime format — every item belongs to a
            // currently-loaded mod (or is a vanilla/runtime token).  Passing an empty loadedModIds
            // causes ModdataFile.Read to classify ALL items as "unloaded mod preserved" items
            // (because no mod ID matches the empty set), so we must include preservedItems too.
            var entries = ModdataFile.Read(GetContainerModdataPath(worldPath), null, out var preservedEntries);
            if (preservedEntries?.Count > 0)
                entries.AddRange(preservedEntries);
            if (entries.Count == 0)
            {
                _log?.Info("[TileRuntime.TileSavePatches] No container items to restore from sidecar");
                return;
            }

            _log?.Info($"[TileRuntime.TileSavePatches] Restoring {entries.Count} container items from sidecar");

            var preparedContainers = new HashSet<int>();
            int restoredCount = 0;
            int skippedCount = 0;
            
            foreach (var entry in entries)
            {
                if (!TryParseContainerLocation(entry.Location, out int topX, out int topY))
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Failed to parse container location: {entry.Location}");
                    skippedCount++;
                    continue;
                }

                int key = ToKey(topX, topY);
                if (!containerDefsByTopLeft.TryGetValue(key, out var definition))
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] No container definition for location ({topX}, {topY})");
                    skippedCount++;
                    continue;
                }

                if (!CustomTileContainers.EnsureContainerChest(topX, topY, definition, out int chestIndex))
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Failed to ensure chest for container at ({topX}, {topY})");
                    skippedCount++;
                    continue;
                }
                
                if (chestIndex < 0 || chestIndex >= Main.maxChests)
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Invalid chest index {chestIndex} for container at ({topX}, {topY})");
                    skippedCount++;
                    continue;
                }

                var chest = Main.chest[chestIndex];
                if (chest?.item == null)
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Chest at ({topX}, {topY}) has null items after EnsureContainerChest");
                    skippedCount++;
                    continue;
                }

                if (preparedContainers.Add(key))
                {
                    ClearChestItems(chest);
                    _log?.Debug($"[TileRuntime.TileSavePatches] Cleared chest at ({topX}, {topY}) for restore");
                }

                if (!TryCreateItemFromEntry(entry, out var item))
                {
                    _log?.Warn($"[TileRuntime.TileSavePatches] Failed to restore item {entry.ItemId} at ({topX}, {topY}) slot {entry.Slot}");
                    skippedCount++;
                    continue;
                }

                if (entry.Slot >= 0 && entry.Slot < chest.item.Length && (chest.item[entry.Slot] == null || chest.item[entry.Slot].IsAir))
                {
                    chest.item[entry.Slot] = item;
                    restoredCount++;
                    continue;
                }

                for (int slot = 0; slot < chest.item.Length; slot++)
                {
                    if (chest.item[slot] == null || chest.item[slot].IsAir)
                    {
                        chest.item[slot] = item;
                        restoredCount++;
                        break;
                    }
                }
            }
            
            _log?.Info($"[TileRuntime.TileSavePatches] Container restore complete: {restoredCount} items restored, {skippedCount} skipped");
        }

        private static bool TryCreateItemFromEntry(ModdataFile.ItemEntry entry, out Item item)
        {
            item = null;
            int itemType = DecodeStoredItemType(entry.ItemId);
            if (itemType <= 0)
                return false;

            item = new Item();
            item.SetDefaults(itemType);
            item.stack = Math.Max(1, entry.Stack);
            if (entry.Prefix > 0)
            {
                try { item.Prefix(entry.Prefix); } catch { }
                item.prefix = (byte)Math.Min(byte.MaxValue, Math.Max(0, entry.Prefix));
            }

            item.favorited = entry.Favorited;
            return true;
        }

        private static void ClearChestItems(Chest chest)
        {
            if (chest?.item == null) return;
            for (int i = 0; i < chest.item.Length; i++)
                chest.item[i] = new Item();
        }

        private static string EncodeStoredItemType(int itemType)
        {
            if (itemType <= 0) return null;
            if (itemType < ItemRegistry.VanillaItemCount) return "v:" + itemType;
            string fullId = ItemRegistry.GetFullId(itemType);
            if (!string.IsNullOrEmpty(fullId)) return fullId;
            return "runtime:" + itemType;
        }

        private static int DecodeStoredItemType(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return -1;
            if (token.StartsWith("v:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token.Substring(2), out int vanillaType))
                return vanillaType > 0 ? vanillaType : -1;

            if (token.StartsWith("runtime:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token.Substring("runtime:".Length), out int runtimeType))
                return runtimeType > 0 ? runtimeType : -1;

            int registered = ItemRegistry.GetRuntimeType(token);
            return registered > 0 ? registered : -1;
        }

        private static void RestoreAllSnapshots()
        {
            foreach (var kvp in _extractedSnapshots)
            {
                FromKey(kvp.Key, out int x, out int y);
                if (x < 0 || x >= Main.maxTilesX || y < 0 || y >= Main.maxTilesY)
                    continue;

                var tile = Main.tile[x, y] ?? new Tile();
                Main.tile[x, y] = tile;

                var snap = kvp.Value;
                tile.active(snap.Active);
                tile.type = snap.Type;
                tile.frameX = snap.FrameX;
                tile.frameY = snap.FrameY;
                tile.wall = snap.Wall;
                tile.liquid = snap.Liquid;
                tile.liquidType((byte)snap.LiquidType);
                tile.slope((byte)snap.Slope);
                tile.halfBrick(snap.HalfBrick);
                tile.color(snap.Color);
                tile.wallColor(snap.WallColor);
                tile.actuator(snap.Actuator);
                tile.inActive(snap.InActive);
            }
        }

        private static void RestoreExtractedContainerChests()
        {
            foreach (var kvp in _extractedContainerChests)
            {
                if (kvp.Key >= 0 && kvp.Key < Main.maxChests && Main.chest[kvp.Key] == null)
                    Main.chest[kvp.Key] = kvp.Value;
            }
        }

        private static string GetCurrentWorldPath()
        {
            try
            {
                var worldFileData = Main.ActiveWorldFileData;
                if (worldFileData != null)
                {
                    var pathProp = worldFileData.GetType().GetProperty("Path");
                    var p = pathProp?.GetValue(worldFileData) as string;
                    if (!string.IsNullOrEmpty(p)) return p;
                }

                var worldPathProp = typeof(Main).GetProperty("worldPathName", BindingFlags.Public | BindingFlags.Static);
                return worldPathProp?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string GetTileModdataPath(string worldPath) => worldPath + ".tiles.moddata";
        private static string GetContainerModdataPath(string worldPath) => worldPath + ".tiles.containers.moddata";
        private static string MakeContainerLocation(int topX, int topY) => $"{ContainerLocationPrefix}{topX}_{topY}";
        private static int ToKey(int x, int y) => (x << 16) ^ (y & 0xFFFF);

        private static void FromKey(int key, out int x, out int y)
        {
            x = (key >> 16) & 0xFFFF;
            y = key & 0xFFFF;
        }

        private static bool TryParseContainerLocation(string location, out int topX, out int topY)
        {
            topX = 0;
            topY = 0;
            if (string.IsNullOrEmpty(location) || !location.StartsWith(ContainerLocationPrefix, StringComparison.Ordinal))
                return false;

            string rest = location.Substring(ContainerLocationPrefix.Length);
            int sep = rest.IndexOf('_');
            return sep > 0 && sep < rest.Length - 1 &&
                   int.TryParse(rest.Substring(0, sep), out topX) &&
                   int.TryParse(rest.Substring(sep + 1), out topY);
        }

        private static bool WriteEntries(string path, List<TileEntry> entries)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"version\": 1,");
                sb.AppendLine($"  \"count\": {entries.Count},");
                sb.AppendLine("  \"tiles\": [");
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    sb.Append("    { ");
                    sb.Append($"\"x\": {e.X}, \"y\": {e.Y}, \"tile_id\": \"{Escape(e.TileId)}\", ");
                    sb.Append($"\"fx\": {e.FrameX}, \"fy\": {e.FrameY}, \"slope\": {e.Slope}, ");
                    sb.Append($"\"half\": {(e.HalfBrick ? "true" : "false")}, \"wall\": {e.Wall}, ");
                    sb.Append($"\"liquid\": {e.Liquid}, \"liquid_type\": {e.LiquidType}, ");
                    sb.Append($"\"color\": {e.Color}, \"wall_color\": {e.WallColor}, ");
                    sb.Append($"\"actuator\": {(e.Actuator ? "true" : "false")}, \"inactive\": {(e.InActive ? "true" : "false")}");
                    sb.Append(" }");
                    if (i < entries.Count - 1) sb.Append(',');
                    sb.AppendLine();
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");

                File.WriteAllText(path, sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileSavePatches] WriteEntries failed: {ex.Message}");
                return false;
            }
        }

        private static List<TileEntry> ReadEntries(string path)
        {
            var result = new List<TileEntry>();
            if (!File.Exists(path))
                return result;

            try
            {
                string json = File.ReadAllText(path);
                var matches = Regex.Matches(json,
                    "\\{\\s*\"x\"\\s*:\\s*(?<x>-?\\d+)\\s*,\\s*\"y\"\\s*:\\s*(?<y>-?\\d+)\\s*,\\s*\"tile_id\"\\s*:\\s*\"(?<id>(?:\\\\.|[^\"])*)\"\\s*,\\s*\"fx\"\\s*:\\s*(?<fx>-?\\d+)\\s*,\\s*\"fy\"\\s*:\\s*(?<fy>-?\\d+)\\s*,\\s*\"slope\"\\s*:\\s*(?<slope>-?\\d+)\\s*,\\s*\"half\"\\s*:\\s*(?<half>true|false)\\s*,\\s*\"wall\"\\s*:\\s*(?<wall>\\d+)\\s*,\\s*\"liquid\"\\s*:\\s*(?<liquid>\\d+)\\s*,\\s*\"liquid_type\"\\s*:\\s*(?<ltype>-?\\d+)\\s*,\\s*\"color\"\\s*:\\s*(?<color>\\d+)\\s*,\\s*\"wall_color\"\\s*:\\s*(?<wcolor>\\d+)\\s*,\\s*\"actuator\"\\s*:\\s*(?<act>true|false)\\s*,\\s*\"inactive\"\\s*:\\s*(?<inactive>true|false)\\s*\\}");

                foreach (Match match in matches)
                {
                    result.Add(new TileEntry
                    {
                        X = int.Parse(match.Groups["x"].Value),
                        Y = int.Parse(match.Groups["y"].Value),
                        TileId = Unescape(match.Groups["id"].Value),
                        FrameX = short.Parse(match.Groups["fx"].Value),
                        FrameY = short.Parse(match.Groups["fy"].Value),
                        Slope = int.Parse(match.Groups["slope"].Value),
                        HalfBrick = string.Equals(match.Groups["half"].Value, "true", StringComparison.OrdinalIgnoreCase),
                        Wall = ushort.Parse(match.Groups["wall"].Value),
                        Liquid = byte.Parse(match.Groups["liquid"].Value),
                        LiquidType = int.Parse(match.Groups["ltype"].Value),
                        Color = byte.Parse(match.Groups["color"].Value),
                        WallColor = byte.Parse(match.Groups["wcolor"].Value),
                        Actuator = string.Equals(match.Groups["act"].Value, "true", StringComparison.OrdinalIgnoreCase),
                        InActive = string.Equals(match.Groups["inactive"].Value, "true", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileSavePatches] ReadEntries failed: {ex.Message}");
            }

            return result;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}

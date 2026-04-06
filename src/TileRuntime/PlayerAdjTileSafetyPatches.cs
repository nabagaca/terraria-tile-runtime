using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.ID;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Guards Player adjacency tile bookkeeping from custom tile type overflows.
    /// </summary>
    internal static class PlayerAdjTileSafetyPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static FieldInfo _adjTileField;
        private static FieldInfo _oldAdjTileField;
        private static FieldInfo _tileIdCountField;
        private static bool _loggedMissingFields;

        // Cached tile count — never changes after TileTypeExtension.Apply().
        private static int _cachedTileCount;
        // Per-player flag: once adjTile arrays are confirmed at the right size, skip all reflection.
        private static readonly bool[] _arraysVerified = new bool[256];

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.player-adjtile-safety");

            var playerType = typeof(Player);
            _adjTileField = playerType.GetField("adjTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _oldAdjTileField = playerType.GetField("oldAdjTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _tileIdCountField = typeof(TileID).GetField("Count", BindingFlags.Public | BindingFlags.Static);
        }

        public static void ApplyPatches()
        {
            if (_applied)
                return;

            _cachedTileCount = TileTypeExtension.ExtendedCount;

            try
            {
                int patched = 0;

                var setAdjTile = typeof(Player).GetMethod("SetAdjTile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (setAdjTile != null)
                {
                    _harmony.Patch(setAdjTile,
                        prefix: new HarmonyMethod(typeof(PlayerAdjTileSafetyPatches), nameof(SetAdjTile_Prefix)));
                    patched++;
                }

                var updateNearbyCraftingTiles = typeof(Player).GetMethod("UpdateNearbyCraftingTiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (updateNearbyCraftingTiles != null)
                {
                    _harmony.Patch(updateNearbyCraftingTiles,
                        prefix: new HarmonyMethod(typeof(PlayerAdjTileSafetyPatches), nameof(UpdateNearbyCraftingTiles_Prefix)));
                    patched++;
                }

                _applied = true;
                _log?.Info($"[TileRuntime.PlayerAdjTileSafetyPatches] Applied {patched} patches");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.PlayerAdjTileSafetyPatches] Failed to apply: {ex.Message}");
            }
        }

        private static void UpdateNearbyCraftingTiles_Prefix(Player __instance)
        {
            if (__instance == null)
                return;

            int who = __instance.whoAmI;
            if (who >= 0 && who < _arraysVerified.Length && _arraysVerified[who])
                return;

            EnsureAdjTileArrays(__instance, _cachedTileCount > 0 ? _cachedTileCount : GetTileCount());
        }

        private static bool SetAdjTile_Prefix(Player __instance, int tileType)
        {
            if (__instance == null || tileType < 0)
                return false;

            int tileCount = _cachedTileCount > 0 ? _cachedTileCount : GetTileCount();

            int who = __instance.whoAmI;
            if (who >= 0 && who < _arraysVerified.Length)
            {
                if (!_arraysVerified[who])
                {
                    EnsureAdjTileArrays(__instance, tileCount);
                    _arraysVerified[who] = true;
                }
                SafeSetAdjTile(__instance, tileType, new HashSet<int>());
                return false;
            }

            // whoAmI out of range — fall back to safe reflection check
            EnsureAdjTileArrays(__instance, tileCount);
            SafeSetAdjTile(__instance, tileType, new HashSet<int>());
            return false;
        }

        private static void SafeSetAdjTile(Player player, int tileType, HashSet<int> visited)
        {
            if (player == null || tileType < 0 || !visited.Add(tileType))
                return;

            try
            {
                if (_adjTileField != null)
                {
                    var adj = _adjTileField.GetValue(player) as bool[];
                    if (adj != null && tileType < adj.Length)
                        adj[tileType] = true;
                }

                if (tileType == 355 || tileType == 699)
                    player.alchemyTable = true;

                var tileCountsAs = Recipe.TileCountsAs;
                if (tileCountsAs == null || tileType >= tileCountsAs.Length)
                    return;

                var equivalents = tileCountsAs[tileType];
                if (equivalents == null)
                    return;

                foreach (int equivalentType in equivalents)
                    SafeSetAdjTile(player, equivalentType, visited);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.PlayerAdjTileSafetyPatches] SafeSetAdjTile error for tile {tileType}: {ex.Message}");
            }
        }

        private static void EnsureAdjTileArrays(Player player, int requiredLength)
        {
            if (requiredLength <= 0)
                return;

            if (_adjTileField == null)
            {
                if (!_loggedMissingFields)
                {
                    _loggedMissingFields = true;
                    _log?.Warn("[TileRuntime.PlayerAdjTileSafetyPatches] Player.adjTile field not found");
                }
                return;
            }

            try
            {
                bool resizedAny = false;

                var adj = _adjTileField.GetValue(player) as bool[];
                if (adj == null || adj.Length < requiredLength)
                {
                    var resized = new bool[requiredLength];
                    if (adj != null)
                        Array.Copy(adj, resized, Math.Min(adj.Length, resized.Length));
                    _adjTileField.SetValue(player, resized);
                    resizedAny = true;
                }

                if (_oldAdjTileField != null)
                {
                    var oldAdj = _oldAdjTileField.GetValue(player) as bool[];
                    if (oldAdj == null || oldAdj.Length < requiredLength)
                    {
                        var resizedOld = new bool[requiredLength];
                        if (oldAdj != null)
                            Array.Copy(oldAdj, resizedOld, Math.Min(oldAdj.Length, resizedOld.Length));
                        _oldAdjTileField.SetValue(player, resizedOld);
                        resizedAny = true;
                    }
                }

                if (resizedAny)
                    _log?.Info($"[TileRuntime.PlayerAdjTileSafetyPatches] Resized player adjTile arrays to {requiredLength}");
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.PlayerAdjTileSafetyPatches] Resize error: {ex.Message}");
            }
        }

        private static int GetTileCount()
        {
            try
            {
                if (_tileIdCountField != null)
                {
                    object value = _tileIdCountField.GetValue(null);
                    if (value is int i) return i;
                    if (value is short s) return s;
                    if (value is ushort us) return us;
                }
            }
            catch
            {
            }

            return Math.Max(700, TileTypeExtension.ExtendedCount);
        }
    }
}

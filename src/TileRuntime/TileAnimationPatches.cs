using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Harmony postfix on Main.UpdateTileAnimations() that generically ticks
    /// all registered animated custom tiles using Main.tileFrame / Main.tileFrameCounter.
    /// </summary>
    internal static class TileAnimationPatches
    {
        private static ILogger _log;
        private static Harmony _harmony;
        private static bool _patchesApplied;

        private struct AnimatedTileEntry
        {
            public int Type;
            public int FrameCount;
            public int TicksPerFrame;
        }

        private static readonly List<AnimatedTileEntry> _animatedTiles = new List<AnimatedTileEntry>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tileanimation");
        }

        public static void RegisterAnimatedTile(int type, int frameCount, int ticksPerFrame)
        {
            if (frameCount <= 0 || ticksPerFrame <= 0)
                return;

            _animatedTiles.Add(new AnimatedTileEntry
            {
                Type = type,
                FrameCount = frameCount,
                TicksPerFrame = ticksPerFrame
            });

            _log?.Info($"[TileRuntime.TileAnimationPatches] Registered animated tile type {type}: {frameCount} frames, {ticksPerFrame} ticks/frame");
        }

        public static void ApplyPatches()
        {
            if (_patchesApplied || _animatedTiles.Count == 0)
                return;

            try
            {
                var updateMethod = typeof(Main).GetMethod("AnimateTiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (updateMethod == null)
                {
                    _log?.Warn("[TileRuntime.TileAnimationPatches] Main.AnimateTiles not found");
                    return;
                }

                var postfix = typeof(TileAnimationPatches).GetMethod(nameof(UpdateTileAnimations_Postfix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                _harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
                _patchesApplied = true;
                _log?.Info($"[TileRuntime.TileAnimationPatches] Patched Main.UpdateTileAnimations for {_animatedTiles.Count} animated tile(s)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileAnimationPatches] Failed to patch: {ex.Message}");
            }
        }

        private static void UpdateTileAnimations_Postfix()
        {
            try
            {
                for (int i = 0; i < _animatedTiles.Count; i++)
                {
                    var entry = _animatedTiles[i];
                    int type = entry.Type;

                    if (type < 0 || type >= Main.tileFrameCounter.Length || type >= Main.tileFrame.Length)
                        continue;

                    Main.tileFrameCounter[type]++;
                    if (Main.tileFrameCounter[type] >= entry.TicksPerFrame)
                    {
                        Main.tileFrameCounter[type] = 0;
                        Main.tileFrame[type] = (Main.tileFrame[type] + 1) % entry.FrameCount;
                    }
                }
            }
            catch
            {
            }
        }
    }
}

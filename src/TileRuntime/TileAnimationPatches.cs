using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Harmony postfix on Main.AnimateTiles() that generically ticks
    /// all registered animated custom tiles using Main.tileFrame / Main.tileFrameCounter.
    /// Supports both looping and triggered (one-shot) animation modes.
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
            public bool Triggered;
        }

        private static readonly List<AnimatedTileEntry> _animatedTiles = new List<AnimatedTileEntry>();
        private static readonly HashSet<int> _activeTriggered = new HashSet<int>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tileanimation");
        }

        public static void RegisterAnimatedTile(int type, int frameCount, int ticksPerFrame, bool triggered = false)
        {
            if (frameCount <= 0 || ticksPerFrame <= 0)
                return;

            _animatedTiles.Add(new AnimatedTileEntry
            {
                Type = type,
                FrameCount = frameCount,
                TicksPerFrame = ticksPerFrame,
                Triggered = triggered
            });

            _log?.Info($"[TileRuntime.TileAnimationPatches] Registered animated tile type {type}: {frameCount} frames, {ticksPerFrame} ticks/frame, triggered={triggered}");
        }

        public static void TriggerAnimation(int type)
        {
            if (type < 0 || type >= Main.tileFrame.Length)
                return;

            Main.tileFrame[type] = 0;
            Main.tileFrameCounter[type] = 0;
            _activeTriggered.Add(type);
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

                var postfix = typeof(TileAnimationPatches).GetMethod(nameof(AnimateTiles_Postfix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                _harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
                _patchesApplied = true;
                _log?.Info($"[TileRuntime.TileAnimationPatches] Patched Main.AnimateTiles for {_animatedTiles.Count} animated tile(s)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileAnimationPatches] Failed to patch: {ex.Message}");
            }
        }

        private static void AnimateTiles_Postfix()
        {
            try
            {
                for (int i = 0; i < _animatedTiles.Count; i++)
                {
                    var entry = _animatedTiles[i];
                    int type = entry.Type;

                    if (type < 0 || type >= Main.tileFrameCounter.Length || type >= Main.tileFrame.Length)
                        continue;

                    if (entry.Triggered)
                    {
                        // Only tick if this tile was triggered
                        if (!_activeTriggered.Contains(type))
                            continue;

                        Main.tileFrameCounter[type]++;
                        if (Main.tileFrameCounter[type] >= entry.TicksPerFrame)
                        {
                            Main.tileFrameCounter[type] = 0;
                            int nextFrame = Main.tileFrame[type] + 1;
                            if (nextFrame >= entry.FrameCount)
                            {
                                // Cycle complete — stop and reset to frame 0
                                Main.tileFrame[type] = 0;
                                _activeTriggered.Remove(type);
                            }
                            else
                            {
                                Main.tileFrame[type] = nextFrame;
                            }
                        }
                    }
                    else
                    {
                        // Looping mode
                        Main.tileFrameCounter[type]++;
                        if (Main.tileFrameCounter[type] >= entry.TicksPerFrame)
                        {
                            Main.tileFrameCounter[type] = 0;
                            Main.tileFrame[type] = (Main.tileFrame[type] + 1) % entry.FrameCount;
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}

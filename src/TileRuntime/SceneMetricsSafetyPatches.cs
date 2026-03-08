using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Guards SceneMetrics scans against tile-array mismatches after runtime tile extension.
    /// </summary>
    internal static class SceneMetricsSafetyPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static bool _suppressionLogged;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.scenemetrics-safety");
        }

        public static void ApplyPatches()
        {
            if (_applied)
                return;

            int patched = 0;

            foreach (var scanMethod in FindInstanceMethods(typeof(SceneMetrics), "Scan"))
            {
                _harmony.Patch(
                    scanMethod,
                    prefix: new HarmonyMethod(typeof(SceneMetricsSafetyPatches), nameof(Scan_Prefix)));
                patched++;
            }

            foreach (var scanTilesMethod in FindInstanceMethods(typeof(SceneMetrics), "ScanTiles"))
            {
                _harmony.Patch(
                    scanTilesMethod,
                    finalizer: new HarmonyMethod(typeof(SceneMetricsSafetyPatches), nameof(ScanTiles_Finalizer)));
                patched++;
            }

            _applied = true;
            _log?.Info($"[TileRuntime.SceneMetricsSafetyPatches] Applied {patched} patch(es)");
        }

        private static void Scan_Prefix()
        {
            try
            {
                TileTypeExtension.RefreshSceneMetricsInstances(_log);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.SceneMetricsSafetyPatches] Scan prefix refresh failed: {ex.Message}");
            }
        }

        private static Exception ScanTiles_Finalizer(Exception __exception)
        {
            if (__exception is IndexOutOfRangeException)
            {
                if (!_suppressionLogged)
                {
                    _suppressionLogged = true;
                    _log?.Warn("[TileRuntime.SceneMetricsSafetyPatches] Suppressed SceneMetrics.ScanTiles IndexOutOfRangeException");
                }

                return null;
            }

            return __exception;
        }

        private static MethodInfo[] FindInstanceMethods(Type owner, string name)
        {
            if (owner == null || string.IsNullOrEmpty(name))
                return Array.Empty<MethodInfo>();

            var methods = new System.Collections.Generic.List<MethodInfo>();
            foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;
                methods.Add(method);
            }

            return methods.ToArray();
        }
    }
}

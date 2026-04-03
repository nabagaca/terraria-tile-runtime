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
        private static bool _sceneMetricsUpToDate;

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

            MethodInfo scanMethod = FindScanMethod();
            if (scanMethod != null)
            {
                _harmony.Patch(
                    scanMethod,
                    prefix: new HarmonyMethod(typeof(SceneMetricsSafetyPatches), nameof(Scan_Prefix)));
                patched++;
            }

            MethodInfo scanTilesMethod = FindScanTilesMethod();
            if (scanTilesMethod != null)
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
            if (_sceneMetricsUpToDate)
                return;

            try
            {
                int resized = TileTypeExtension.RefreshSceneMetricsInstances(_log);
                if (resized == 0)
                    _sceneMetricsUpToDate = true;
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

        private static MethodInfo FindScanMethod()
        {
            foreach (var method in typeof(SceneMetrics).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "Scan", StringComparison.Ordinal))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                // Exact Terraria 1.4.5 target: Scan(SceneMetricsScanSettings)
                if (string.Equals(parameters[0].ParameterType.Name, "SceneMetricsScanSettings", StringComparison.Ordinal))
                    return method;
            }

            return null;
        }

        private static MethodInfo FindScanTilesMethod()
        {
            return typeof(SceneMetrics).GetMethod(
                "ScanTiles",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
        }
    }
}

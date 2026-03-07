using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Runtime smart-interact integration for custom interactive tiles.
    /// </summary>
    internal static class TileSmartInteractPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static bool _loggedMissingMembers;

        private static Type _providerType;
        private static MethodBase _fillPotentialTargetTilesMethod;
        private static MethodBase _provideCandidateMethod;
        private static MethodBase _inSmartCursorHighlightAreaMethod;
        private static FieldInfo _targetsField;
        private static FieldInfo _settingsLXField;
        private static FieldInfo _settingsHXField;
        private static FieldInfo _settingsLYField;
        private static FieldInfo _settingsHYField;
        private static FieldInfo _mainSmartInteractXField;
        private static FieldInfo _mainSmartInteractYField;
        private static FieldInfo _mainSmartInteractTileCoordsField;
        private static FieldInfo _mainSmartInteractTileCoordsSelectedField;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tiles.smartinteract");
            CacheReflection();
        }

        public static void ApplyPatches()
        {
            if (_applied)
                return;

            if (_providerType == null || _fillPotentialTargetTilesMethod == null || _provideCandidateMethod == null || _targetsField == null)
            {
                if (!_loggedMissingMembers)
                {
                    _loggedMissingMembers = true;
                    _log?.Warn("[TileRuntime.TileSmartInteractPatches] Missing smart-interact members; patch skipped");
                }
                return;
            }

            _harmony.Patch(
                _fillPotentialTargetTilesMethod,
                postfix: new HarmonyMethod(typeof(TileSmartInteractPatches), nameof(FillPotentialTargetTiles_Postfix)));

            _harmony.Patch(
                _provideCandidateMethod,
                postfix: new HarmonyMethod(typeof(TileSmartInteractPatches), nameof(ProvideCandidate_Postfix)));

            if (_inSmartCursorHighlightAreaMethod != null)
            {
                _harmony.Patch(
                    _inSmartCursorHighlightAreaMethod,
                    postfix: new HarmonyMethod(typeof(TileSmartInteractPatches), nameof(InSmartCursorHighlightArea_Postfix)));
            }

            _applied = true;
            _log?.Info("[TileRuntime.TileSmartInteractPatches] Applied");
        }

        private static void CacheReflection()
        {
            _providerType = Type.GetType("Terraria.GameContent.ObjectInteractions.TileSmartInteractCandidateProvider, Terraria");
            if (_providerType == null)
                return;

            _fillPotentialTargetTilesMethod = _providerType.GetMethod(
                "FillPotentialTargetTiles",
                BindingFlags.NonPublic | BindingFlags.Instance);

            _provideCandidateMethod = _providerType.GetMethod(
                "ProvideCandidate",
                BindingFlags.Public | BindingFlags.Instance);

            _targetsField = _providerType.GetField("targets", BindingFlags.NonPublic | BindingFlags.Instance);

            _inSmartCursorHighlightAreaMethod = typeof(Main).GetMethod(
                "InSmartCursorHighlightArea",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int), typeof(int), typeof(bool).MakeByRefType() },
                null);

            var settingsType = Type.GetType("Terraria.GameContent.ObjectInteractions.SmartInteractScanSettings, Terraria");
            _settingsLXField = settingsType?.GetField("LX", BindingFlags.Public | BindingFlags.Instance);
            _settingsHXField = settingsType?.GetField("HX", BindingFlags.Public | BindingFlags.Instance);
            _settingsLYField = settingsType?.GetField("LY", BindingFlags.Public | BindingFlags.Instance);
            _settingsHYField = settingsType?.GetField("HY", BindingFlags.Public | BindingFlags.Instance);

            _mainSmartInteractXField = typeof(Main).GetField("SmartInteractX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _mainSmartInteractYField = typeof(Main).GetField("SmartInteractY", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _mainSmartInteractTileCoordsField = typeof(Main).GetField("SmartInteractTileCoords", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _mainSmartInteractTileCoordsSelectedField = typeof(Main).GetField("SmartInteractTileCoordsSelected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static void FillPotentialTargetTiles_Postfix(object __instance, object settings)
        {
            if (__instance == null || settings == null || _targetsField == null)
                return;

            try
            {
                var targets = _targetsField.GetValue(__instance) as IList;
                if (targets == null)
                    return;

                int lx = GetSafeInt(_settingsLXField, settings, 0);
                int hx = GetSafeInt(_settingsHXField, settings, -1);
                int ly = GetSafeInt(_settingsLYField, settings, 0);
                int hy = GetSafeInt(_settingsHYField, settings, -1);

                if (hx < lx || hy < ly)
                    return;

                int maxX = Main.maxTilesX - 1;
                int maxY = Main.maxTilesY - 1;
                lx = Math.Max(0, Math.Min(maxX, lx));
                hx = Math.Max(0, Math.Min(maxX, hx));
                ly = Math.Max(0, Math.Min(maxY, ly));
                hy = Math.Max(0, Math.Min(maxY, hy));

                Type tupleType = GetListElementType(targets);
                for (int x = lx; x <= hx; x++)
                {
                    for (int y = ly; y <= hy; y++)
                    {
                        if (!CustomTileContainers.TryGetTileDefinition(x, y, out var definition, out int tileType))
                            continue;
                        if (!CustomTileContainers.IsSmartInteractCandidate(tileType, definition))
                            continue;
                        if (ContainsCoordinate(targets, x, y))
                            continue;

                        object tuple = CreateCoordinate(tupleType, x, y);
                        if (tuple != null)
                            targets.Add(tuple);
                    }
                }
            }
            catch
            {
            }
        }

        private static void ProvideCandidate_Postfix(object settings, bool __result)
        {
            if (!__result)
                return;

            try
            {
                int smartX = GetSafeMainInt(_mainSmartInteractXField, -1);
                int smartY = GetSafeMainInt(_mainSmartInteractYField, -1);
                if (!CustomTileContainers.TryGetTileDefinition(smartX, smartY, out var definition, out int tileType))
                    return;
                if (!CustomTileContainers.IsSmartInteractCandidate(tileType, definition))
                    return;

                if (!CustomTileContainers.TryGetTopLeft(smartX, smartY, definition, out int topX, out int topY))
                {
                    topX = smartX;
                    topY = smartY;
                }

                int width = Math.Max(1, definition.Width);
                int height = Math.Max(1, definition.Height);

                var allCoords = _mainSmartInteractTileCoordsField?.GetValue(null) as IList;
                var selectedCoords = _mainSmartInteractTileCoordsSelectedField?.GetValue(null) as IList;
                if (allCoords == null || selectedCoords == null)
                    return;

                Type pointType = GetListElementType(allCoords);
                if (pointType == null)
                    return;

                for (int x = topX; x < topX + width; x++)
                {
                    for (int y = topY; y < topY + height; y++)
                    {
                        AddPointIfMissing(allCoords, selectedCoords, pointType, x, y);
                    }
                }
            }
            catch
            {
            }
        }

        private static void InSmartCursorHighlightArea_Postfix(int x, int y, ref bool actuallySelected, ref bool __result)
        {
            if (__result)
                return;

            try
            {
                int smartX = GetSafeMainInt(_mainSmartInteractXField, -1);
                int smartY = GetSafeMainInt(_mainSmartInteractYField, -1);
                if (!CustomTileContainers.TryGetTileDefinition(smartX, smartY, out var definition, out int tileType))
                    return;
                if (!CustomTileContainers.IsSmartInteractCandidate(tileType, definition))
                    return;
                if (!CustomTileContainers.ShouldHaveOutline(tileType, definition))
                    return;

                if (!CustomTileContainers.TryGetTopLeft(smartX, smartY, definition, out int topX, out int topY))
                {
                    topX = smartX;
                    topY = smartY;
                }

                int width = Math.Max(1, definition.Width);
                int height = Math.Max(1, definition.Height);
                if (x < topX || x >= topX + width || y < topY || y >= topY + height)
                    return;

                if (!Collision.InTileBounds(x, y, Main.TileInteractionLX, Main.TileInteractionLY, Main.TileInteractionHX, Main.TileInteractionHY))
                    return;

                __result = true;
                actuallySelected = true;
            }
            catch
            {
            }
        }

        private static void AddPointIfMissing(IList allCoords, IList selectedCoords, Type pointType, int x, int y)
        {
            bool hasAll = ContainsCoordinate(allCoords, x, y);
            bool hasSelected = ContainsCoordinate(selectedCoords, x, y);
            if (hasAll && hasSelected)
                return;

            object point = CreateCoordinate(pointType, x, y);
            if (point == null)
                return;

            if (!hasSelected)
                selectedCoords.Add(point);
            if (!hasAll)
                allCoords.Add(point);
        }

        private static Type GetListElementType(IList list)
        {
            Type listType = list?.GetType();
            if (listType == null || !listType.IsGenericType)
                return null;

            Type[] args = listType.GetGenericArguments();
            return args.Length == 1 ? args[0] : null;
        }

        private static object CreateCoordinate(Type type, int x, int y)
        {
            if (type == null)
                return null;

            try
            {
                if (type == typeof(Tuple<int, int>))
                    return Tuple.Create(x, y);

                var ctor = type.GetConstructor(new[] { typeof(int), typeof(int) });
                if (ctor != null)
                    return ctor.Invoke(new object[] { x, y });

                object instance = Activator.CreateInstance(type);
                SetMember(instance, "X", x);
                SetMember(instance, "Y", y);
                return instance;
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsCoordinate(IList list, int x, int y)
        {
            if (list == null)
                return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (!TryReadCoordinate(list[i], out int cx, out int cy))
                    continue;
                if (cx == x && cy == y)
                    return true;
            }

            return false;
        }

        private static bool TryReadCoordinate(object value, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (value == null)
                return false;

            Type type = value.GetType();
            object vx = GetMember(type, value, "X") ?? GetMember(type, value, "x") ?? GetMember(type, value, "Item1");
            object vy = GetMember(type, value, "Y") ?? GetMember(type, value, "y") ?? GetMember(type, value, "Item2");
            if (vx == null || vy == null)
                return false;

            try
            {
                x = Convert.ToInt32(vx);
                y = Convert.ToInt32(vy);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object GetMember(Type type, object instance, string name)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(instance);

            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(instance, null);

            return null;
        }

        private static void SetMember(object instance, string memberName, object value)
        {
            if (instance == null)
                return;

            Type type = instance.GetType();
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
                prop.SetValue(instance, value, null);
        }

        private static int GetSafeMainInt(FieldInfo field, int fallback)
        {
            if (field == null)
                return fallback;

            try
            {
                object value = field.GetValue(null);
                if (value == null)
                    return fallback;
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static int GetSafeInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null)
                return fallback;

            try
            {
                object value = field.GetValue(instance);
                if (value == null)
                    return fallback;
                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }
    }
}

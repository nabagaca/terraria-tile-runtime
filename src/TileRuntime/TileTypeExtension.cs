using System;
using System.Collections.Generic;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Extends tile-indexed Terraria arrays so runtime tile IDs above vanilla are safe.
    /// This is the first tile-global mutation moved into the shared runtime.
    /// </summary>
    internal static class TileTypeExtension
    {
        private static ILogger _log;
        private static bool _applied;
        private static readonly List<string> _resizedMembers = new List<string>();
        private static readonly List<string> _criticalFailures = new List<string>();

        public static int OriginalCount { get; private set; }
        public static int ExtendedCount { get; private set; }

        public static int Apply(ILogger logger, int newCount, bool failFast = true)
        {
            _log = logger;
            if (_applied)
            {
                _log?.Warn("[TileRuntime.TileTypeExtension] Already applied");
                return OriginalCount;
            }

            _resizedMembers.Clear();
            _criticalFailures.Clear();

            try
            {
                var tileIdType = typeof(Terraria.ID.TileID);
                var countField = tileIdType.GetField("Count", BindingFlags.Public | BindingFlags.Static);
                if (countField == null)
                {
                    _log?.Error("[TileRuntime.TileTypeExtension] TileID.Count field not found");
                    return -1;
                }

                OriginalCount = ReadCountValue(countField);
                if (OriginalCount <= 0)
                {
                    _log?.Error("[TileRuntime.TileTypeExtension] Invalid TileID.Count value");
                    return -1;
                }

                if (newCount <= OriginalCount)
                    newCount = OriginalCount + 64;

                if (newCount > ushort.MaxValue)
                {
                    _log?.Warn($"[TileRuntime.TileTypeExtension] Requested count {newCount} exceeds ushort max; clamping to {ushort.MaxValue}");
                    newCount = ushort.MaxValue;
                }

                ExtendedCount = newCount;
                _log?.Info($"[TileRuntime.TileTypeExtension] Original TileID.Count={OriginalCount}, extending to {ExtendedCount}");

                int setsResized = ResizeTileIdSets(tileIdType, OriginalCount, ExtendedCount);
                int textureResized = ResizeTextureAssets(OriginalCount, ExtendedCount);
                int mainResized = ResizeMainArrays(OriginalCount, ExtendedCount);
                int langResized = ResizeLangArrays(OriginalCount, ExtendedCount);
                int assemblyResized = ResizeAllAssemblyArrays(OriginalCount, ExtendedCount);
                int sceneMetricsResized = ResizeSceneMetricsInstances(OriginalCount, ExtendedCount);

                SetCountValue(countField, ExtendedCount);
                ValidateCriticalArrays(ExtendedCount);

                if (_criticalFailures.Count > 0)
                {
                    string joined = string.Join("; ", _criticalFailures.ToArray());
                    _log?.Error($"[TileRuntime.TileTypeExtension] Critical tile array failures: {joined}");
                    if (failFast)
                        throw new InvalidOperationException("Critical tile array extension failed");
                }

                _applied = true;
                _log?.Info($"[TileRuntime.TileTypeExtension] Complete: {setsResized} sets + {textureResized} texture + {mainResized} main + {langResized} lang + {assemblyResized} assembly arrays + {sceneMetricsResized} scene-metrics arrays");
                _log?.Info($"[TileRuntime.TileTypeExtension] Resize inventory count: {_resizedMembers.Count}");
                return OriginalCount;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Failed: {ex.Message}\n{ex.StackTrace}");
                return -1;
            }
        }

        public static int RefreshSceneMetricsInstances(ILogger logger = null)
        {
            if (logger != null)
                _log = logger;

            if (!_applied || OriginalCount <= 0 || ExtendedCount <= 0 || ExtendedCount <= OriginalCount)
                return 0;

            return ResizeSceneMetricsInstances(OriginalCount, ExtendedCount);
        }

        private static int ReadCountValue(FieldInfo countField)
        {
            object value = countField.GetValue(null);
            if (value is int i) return i;
            if (value is short s) return s;
            if (value is ushort us) return us;
            return -1;
        }

        private static void SetCountValue(FieldInfo countField, int value)
        {
            if (countField.FieldType == typeof(int))
            {
                countField.SetValue(null, value);
            }
            else if (countField.FieldType == typeof(short))
            {
                if (value > short.MaxValue)
                {
                    _log?.Warn($"[TileRuntime.TileTypeExtension] TileID.Count is short; clamping {value} to {short.MaxValue}");
                    value = short.MaxValue;
                }

                countField.SetValue(null, (short)value);
            }
            else if (countField.FieldType == typeof(ushort))
            {
                countField.SetValue(null, (ushort)value);
            }
            else
            {
                countField.SetValue(null, value);
            }

            _log?.Info($"[TileRuntime.TileTypeExtension] TileID.Count updated to {value}");
        }

        private static int ResizeTileIdSets(Type tileIdType, int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var setsType = tileIdType.GetNestedType("Sets", BindingFlags.Public);
                if (setsType == null)
                {
                    _log?.Warn("[TileRuntime.TileTypeExtension] TileID.Sets not found");
                    _criticalFailures.Add("TileID.Sets missing");
                    return 0;
                }

                var factoryField = setsType.GetField("Factory", BindingFlags.Public | BindingFlags.Static);
                if (factoryField != null)
                {
                    var factory = factoryField.GetValue(null);
                    if (factory != null)
                    {
                        var sizeField = factory.GetType().GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance);
                        sizeField?.SetValue(factory, newSize);

                        foreach (var cacheName in new[] { "_boolBufferCache", "_intBufferCache", "_ushortBufferCache", "_floatBufferCache" })
                        {
                            var cacheField = factory.GetType().GetField(cacheName, BindingFlags.NonPublic | BindingFlags.Instance);
                            var cache = cacheField?.GetValue(factory);
                            cache?.GetType().GetMethod("Clear")?.Invoke(cache, null);
                        }

                        _resizedMembers.Add("TileID.Sets.Factory._size");
                    }
                }

                foreach (var field in setsType.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.Name == "Factory" || !field.FieldType.IsArray)
                        continue;

                    var arr = field.GetValue(null) as Array;
                    if (arr == null || arr.Length != oldSize)
                        continue;

                    if (TryResizeArrayField(field, null, arr, oldSize, newSize))
                    {
                        _resizedMembers.Add($"TileID.Sets.{field.Name}");
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Error resizing TileID.Sets arrays: {ex.Message}");
            }

            return count;
        }

        private static int ResizeTextureAssets(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var texType = typeof(Terraria.GameContent.TextureAssets);
                foreach (var field in texType.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!field.FieldType.IsArray || !field.Name.StartsWith("Tile", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var arr = field.GetValue(null) as Array;
                    if (arr == null || arr.Length != oldSize)
                        continue;

                    if (!TryResizeArrayField(field, null, arr, oldSize, newSize, fillWithFirstEntry: true))
                        continue;

                    _resizedMembers.Add($"TextureAssets.{field.Name}");
                    count++;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Error resizing TextureAssets.Tile arrays: {ex.Message}");
            }

            return count;
        }

        private static int ResizeMainArrays(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var mainType = typeof(Terraria.Main);
                foreach (var field in mainType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!field.FieldType.IsArray)
                        continue;

                    var arr = field.GetValue(null) as Array;
                    if (arr == null || arr.Length != oldSize)
                        continue;

                    if (!TryResizeArrayField(field, null, arr, oldSize, newSize))
                        continue;

                    _resizedMembers.Add($"Main.{field.Name}");
                    count++;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Error resizing Main tile arrays: {ex.Message}");
            }

            return count;
        }

        private static int ResizeLangArrays(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var langType = typeof(Terraria.Lang);
                foreach (var field in langType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!field.FieldType.IsArray)
                        continue;

                    var arr = field.GetValue(null) as Array;
                    if (arr == null || arr.Length != oldSize)
                        continue;

                    if (!TryResizeArrayField(field, null, arr, oldSize, newSize, fillWithFirstEntry: true))
                        continue;

                    _resizedMembers.Add($"Lang.{field.Name}");
                    count++;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Error resizing Lang tile arrays: {ex.Message}");
            }

            return count;
        }

        private static int ResizeAllAssemblyArrays(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var asm = typeof(Terraria.Main).Assembly;
                foreach (var type in asm.GetTypes())
                {
                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var field in fields)
                    {
                        if (!field.FieldType.IsArray)
                            continue;

                        Array arr;
                        try { arr = field.GetValue(null) as Array; }
                        catch { continue; }

                        if (arr == null || arr.Length != oldSize)
                            continue;

                        if (!TryResizeArrayField(field, null, arr, oldSize, newSize))
                            continue;

                        _resizedMembers.Add($"{type.FullName}.{field.Name}");
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Assembly scan error: {ex.Message}");
            }

            return count;
        }

        private static int ResizeSceneMetricsInstances(int oldSize, int newSize)
        {
            int count = 0;
            try
            {
                var asm = typeof(Terraria.Main).Assembly;
                var sceneMetricsType = asm.GetType("Terraria.SceneMetrics");
                if (sceneMetricsType == null)
                {
                    _log?.Warn("[TileRuntime.TileTypeExtension] Terraria.SceneMetrics type not found");
                    return 0;
                }

                foreach (var type in asm.GetTypes())
                {
                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var field in fields)
                    {
                        if (field.FieldType != sceneMetricsType)
                            continue;

                        object instance;
                        try
                        {
                            instance = field.GetValue(null);
                        }
                        catch
                        {
                            continue;
                        }

                        if (instance == null)
                            continue;

                        count += ResizeInstanceArrayFields(instance, oldSize, newSize, $"{type.FullName}.{field.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTypeExtension] Error resizing SceneMetrics instances: {ex.Message}");
            }

            return count;
        }

        private static int ResizeInstanceArrayFields(object instance, int oldSize, int newSize, string ownerLabel)
        {
            int count = 0;
            var instanceType = instance.GetType();
            foreach (var field in instanceType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!field.FieldType.IsArray)
                    continue;

                Array source;
                try
                {
                    source = field.GetValue(instance) as Array;
                }
                catch
                {
                    continue;
                }

                if (source == null || source.Length != oldSize)
                    continue;

                try
                {
                    var elementType = field.FieldType.GetElementType();
                    var dest = Array.CreateInstance(elementType, newSize);
                    Array.Copy(source, dest, Math.Min(source.Length, newSize));
                    FillNewEntries(dest, source, oldSize, newSize, elementType);
                    field.SetValue(instance, dest);

                    _resizedMembers.Add($"{ownerLabel}.{field.Name}");
                    count++;
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[TileRuntime.TileTypeExtension] Failed to resize instance array {ownerLabel}.{field.Name}: {ex.Message}");
                }
            }

            return count;
        }

        private static bool TryResizeArrayField(FieldInfo field, object target, Array source, int oldSize, int newSize, bool fillWithFirstEntry = false)
        {
            try
            {
                if (source.Rank != 1)
                    return false;

                var elementType = field.FieldType.GetElementType();
                if (elementType != null && elementType.IsArray)
                {
                    var destJagged = ResizeJaggedArray(source, oldSize, newSize, elementType);
                    if (destJagged == null)
                        return false;

                    field.SetValue(target, destJagged);
                    return true;
                }

                var dest = Array.CreateInstance(elementType, newSize);
                Array.Copy(source, dest, Math.Min(source.Length, newSize));

                if (fillWithFirstEntry && oldSize > 0)
                {
                    var placeholder = source.GetValue(0);
                    if (placeholder != null)
                    {
                        for (int i = oldSize; i < newSize; i++)
                            dest.SetValue(placeholder, i);
                    }
                }
                else
                {
                    FillNewEntries(dest, source, oldSize, newSize, elementType);
                }

                field.SetValue(target, dest);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileTypeExtension] Failed to resize {field.DeclaringType?.Name}.{field.Name}: {ex.Message}");
                return false;
            }
        }

        private static Array ResizeJaggedArray(Array source, int oldSize, int newSize, Type rowType)
        {
            var dest = Array.CreateInstance(rowType, newSize);
            int copiedRows = Math.Min(source.Length, newSize);
            for (int rowIndex = 0; rowIndex < copiedRows; rowIndex++)
            {
                var sourceRow = source.GetValue(rowIndex) as Array;
                if (sourceRow == null)
                {
                    dest.SetValue(CreateJaggedRow(rowType, newSize), rowIndex);
                    continue;
                }

                if (sourceRow.Rank != 1)
                {
                    dest.SetValue(sourceRow, rowIndex);
                    continue;
                }

                var newRow = Array.CreateInstance(rowType.GetElementType(), newSize);
                Array.Copy(sourceRow, newRow, Math.Min(sourceRow.Length, newSize));
                dest.SetValue(newRow, rowIndex);
            }

            for (int rowIndex = copiedRows; rowIndex < newSize; rowIndex++)
                dest.SetValue(CreateJaggedRow(rowType, newSize), rowIndex);

            return dest;
        }

        private static Array CreateJaggedRow(Type rowType, int newSize)
        {
            var innerType = rowType?.GetElementType();
            return innerType == null ? null : Array.CreateInstance(innerType, newSize);
        }

        private static void FillNewEntries(Array newArr, Array oldArr, int oldSize, int newSize, Type elemType)
        {
            if (oldSize >= newSize)
                return;

            if (elemType == typeof(int))
            {
                var oldTyped = (int[])oldArr;
                int defaultVal = DetectIntDefault(oldTyped);
                if (defaultVal != 0)
                {
                    var newTyped = (int[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newTyped[i] = defaultVal;
                }
            }
            else if (elemType == typeof(short))
            {
                var oldTyped = (short[])oldArr;
                short defaultVal = DetectShortDefault(oldTyped);
                if (defaultVal != 0)
                {
                    var newTyped = (short[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newTyped[i] = defaultVal;
                }
            }
            else if (elemType == typeof(float))
            {
                var oldTyped = (float[])oldArr;
                float defaultVal = DetectFloatDefault(oldTyped);
                if (Math.Abs(defaultVal) > 0.0001f)
                {
                    var newTyped = (float[])newArr;
                    for (int i = oldSize; i < newSize; i++)
                        newTyped[i] = defaultVal;
                }
            }
        }

        private static int DetectIntDefault(int[] arr)
        {
            int countNeg1 = 0;
            int countZero = 0;
            int start = Math.Max(0, arr.Length - 128);
            for (int i = start; i < arr.Length; i++)
            {
                if (arr[i] == -1) countNeg1++;
                else if (arr[i] == 0) countZero++;
            }

            return countNeg1 > countZero ? -1 : 0;
        }

        private static short DetectShortDefault(short[] arr)
        {
            int countNeg1 = 0;
            int countZero = 0;
            int start = Math.Max(0, arr.Length - 128);
            for (int i = start; i < arr.Length; i++)
            {
                if (arr[i] == -1) countNeg1++;
                else if (arr[i] == 0) countZero++;
            }

            return countNeg1 > countZero ? (short)-1 : (short)0;
        }

        private static float DetectFloatDefault(float[] arr)
        {
            int countOne = 0;
            int countZero = 0;
            int start = Math.Max(0, arr.Length - 128);
            for (int i = start; i < arr.Length; i++)
            {
                if (Math.Abs(arr[i] - 1f) < 0.0001f) countOne++;
                else if (Math.Abs(arr[i]) < 0.0001f) countZero++;
            }

            return countOne > countZero ? 1f : 0f;
        }

        private static void ValidateCriticalArrays(int newSize)
        {
            ValidateArrayLength(typeof(Terraria.Main), "tileSolid", newSize);
            ValidateArrayLength(typeof(Terraria.Main), "tileStone", newSize);
            ValidateArrayLength(typeof(Terraria.Main), "tileFrameImportant", newSize);
            ValidateArrayLength(typeof(Terraria.Main), "tileContainer", newSize);
            ValidateArrayLength(typeof(Terraria.Main), "tileLighted", newSize);
            ValidateArrayLength(typeof(Terraria.Main), "tileBlendAll", newSize);
            ValidateJaggedArrayLengths(typeof(Terraria.Main), "tileMerge", newSize);
            ValidateArrayLength(typeof(Terraria.GameContent.TextureAssets), "Tile", newSize);

            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var basicChestField = setsType?.GetField("BasicChest", BindingFlags.Public | BindingFlags.Static);
                var arr = basicChestField?.GetValue(null) as Array;
                if (arr == null || arr.Length < newSize)
                    _criticalFailures.Add("TileID.Sets.BasicChest");
            }
            catch
            {
                _criticalFailures.Add("TileID.Sets.BasicChest");
            }

            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var factoryField = setsType?.GetField("Factory", BindingFlags.Public | BindingFlags.Static);
                var factory = factoryField?.GetValue(null);
                var sizeField = factory?.GetType().GetField("_size", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(sizeField?.GetValue(factory) is int size) || size < newSize)
                    _criticalFailures.Add("TileID.Sets.Factory._size");
            }
            catch
            {
                _criticalFailures.Add("TileID.Sets.Factory._size");
            }

            ValidateTileSetLength("BlockMergesWithMergeAllBlock", newSize);
            ValidateTileSetLength("ChecksForMerge", newSize);
            ValidateTileSetLength("ForcedDirtMerging", newSize);
            ValidateTileSetLength("MergesWithClouds", newSize);
        }

        private static void ValidateArrayLength(Type type, string fieldName, int newSize)
        {
            try
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || arr.Length < newSize)
                    _criticalFailures.Add($"{type.Name}.{fieldName}");
            }
            catch
            {
                _criticalFailures.Add($"{type.Name}.{fieldName}");
            }
        }

        private static void ValidateJaggedArrayLengths(Type type, string fieldName, int newSize)
        {
            try
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var outer = field?.GetValue(null) as Array;
                if (outer == null || outer.Length < newSize)
                {
                    _criticalFailures.Add($"{type.Name}.{fieldName}");
                    return;
                }

                for (int i = 0; i < newSize; i++)
                {
                    var row = outer.GetValue(i) as Array;
                    if (row == null || row.Length < newSize)
                    {
                        _criticalFailures.Add($"{type.Name}.{fieldName}[{i}]");
                        return;
                    }
                }
            }
            catch
            {
                _criticalFailures.Add($"{type.Name}.{fieldName}");
            }
        }

        private static void ValidateTileSetLength(string fieldName, int newSize)
        {
            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var field = setsType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || arr.Length < newSize)
                    _criticalFailures.Add($"TileID.Sets.{fieldName}");
            }
            catch
            {
                _criticalFailures.Add($"TileID.Sets.{fieldName}");
            }
        }
    }
}

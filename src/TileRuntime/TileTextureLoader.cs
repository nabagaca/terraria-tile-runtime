using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Loads and injects runtime-owned custom tile textures into TextureAssets.Tile[runtimeTileId].
    /// </summary>
    internal static class TileTextureLoader
    {
        private static ILogger _log;
        private static Type _texture2dType;
        private static MethodInfo _fromStreamMethod;
        private static object _graphicsDevice;
        private static bool _reflectionReady;
        private static bool _reflectionFailed;
        private static bool _patchesApplied;
        private static Harmony _harmony;
        private static MethodInfo _assetInitializerLoadAssetGenericMethod;
        private static bool _loggedOutlineFallbackPatch;
        private static MethodInfo _assetInitializerLoadTexturesMethod;
        private static bool _outlineSuppressionActive;
        private static readonly HashSet<int> _suppressedOutlineTileTypes = new HashSet<int>();

        private static readonly Dictionary<int, object> _assetCache = new Dictionary<int, object>();
        private static readonly Dictionary<int, object> _highlightAssetCache = new Dictionary<int, object>();
        private static readonly Dictionary<int, string> _runtimeTexturePathCache = new Dictionary<int, string>();
        private static readonly HashSet<int> _outlineGeneratedLogged = new HashSet<int>();
        private static readonly HashSet<int> _outlineFallbackLogged = new HashSet<int>();
        private static readonly HashSet<int> _outlineFailureLogged = new HashSet<int>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tiletextures");
        }

        public static void ApplyPatches()
        {
            if (_patchesApplied) return;

            try
            {
                var loadTiles = typeof(Terraria.Main).GetMethod("LoadTiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(int) },
                    null);

                if (loadTiles == null)
                {
                    _log?.Warn("[TileRuntime.TileTextureLoader] Main.LoadTiles(int) not found");
                    return;
                }

                var prefix = typeof(TileTextureLoader).GetMethod(nameof(LoadTiles_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(loadTiles, prefix: new HarmonyMethod(prefix));

                PatchOutlineAssetFallback();
                PatchLoadTexturesOutlineGuard();

                _patchesApplied = true;
                _log?.Info("[TileRuntime.TileTextureLoader] Patched Main.LoadTiles(int) for custom tile IDs");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTextureLoader] Failed to patch Main.LoadTiles: {ex.Message}");
            }
        }

        public static int InjectAllTextures()
        {
            if (!InitializeReflection())
            {
                _log?.Warn("[TileRuntime.TileTextureLoader] Cannot inject textures - XNA reflection not ready");
                return 0;
            }

            int injected = 0;
            int placeholders = 0;

            foreach (var fullId in TileRegistry.AllIds)
            {
                int runtimeType = TileRegistry.GetRuntimeType(fullId);
                if (runtimeType < 0) continue;

                var def = TileRegistry.GetDefinitionById(fullId);
                if (def == null) continue;

                bool hasTexture = false;
                if (!string.IsNullOrEmpty(def.TexturePath))
                {
                    int colon = fullId.IndexOf(':');
                    if (colon > 0)
                    {
                        string modId = fullId.Substring(0, colon);
                        string modFolder = TileRegistry.GetModFolder(modId);
                        if (string.IsNullOrEmpty(modFolder))
                        {
                            _log?.Warn($"[TileRuntime.TileTextureLoader] No mod folder registered for {modId} while loading {fullId}");
                        }
                        else
                        {
                            string path = Path.Combine(modFolder, def.TexturePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(path))
                            {
                                if (def.AnimateFromGif)
                                {
                                    if (LoadGifAsSpriteSheet(runtimeType, path, def))
                                    {
                                        injected++;
                                        hasTexture = true;
                                    }
                                }
                                else if (InjectTexture(runtimeType, path))
                                {
                                    injected++;
                                    hasTexture = true;
                                }
                            }
                            else
                            {
                                _log?.Warn($"[TileRuntime.TileTextureLoader] Texture not found: {path} for {fullId}");
                            }
                        }
                    }
                }

                if (!hasTexture && InjectPlaceholder(runtimeType))
                    placeholders++;
            }

            _log?.Info($"[TileRuntime.TileTextureLoader] Injected {injected} textures, {placeholders} placeholders");
            EnsureHighlightMaskAssignments();
            return injected + placeholders;
        }

        public static int ReinjectCachedTextures()
        {
            if (_assetCache.Count == 0) return 0;

            try
            {
                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null) return 0;

                int restored = 0;
                foreach (var kvp in _assetCache)
                {
                    if (kvp.Key >= 0 && kvp.Key < arr.Length)
                    {
                        arr.SetValue(kvp.Value, kvp.Key);
                        restored++;
                    }
                }

                EnsureHighlightMaskAssignments();
                return restored;
            }
            catch
            {
                return 0;
            }
        }

        private static void PatchOutlineAssetFallback()
        {
            try
            {
                var assetInitializerType = typeof(Main).Assembly.GetType("Terraria.Initializers.AssetInitializer");
                if (assetInitializerType == null)
                    return;

                foreach (var method in assetInitializerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (method.Name != "LoadAsset" || !method.IsGenericMethodDefinition)
                        continue;

                    var p = method.GetParameters();
                    if (p.Length == 2 && p[0].ParameterType == typeof(string))
                    {
                        _assetInitializerLoadAssetGenericMethod = method;
                        break;
                    }
                }

                if (_assetInitializerLoadAssetGenericMethod == null)
                    return;

                var prefix = typeof(TileTextureLoader).GetMethod(nameof(LoadAsset_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix == null)
                    return;

                // Construct and patch closed generic LoadAsset<T> methods for known TextureAssets element types.
                var patchedAny = false;
                var assetPayloadTypes = new HashSet<Type>();
                try
                {
                    var tileField = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                    var tileArr = tileField?.GetValue(null) as Array;
                    var tileAssetType = tileArr?.GetType().GetElementType();
                    var tilePayloadType = GetAssetPayloadType(tileAssetType);
                    if (tilePayloadType != null) assetPayloadTypes.Add(tilePayloadType);

                    var highlightField = typeof(Terraria.GameContent.TextureAssets).GetField("HighlightMask", BindingFlags.Public | BindingFlags.Static);
                    var highlightArr = highlightField?.GetValue(null) as Array;
                    var highlightAssetType = highlightArr?.GetType().GetElementType();
                    var highlightPayloadType = GetAssetPayloadType(highlightAssetType);
                    if (highlightPayloadType != null) assetPayloadTypes.Add(highlightPayloadType);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to determine asset element types: {ex.Message}");
                }

                if (assetPayloadTypes.Count == 0)
                {
                    _log?.Warn("[TileRuntime.TileTextureLoader] No asset element types discovered; cannot patch AssetInitializer.LoadAsset generics deterministically");
                    return;
                }

                foreach (var payloadType in assetPayloadTypes)
                {
                    try
                    {
                        var constructed = _assetInitializerLoadAssetGenericMethod.MakeGenericMethod(payloadType);
                        _harmony.Patch(constructed, prefix: new HarmonyMethod(prefix));
                        patchedAny = true;
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to patch LoadAsset<{payloadType.Name}>: {ex.Message}");
                    }
                }

                if (patchedAny && !_loggedOutlineFallbackPatch)
                {
                    _loggedOutlineFallbackPatch = true;
                    _log?.Info("[TileRuntime.TileTextureLoader] Patched AssetInitializer.LoadAsset for custom tile outlines");
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to patch outline fallback: {ex.Message}");
            }
        }

        private static Type GetAssetPayloadType(Type arrayElementType)
        {
            if (arrayElementType == null)
                return null;

            if (!arrayElementType.IsGenericType)
                return null;

            var genericDef = arrayElementType.GetGenericTypeDefinition();
            if (!string.Equals(genericDef.FullName, "ReLogic.Content.Asset`1", StringComparison.Ordinal))
                return null;

            var args = arrayElementType.GetGenericArguments();
            return args.Length == 1 ? args[0] : null;
        }

        private static void PatchLoadTexturesOutlineGuard()
        {
            try
            {
                var assetInitializerType = typeof(Main).Assembly.GetType("Terraria.Initializers.AssetInitializer");
                if (assetInitializerType == null)
                    return;

                foreach (var method in assetInitializerType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (method.Name != "LoadTextures")
                        continue;

                    var p = method.GetParameters();
                    if (p.Length == 1)
                    {
                        _assetInitializerLoadTexturesMethod = method;
                        break;
                    }
                }

                if (_assetInitializerLoadTexturesMethod == null)
                    return;

                var prefix = typeof(TileTextureLoader).GetMethod(nameof(LoadTextures_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                var postfix = typeof(TileTextureLoader).GetMethod(nameof(LoadTextures_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                if (prefix == null || postfix == null)
                    return;

                _harmony.Patch(
                    _assetInitializerLoadTexturesMethod,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));
                _log?.Info("[TileRuntime.TileTextureLoader] Patched AssetInitializer.LoadTextures outline guard");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to patch LoadTextures outline guard: {ex.Message}");
            }
        }

        private static void LoadTextures_Prefix()
        {
            try
            {
                _suppressedOutlineTileTypes.Clear();
                if (TileRegistry.Count == 0)
                    return;

                foreach (var fullId in TileRegistry.AllIds)
                {
                    int runtimeType = TileRegistry.GetRuntimeType(fullId);
                    if (runtimeType < 0)
                        continue;

                    var def = TileRegistry.GetDefinition(runtimeType);
                    if (!CustomTileContainers.ShouldHaveOutline(runtimeType, def))
                        continue;

                    if (GetTileSetBool("HasOutlines", runtimeType))
                    {
                        SetTileSetBool("HasOutlines", runtimeType, false);
                        _suppressedOutlineTileTypes.Add(runtimeType);
                    }
                }

                _outlineSuppressionActive = _suppressedOutlineTileTypes.Count > 0;
            }
            catch
            {
            }
        }

        private static void LoadTextures_Postfix()
        {
            try
            {
                if (!_outlineSuppressionActive)
                    return;

                foreach (int runtimeType in _suppressedOutlineTileTypes)
                    SetTileSetBool("HasOutlines", runtimeType, true);

                EnsureHighlightMaskAssignments();
            }
            catch
            {
            }
            finally
            {
                _suppressedOutlineTileTypes.Clear();
                _outlineSuppressionActive = false;
            }
        }

        private static void LoadAsset_Prefix(ref string assetName)
        {
            try
            {
                if (string.IsNullOrEmpty(assetName))
                    return;

                const string outlinePrefix = "Images\\Misc\\TileOutlines\\Tiles_";
                if (!assetName.StartsWith(outlinePrefix, StringComparison.OrdinalIgnoreCase))
                    return;

                string idText = assetName.Substring(outlinePrefix.Length);
                if (!int.TryParse(idText, out int tileType))
                    return;

                if (!TileRegistry.IsCustomTile(tileType))
                    return;

                // Keep vanilla load path alive by redirecting missing custom outline assets
                // to a guaranteed existing outline. Runtime assignment later replaces this
                // with the custom tile texture-backed mask.
                assetName = "Images\\Misc\\TileOutlines\\Tiles_21";
            }
            catch
            {
            }
        }

        private static void EnsureHighlightMaskAssignments()
        {
            try
            {
                InitializeReflection();

                var highlightField = typeof(Terraria.GameContent.TextureAssets).GetField("HighlightMask", BindingFlags.Public | BindingFlags.Static);
                var tileField = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var highlightArray = highlightField?.GetValue(null) as Array;
                var tileArray = tileField?.GetValue(null) as Array;
                if (highlightArray == null || tileArray == null)
                    return;

                object fallback = null;
                if (highlightArray.Length > 21)
                    fallback = highlightArray.GetValue(21);
                if (fallback == null && highlightArray.Length > 0)
                    fallback = highlightArray.GetValue(0);

                foreach (var fullId in TileRegistry.AllIds)
                {
                    int runtimeType = TileRegistry.GetRuntimeType(fullId);
                    if (runtimeType < 0 || runtimeType >= highlightArray.Length || runtimeType >= tileArray.Length)
                        continue;

                    var def = TileRegistry.GetDefinition(runtimeType);
                    if (!CustomTileContainers.ShouldHaveOutline(runtimeType, def))
                        continue;

                    object tileAsset = tileArray.GetValue(runtimeType);
                    object highlightAsset = GetOrCreateHighlightMaskAsset(fullId, runtimeType, def, tileAsset);
                    if (highlightAsset != null)
                    {
                        highlightArray.SetValue(highlightAsset, runtimeType);
                        if (_outlineGeneratedLogged.Add(runtimeType))
                            _log?.Info($"[TileRuntime.TileTextureLoader] Highlight mask generated for {fullId} (type {runtimeType})");
                    }
                    else
                    {
                        highlightArray.SetValue(fallback, runtimeType);
                        if (_outlineFallbackLogged.Add(runtimeType))
                            _log?.Warn($"[TileRuntime.TileTextureLoader] Highlight mask fallback used for {fullId} (type {runtimeType})");
                    }
                }
            }
            catch
            {
            }
        }

        private static object GetOrCreateHighlightMaskAsset(string fullId, int runtimeType, TileDefinition definition, object tileAsset)
        {
            if (tileAsset == null)
                return null;

            if (_highlightAssetCache.TryGetValue(runtimeType, out var cached) && cached != null)
                return cached;

            try
            {
                object wrapper = null;
                if (definition != null && !string.IsNullOrWhiteSpace(definition.OutlineTexturePath))
                {
                    wrapper = CreateManualHighlightMaskAsset(fullId, runtimeType, definition, tileAsset.GetType());
                    if (wrapper != null)
                    {
                        _highlightAssetCache[runtimeType] = wrapper;
                        return wrapper;
                    }
                }

                if (definition != null && !definition.AutoGenerateOutline)
                {
                    LogOutlineGenerationFailure(runtimeType, "auto outline generation disabled");
                    return null;
                }

                object tileTexture = ExtractAssetTextureValue(tileAsset);
                if (tileTexture == null)
                {
                    LogOutlineGenerationFailure(runtimeType, "tile asset value was null");
                    return null;
                }

                wrapper = CreateSilhouetteMaskAssetFromSourcePng(runtimeType, tileAsset.GetType());
                if (wrapper == null)
                {
                    object maskTexture = CreateSilhouetteMaskTexture(runtimeType, tileTexture);
                    if (maskTexture == null)
                    {
                        LogOutlineGenerationFailure(runtimeType, "mask texture generation returned null");
                        return null;
                    }

                    wrapper = CreateAssetWrapper(tileAsset.GetType(), maskTexture, $"TerrariaModder/CustomTileOutline_{runtimeType}");
                    if (wrapper == null)
                    {
                        LogOutlineGenerationFailure(runtimeType, "asset wrapper creation failed");
                        return null;
                    }
                }

                _highlightAssetCache[runtimeType] = wrapper;
                return wrapper;
            }
            catch (Exception ex)
            {
                LogOutlineGenerationFailure(runtimeType, ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static object CreateManualHighlightMaskAsset(string fullId, int runtimeType, TileDefinition definition, Type assetType)
        {
            if (definition == null || assetType == null || string.IsNullOrWhiteSpace(fullId) || string.IsNullOrWhiteSpace(definition.OutlineTexturePath))
                return null;

            int colon = fullId.IndexOf(':');
            if (colon <= 0)
            {
                LogOutlineGenerationFailure(runtimeType, "could not resolve mod id for manual outline texture");
                return null;
            }

            string modId = fullId.Substring(0, colon);
            string modFolder = TileRegistry.GetModFolder(modId);
            if (string.IsNullOrWhiteSpace(modFolder))
            {
                LogOutlineGenerationFailure(runtimeType, $"mod folder not registered for '{modId}'");
                return null;
            }

            string path = Path.Combine(modFolder, definition.OutlineTexturePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                LogOutlineGenerationFailure(runtimeType, $"manual outline texture not found: {path}");
                return null;
            }

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    object maskTexture = _fromStreamMethod?.Invoke(null, new object[] { _graphicsDevice, stream });
                    if (maskTexture == null)
                    {
                        LogOutlineGenerationFailure(runtimeType, "manual outline texture load returned null");
                        return null;
                    }

                    object wrapper = CreateAssetWrapper(assetType, maskTexture, $"TerrariaModder/CustomTileOutlineManual_{runtimeType}");
                    if (wrapper == null)
                    {
                        LogOutlineGenerationFailure(runtimeType, "manual outline wrapper creation failed");
                        return null;
                    }

                    return wrapper;
                }
            }
            catch (Exception ex)
            {
                LogOutlineGenerationFailure(runtimeType, "manual outline load failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static object ExtractAssetTextureValue(object assetWrapper)
        {
            if (assetWrapper == null)
                return null;

            Type type = assetWrapper.GetType();
            try
            {
                var prop = type.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(assetWrapper, null);
            }
            catch
            {
            }

            try
            {
                var valueField = type.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? type.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? type.GetField("ownValue", BindingFlags.NonPublic | BindingFlags.Instance);
                return valueField?.GetValue(assetWrapper);
            }
            catch
            {
                return null;
            }
        }

        private static object CreateSilhouetteMaskAssetFromSourcePng(int runtimeType, Type assetType)
        {
            if (assetType == null)
                return null;

            if (!_runtimeTexturePathCache.TryGetValue(runtimeType, out var pngPath) || string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath))
            {
                LogOutlineGenerationFailure(runtimeType, "source texture path missing");
                return null;
            }

            try
            {
                using (var source = new Bitmap(pngPath))
                using (var mask = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
                {
                    int width = source.Width;
                    int height = source.Height;
                    var opaque = new bool[width, height];

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            opaque[x, y] = source.GetPixel(x, y).A > 0;
                        }
                    }

                    // Edge-only mask: keep only boundary pixels, not full silhouette fill.
                    // Treat vanilla-style tile sheet gutters (16px cell + 2px padding) as connected
                    // so we do not generate false interior edges at frame padding boundaries.
                    for (int y = 0; y < source.Height; y++)
                    {
                        for (int x = 0; x < source.Width; x++)
                        {
                            if (!opaque[x, y])
                            {
                                mask.SetPixel(x, y, System.Drawing.Color.Transparent);
                                continue;
                            }

                            bool hasLeft = IsNeighborOpaqueOrAcrossPadding(opaque, width, height, x, y, -1, 0);
                            bool hasRight = IsNeighborOpaqueOrAcrossPadding(opaque, width, height, x, y, 1, 0);
                            bool hasUp = IsNeighborOpaqueOrAcrossPadding(opaque, width, height, x, y, 0, -1);
                            bool hasDown = IsNeighborOpaqueOrAcrossPadding(opaque, width, height, x, y, 0, 1);
                            bool edge = !(hasLeft && hasRight && hasUp && hasDown);

                            mask.SetPixel(
                                x,
                                y,
                                edge
                                    ? System.Drawing.Color.FromArgb(255, 255, 255, 255)
                                    : System.Drawing.Color.Transparent);
                        }
                    }

                    using (var stream = new MemoryStream())
                    {
                        mask.Save(stream, ImageFormat.Png);
                        stream.Position = 0;

                        object maskTexture = _fromStreamMethod?.Invoke(null, new object[] { _graphicsDevice, stream });
                        if (maskTexture == null)
                        {
                            LogOutlineGenerationFailure(runtimeType, "Texture2D.FromStream returned null");
                            return null;
                        }

                        object wrapper = CreateAssetWrapper(assetType, maskTexture, $"TerrariaModder/CustomTileOutline_{runtimeType}");
                        if (wrapper == null)
                            LogOutlineGenerationFailure(runtimeType, "asset wrapper creation failed (png path)");
                        return wrapper;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOutlineGenerationFailure(runtimeType, "PNG mask path failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static bool IsNeighborOpaqueOrAcrossPadding(bool[,] opaque, int width, int height, int x, int y, int dx, int dy)
        {
            int nx = x + dx;
            int ny = y + dy;
            if (nx >= 0 && nx < width && ny >= 0 && ny < height && opaque[nx, ny])
                return true;

            const int cellSize = 16;
            const int padding = 2;
            const int step = cellSize + padding;
            int bridge = padding + 1;

            if (dx == 1 && dy == 0)
            {
                if (x % step == cellSize - 1)
                {
                    int bx = x + bridge;
                    return bx >= 0 && bx < width && opaque[bx, y];
                }
                return false;
            }

            if (dx == -1 && dy == 0)
            {
                if (x % step == 0)
                {
                    int bx = x - bridge;
                    return bx >= 0 && bx < width && opaque[bx, y];
                }
                return false;
            }

            if (dx == 0 && dy == 1)
            {
                if (y % step == cellSize - 1)
                {
                    int by = y + bridge;
                    return by >= 0 && by < height && opaque[x, by];
                }
                return false;
            }

            if (dx == 0 && dy == -1)
            {
                if (y % step == 0)
                {
                    int by = y - bridge;
                    return by >= 0 && by < height && opaque[x, by];
                }
                return false;
            }

            return false;
        }

        private static void LogOutlineGenerationFailure(int runtimeType, string reason)
        {
            if (!_outlineFailureLogged.Add(runtimeType))
                return;

            string fullId = TileRegistry.GetFullId(runtimeType) ?? "<unknown>";
            _log?.Warn($"[TileRuntime.TileTextureLoader] Highlight mask generation failed for {fullId} (type {runtimeType}): {reason}");
        }

        private static object CreateSilhouetteMaskTexture(int runtimeType, object sourceTexture)
        {
            if (sourceTexture == null || _graphicsDevice == null || _texture2dType == null)
                return null;

            try
            {
                int width = Convert.ToInt32(_texture2dType.GetProperty("Width", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sourceTexture, null));
                int height = Convert.ToInt32(_texture2dType.GetProperty("Height", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sourceTexture, null));
                if (width <= 0 || height <= 0)
                    return null;

                Type colorType = _texture2dType.Assembly.GetType("Microsoft.Xna.Framework.Color");
                if (colorType == null)
                    return null;

                object maskTexture = CreateTexture2D(width, height);
                if (maskTexture == null)
                {
                    LogOutlineGenerationFailure(runtimeType, "could not construct Texture2D for highlight mask");
                    return null;
                }

                MethodInfo getDataGeneric = null;
                MethodInfo setDataGeneric = null;
                foreach (var m in _texture2dType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!m.IsGenericMethodDefinition)
                        continue;

                    if (m.Name == "GetData")
                    {
                        var p = m.GetParameters();
                        if (p.Length == 1)
                        {
                            getDataGeneric = m;
                        }
                    }
                    else if (m.Name == "SetData")
                    {
                        var p = m.GetParameters();
                        if (p.Length == 1)
                        {
                            setDataGeneric = m;
                        }
                    }
                }

                if (getDataGeneric == null || setDataGeneric == null)
                    return null;

                MethodInfo getData = getDataGeneric.MakeGenericMethod(colorType);
                MethodInfo setData = setDataGeneric.MakeGenericMethod(colorType);
                Array pixels = Array.CreateInstance(colorType, width * height);
                getData.Invoke(sourceTexture, new object[] { pixels });

                var colorCtor = colorType.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                if (colorCtor == null)
                    return null;

                for (int i = 0; i < pixels.Length; i++)
                {
                    object color = pixels.GetValue(i);
                    byte a = ReadColorChannel(color, "A");
                    byte alpha = a > 0 ? (byte)255 : (byte)0;
                    pixels.SetValue(colorCtor.Invoke(new object[] { (byte)255, (byte)255, (byte)255, alpha }), i);
                }

                setData.Invoke(maskTexture, new object[] { pixels });
                return maskTexture;
            }
            catch
            {
                return null;
            }
        }

        private static object CreateTexture2D(int width, int height)
        {
            if (_texture2dType == null || _graphicsDevice == null || width <= 0 || height <= 0)
                return null;

            // Preferred ctor: Texture2D(GraphicsDevice, int, int)
            try
            {
                foreach (var ctor in _texture2dType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var p = ctor.GetParameters();
                    if (p.Length == 3 &&
                        p[1].ParameterType == typeof(int) &&
                        p[2].ParameterType == typeof(int) &&
                        p[0].ParameterType.IsAssignableFrom(_graphicsDevice.GetType()))
                    {
                        return ctor.Invoke(new object[] { _graphicsDevice, width, height });
                    }
                }
            }
            catch
            {
            }

            // Fallback ctor: Texture2D(GraphicsDevice, int, int, bool, SurfaceFormat)
            try
            {
                Type surfaceFormatType = _texture2dType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.SurfaceFormat");
                if (surfaceFormatType == null)
                    return null;

                object surfaceFormatColor = Enum.Parse(surfaceFormatType, "Color");
                foreach (var ctor in _texture2dType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var p = ctor.GetParameters();
                    if (p.Length == 5 &&
                        p[1].ParameterType == typeof(int) &&
                        p[2].ParameterType == typeof(int) &&
                        p[3].ParameterType == typeof(bool) &&
                        p[4].ParameterType == surfaceFormatType &&
                        p[0].ParameterType.IsAssignableFrom(_graphicsDevice.GetType()))
                    {
                        return ctor.Invoke(new[] { _graphicsDevice, (object)width, height, false, surfaceFormatColor });
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static byte ReadColorChannel(object color, string name)
        {
            if (color == null || string.IsNullOrEmpty(name))
                return 0;

            Type type = color.GetType();
            try
            {
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                    return Convert.ToByte(field.GetValue(color));
            }
            catch
            {
            }

            try
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                    return Convert.ToByte(prop.GetValue(color, null));
            }
            catch
            {
            }

            return 0;
        }

        private static bool GetTileSetBool(string fieldName, int index)
        {
            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var field = setsType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as bool[];
                return arr != null && index >= 0 && index < arr.Length && arr[index];
            }
            catch
            {
                return false;
            }
        }

        private static void SetTileSetBool(string fieldName, int index, bool value)
        {
            try
            {
                var setsType = typeof(Terraria.ID.TileID).GetNestedType("Sets", BindingFlags.Public);
                var field = setsType?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as bool[];
                if (arr != null && index >= 0 && index < arr.Length)
                    arr[index] = value;
            }
            catch
            {
            }
        }

        private static bool LoadGifAsSpriteSheet(int runtimeType, string gifPath, TileDefinition def)
        {
            try
            {
                using (var gif = Image.FromFile(gifPath))
                {
                    var dimension = new FrameDimension(gif.FrameDimensionsList[0]);
                    int frameCount = gif.GetFrameCount(dimension);
                    if (frameCount <= 0)
                    {
                        _log?.Warn($"[TileRuntime.TileTextureLoader] GIF has no frames: {gifPath}");
                        return false;
                    }

                    int cols = def.Width;
                    int cellW = def.CoordinateWidth;
                    int padding = def.CoordinatePadding;
                    int[] coordHeights = def.CoordinateHeights;
                    if (coordHeights == null || coordHeights.Length == 0)
                    {
                        coordHeights = new int[def.Height];
                        for (int i = 0; i < def.Height; i++)
                            coordHeights[i] = 16;
                    }

                    // Sheet dimensions for one frame: cols cells wide with padding between, rows cells tall with padding between
                    int sheetWidth = cols * cellW + (cols - 1) * padding;
                    int singleFrameHeight = 0;
                    for (int r = 0; r < coordHeights.Length; r++)
                    {
                        singleFrameHeight += coordHeights[r];
                        if (r < coordHeights.Length - 1)
                            singleFrameHeight += padding;
                    }

                    // Terraria's default animation offset formula in GetTileDrawData is:
                    //   addFrY = Main.tileFrame[type] * 38
                    // So frames must be spaced exactly 38px apart in the sprite sheet,
                    // regardless of actual frame content height.
                    const int vanillaFrameStride = 38;
                    int totalSheetHeight = vanillaFrameStride * frameCount;
                    using (var sheet = new Bitmap(sheetWidth, totalSheetHeight, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(sheet))
                    {
                        g.Clear(System.Drawing.Color.Transparent);

                        for (int f = 0; f < frameCount; f++)
                        {
                            gif.SelectActiveFrame(dimension, f);
                            using (var frameBmp = new Bitmap(gif))
                            {
                                int frameY = f * vanillaFrameStride;
                                int srcY = 0;
                                for (int row = 0; row < coordHeights.Length; row++)
                                {
                                    int cellH = coordHeights[row];
                                    int destY = frameY;
                                    for (int r2 = 0; r2 < row; r2++)
                                        destY += coordHeights[r2] + padding;

                                    for (int col = 0; col < cols; col++)
                                    {
                                        int srcX = col * cellW;
                                        int destX = col * (cellW + padding);

                                        // Clamp source region to actual GIF size
                                        int copyW = Math.Min(cellW, frameBmp.Width - srcX);
                                        int copyH = Math.Min(cellH, frameBmp.Height - srcY);
                                        if (copyW > 0 && copyH > 0)
                                        {
                                            var srcRect = new Rectangle(srcX, srcY, copyW, copyH);
                                            var destRect = new Rectangle(destX, destY, copyW, copyH);
                                            g.DrawImage(frameBmp, destRect, srcRect, GraphicsUnit.Pixel);
                                        }
                                    }
                                    srcY += cellH;
                                }
                            }
                        }

                        // Convert to Texture2D via PNG stream
                        using (var stream = new MemoryStream())
                        {
                            sheet.Save(stream, ImageFormat.Png);
                            stream.Position = 0;

                            object texture = _fromStreamMethod.Invoke(null, new object[] { _graphicsDevice, stream });
                            if (texture == null)
                            {
                                _log?.Warn($"[TileRuntime.TileTextureLoader] GIF sprite sheet Texture2D.FromStream returned null for type {runtimeType}");
                                return false;
                            }

                            var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                            var arr = field?.GetValue(null) as Array;
                            if (arr == null || runtimeType < 0 || runtimeType >= arr.Length)
                                return false;

                            var assetType = arr.GetType().GetElementType();
                            var asset = CreateAssetWrapper(assetType, texture, $"TerrariaModder/CustomTile_{runtimeType}");
                            if (asset == null) return false;

                            arr.SetValue(asset, runtimeType);
                            _assetCache[runtimeType] = asset;
                        }

                        // Save first frame as PNG for outline generation
                        string tempPngPath = gifPath + ".spritesheet.png";
                        try
                        {
                            // Extract first-frame region for outline generation
                            using (var firstFrame = new Bitmap(sheetWidth, singleFrameHeight, PixelFormat.Format32bppArgb))
                            using (var fg = Graphics.FromImage(firstFrame))
                            {
                                fg.DrawImage(sheet, new Rectangle(0, 0, sheetWidth, singleFrameHeight),
                                    new Rectangle(0, 0, sheetWidth, singleFrameHeight), GraphicsUnit.Pixel);
                                firstFrame.Save(tempPngPath, ImageFormat.Png);
                            }
                            _runtimeTexturePathCache[runtimeType] = tempPngPath;
                        }
                        catch
                        {
                            _runtimeTexturePathCache[runtimeType] = gifPath;
                        }
                    }

                    def.AnimationFrameCount = frameCount;
                    _log?.Info($"[TileRuntime.TileTextureLoader] Loaded GIF sprite sheet for type {runtimeType}: {frameCount} frames from {gifPath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTextureLoader] Failed to load GIF {gifPath}: {ex.Message}");
                return false;
            }
        }

        private static bool InjectTexture(int runtimeType, string pngPath)
        {
            try
            {
                object texture;
                using (var stream = File.OpenRead(pngPath))
                {
                    texture = _fromStreamMethod.Invoke(null, new object[] { _graphicsDevice, stream });
                }

                if (texture == null)
                    return false;

                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || runtimeType < 0 || runtimeType >= arr.Length)
                    return false;

                var assetType = arr.GetType().GetElementType();
                var asset = CreateAssetWrapper(assetType, texture, $"TerrariaModder/CustomTile_{runtimeType}");
                if (asset == null) return false;

                arr.SetValue(asset, runtimeType);
                _assetCache[runtimeType] = asset;
                _runtimeTexturePathCache[runtimeType] = pngPath;
                return true;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to load {pngPath}: {tie.InnerException.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to load {pngPath}: {ex.Message}");
                return false;
            }
        }

        private static bool InjectPlaceholder(int runtimeType)
        {
            try
            {
                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || runtimeType < 0 || runtimeType >= arr.Length)
                {
                    _log?.Warn($"[TileRuntime.TileTextureLoader] Placeholder injection unavailable for tile {runtimeType}: TextureAssets.Tile not ready");
                    return false;
                }

                object placeholder = arr.GetValue(0);
                if (placeholder == null)
                {
                    _log?.Warn($"[TileRuntime.TileTextureLoader] Placeholder texture slot 0 is null for tile {runtimeType}");
                    return false;
                }

                arr.SetValue(placeholder, runtimeType);
                _assetCache[runtimeType] = placeholder;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LoadTiles_Prefix(int i)
        {
            try
            {
                if (i < TileRegistry.VanillaTileCount)
                    return true;

                ForceTileTextureSlot(i);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void ForceTileTextureSlot(int tileType)
        {
            var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
            var arr = field?.GetValue(null) as Array;
            if (arr == null || tileType < 0 || tileType >= arr.Length)
                return;

            if (_assetCache.TryGetValue(tileType, out var cached) && cached != null)
            {
                arr.SetValue(cached, tileType);
                return;
            }

            object placeholder = arr.GetValue(0);
            if (placeholder != null)
            {
                arr.SetValue(placeholder, tileType);
                _assetCache[tileType] = placeholder;
            }
        }

        private static object CreateAssetWrapper(Type assetType, object texture, string assetName)
        {
            try
            {
                var instance = Activator.CreateInstance(assetType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { assetName }, null);
                if (instance == null)
                    instance = Activator.CreateInstance(assetType, true);
                if (instance == null)
                    return null;

                var valueField = assetType.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("ownValue", BindingFlags.NonPublic | BindingFlags.Instance);
                valueField?.SetValue(instance, texture);

                var stateField = assetType.GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stateField != null)
                {
                    if (stateField.FieldType.IsEnum)
                        stateField.SetValue(instance, Enum.ToObject(stateField.FieldType, 2));
                    else if (stateField.FieldType == typeof(int))
                        stateField.SetValue(instance, 2);
                }

                return instance;
            }
            catch
            {
                return null;
            }
        }

        private static bool InitializeReflection()
        {
            if (_reflectionFailed) return false;
            if (_reflectionReady) return true;

            try
            {
                var mainType = typeof(Terraria.Main);
                var assembly = mainType.Assembly;

                _texture2dType = assembly.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                if (_texture2dType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _texture2dType = asm.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                        if (_texture2dType != null) break;
                    }
                }
                if (_texture2dType == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                var gdType = _texture2dType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.GraphicsDevice");
                if (gdType == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                _fromStreamMethod = _texture2dType.GetMethod("FromStream",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { gdType, typeof(Stream) }, null);
                if (_fromStreamMethod == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                var graphicsProp = typeof(Terraria.Main).GetProperty("graphics", BindingFlags.Public | BindingFlags.Static);
                if (graphicsProp != null)
                {
                    var gm = graphicsProp.GetValue(null);
                    var gdProp = gm?.GetType().GetProperty("GraphicsDevice");
                    _graphicsDevice = gdProp?.GetValue(gm);
                }

                if (_graphicsDevice == null)
                {
                    var instField = typeof(Terraria.Main).GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var inst = instField?.GetValue(null);
                    var gdProp = inst?.GetType().GetProperty("GraphicsDevice");
                    _graphicsDevice = gdProp?.GetValue(inst);
                }

                if (_graphicsDevice == null)
                    return false;

                _reflectionReady = true;
                _log?.Info("[TileRuntime.TileTextureLoader] XNA reflection initialized");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTextureLoader] Reflection init failed: {ex.Message}");
                _reflectionFailed = true;
                return false;
            }
        }
    }
}

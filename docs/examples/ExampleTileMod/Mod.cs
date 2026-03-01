using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;
using TerrariaModder.TileRuntime;

namespace ExampleTileMod
{
    public class Mod : IMod
    {
        private const string TilePolishedSlateId = "example-tile-mod:polished-slate";
        private const string TileFieldChestId = "example-tile-mod:field-chest";
        private const string ItemPolishedSlateId = "example-tile-mod:polished-slate-item";
        private const string ItemFieldChestId = "example-tile-mod:field-chest-item";

        private static readonly List<(ItemDefinition Definition, string TileId)> PlaceableItems =
            new List<(ItemDefinition Definition, string TileId)>();

        private ILogger _log;
        private bool _pendingInventoryRefresh;

        public string Id => "example-tile-mod";
        public string Name => "Example Tile Mod";
        public string Version => "1.0.0";

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            var tiles = context.UseTileRuntime();

            tiles.RegisterTile("polished-slate", new TileDefinition
            {
                DisplayName = "Polished Slate",
                TexturePath = @"assets\tiles\polished-slate.png",
                Width = 1,
                Height = 1,
                Solid = true,
                Brick = true,
                MergeCategories = [TileMergeCategory.Dirt, TileMergeCategory.Stone],
                MergeWith = ["GrayBrick"],
                FrameImportant = false,
                HitSoundStyle = 1,
                MapColorR = 96,
                MapColorG = 103,
                MapColorB = 110,
                DropItemId = ItemPolishedSlateId
            });

            tiles.RegisterTile("field-chest", new TileDefinition
            {
                DisplayName = "Field Chest",
                TexturePath = @"assets\tiles\field-chest.png",
                Width = 2,
                Height = 2,
                OriginX = 0,
                OriginY = 1,
                CoordinateWidth = 16,
                CoordinatePadding = 2,
                CoordinateHeights = new[] { 16, 16 },
                StyleHorizontal = true,
                FrameImportant = true,
                LavaDeath = false,
                IsContainer = true,
                RegisterAsBasicChest = false,
                ContainerName = "Field Chest",
                ContainerCapacity = 20,
                ContainerRequiresEmptyToBreak = true,
                DropItemId = ItemFieldChestId,
                OnRightClick = HandleFieldChestRightClick,
                OnPlace = (x, y) => Main.NewText($"Placed chest at {x}, {y}"),
                OnBreak = (x, y) => Main.NewText($"Removed chest at {x}, {y}")
            });

            RegisterPlaceableItem(context, "polished-slate-item", new ItemDefinition
            {
                DisplayName = "Polished Slate",
                Tooltip = new[] { "A clean stone building block." },
                Texture = @"assets\items\polished-slate-item.png",
                CreateTile = -1,
                PlaceStyle = 0,
                Width = 20,
                Height = 20,
                MaxStack = 999,
                Consumable = true,
                UseStyle = 1,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Material = true,
                Rarity = 1,
                Value = 100
            }, TilePolishedSlateId);

            RegisterPlaceableItem(context, "field-chest-item", new ItemDefinition
            {
                DisplayName = "Field Chest",
                Tooltip = new[] { "A runtime-registered chest tile.", "Stores 20 item stacks." },
                Texture = @"assets\items\field-chest-item.png",
                CreateTile = -1,
                PlaceStyle = 0,
                Width = 20,
                Height = 20,
                MaxStack = 99,
                Consumable = true,
                UseStyle = 1,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 1,
                Value = 500
            }, TileFieldChestId);

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemPolishedSlateId,
                ResultStack = 10,
                Ingredients = new Dictionary<string, int>
                {
                    ["StoneBlock"] = 10
                },
                Station = "WorkBenches"
            });

            context.RegisterRecipe(new RecipeDefinition
            {
                Result = ItemFieldChestId,
                ResultStack = 1,
                Ingredients = new Dictionary<string, int>
                {
                    [ItemPolishedSlateId] = 16,
                    ["Wood"] = 8
                },
                Station = "WorkBenches"
            });

            FrameEvents.OnPostUpdate += HandlePostUpdate;
        }

        public void OnWorldLoad()
        {
            ResolvePlaceableItemTiles();
            _pendingInventoryRefresh = true;
        }

        public void OnWorldUnload() { }

        public void Unload()
        {
            FrameEvents.OnPostUpdate -= HandlePostUpdate;
            _pendingInventoryRefresh = false;
        }

        public static int GetPolishedSlateTileType()
        {
            return TileRuntimeApi.ResolveTile(TilePolishedSlateId);
        }

        private static bool HandleFieldChestRightClick(object playerObject, int tileX, int tileY)
        {
            Main.NewText($"Chest interaction at {tileX}, {tileY}");

            var player = playerObject as Player;
            if (player != null && player.altFunctionUse == 2)
            {
                Main.NewText("Alternate use detected.");
                return true;
            }

            return false;
        }

        private void HandlePostUpdate()
        {
            if (!_pendingInventoryRefresh)
                return;

            if (RefreshPlaceableItemInstances())
                _pendingInventoryRefresh = false;
        }

        private void ResolvePlaceableItemTiles()
        {
            for (int i = 0; i < PlaceableItems.Count; i++)
            {
                var entry = PlaceableItems[i];
                int tileType = TileRuntimeApi.ResolveTile(entry.TileId);
                entry.Definition.CreateTile = tileType;

                if (tileType < 0)
                    _log?.Warn($"Failed to resolve runtime tile '{entry.TileId}' for a placeable example item.");
            }
        }

        private bool RefreshPlaceableItemInstances()
        {
            if (Main.player == null || Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
                return false;

            var player = Main.player[Main.myPlayer];
            if (player?.inventory == null)
                return false;

            var placeableItemTypes = new HashSet<int>();
            foreach (string itemId in new[] { ItemPolishedSlateId, ItemFieldChestId })
            {
                int itemType = ItemRegistry.ResolveItemType(itemId);
                if (itemType > 0)
                    placeableItemTypes.Add(itemType);
            }

            if (placeableItemTypes.Count == 0)
                return false;

            int refreshed = 0;
            for (int i = 0; i < player.inventory.Length; i++)
            {
                Item item = player.inventory[i];
                if (item == null || item.IsAir || !placeableItemTypes.Contains(item.type))
                    continue;

                int type = item.type;
                int stack = item.stack;
                byte prefix = item.prefix;

                item.SetDefaults(type);
                item.stack = stack;
                if (prefix > 0)
                    item.Prefix(prefix);

                refreshed++;
            }

            if (refreshed > 0)
                _log?.Info($"Refreshed {refreshed} placeable example items after tile runtime resolution.");

            return true;
        }

        private static bool RegisterPlaceableItem(ModContext context, string itemName, ItemDefinition definition, string tileId)
        {
            if (!context.RegisterItem(itemName, definition))
                return false;

            PlaceableItems.Add((definition, tileId));
            return true;
        }
    }
}

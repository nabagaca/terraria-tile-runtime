using System;
using System.Collections.Generic;

namespace Terraria
{
    public class Main
    {
        public static bool[] tileSolid = new bool[1024];
        public static bool[] tileSolidTop = new bool[1024];
        public static bool[] tileBrick = new bool[1024];
        public static bool[] tileStone = new bool[1024];
        public static bool[] tileNoAttach = new bool[1024];
        public static bool[] tileTable = new bool[1024];
        public static bool[] tileLighted = new bool[1024];
        public static bool[] tileLavaDeath = new bool[1024];
        public static bool[] tileFrameImportant = new bool[1024];
        public static bool[] tileNoFail = new bool[1024];
        public static bool[] tileCut = new bool[1024];
        public static bool[] tileMergeDirt = new bool[1024];
        public static bool[] tileBlendAll = new bool[1024];
        public static bool[] tileContainer = new bool[1024];
        public static bool[][] tileMerge = CreateMergeMatrix(1024);
        public static Tile[,] tile = new Tile[16, 16];
        public static Chest[] chest = new Chest[1000];
        public static Player[] player = new Player[255];
        public static int maxTilesX = 16;
        public static int maxTilesY = 16;
        public static int maxChests = 1000;
        public static int myPlayer;
        public static int stackSplit;
        public static bool playerInventory;
        public static object ActiveWorldFileData;
        public static object instance;
        public static object graphics;

        public static void NewText(string text)
        {
        }

        private static bool[][] CreateMergeMatrix(int size)
        {
            var matrix = new bool[size][];
            for (int i = 0; i < size; i++)
                matrix[i] = new bool[size];
            return matrix;
        }
    }

    public class Tile
    {
        private bool _active;
        private byte _slope;
        private bool _halfBrick;
        private byte _liquidType;
        private byte _color;
        private byte _wallColor;
        private bool _actuator;
        private bool _inActive;

        public ushort type;
        public short frameX;
        public short frameY;
        public ushort wall;
        public byte liquid;

        public bool active()
        {
            return _active;
        }

        public void active(bool value)
        {
            _active = value;
        }

        public byte slope()
        {
            return _slope;
        }

        public void slope(byte value)
        {
            _slope = value;
        }

        public bool halfBrick()
        {
            return _halfBrick;
        }

        public void halfBrick(bool value)
        {
            _halfBrick = value;
        }

        public byte liquidType()
        {
            return _liquidType;
        }

        public void liquidType(byte value)
        {
            _liquidType = value;
        }

        public byte color()
        {
            return _color;
        }

        public void color(byte value)
        {
            _color = value;
        }

        public byte wallColor()
        {
            return _wallColor;
        }

        public void wallColor(byte value)
        {
            _wallColor = value;
        }

        public bool actuator()
        {
            return _actuator;
        }

        public void actuator(bool value)
        {
            _actuator = value;
        }

        public bool inActive()
        {
            return _inActive;
        }

        public void inActive(bool value)
        {
            _inActive = value;
        }
    }

    public class Player
    {
        public bool releaseUseTile;
        public bool tileInteractAttempted;
        public int chest = -1;
        public int chestX;
        public int chestY;
        public int altFunctionUse;
        public bool[] adjTile = new bool[1024];
        public Item[] inventory = new Item[58];

        public bool IsInInteractionRangeToMultiTileHitbox(int x, int y)
        {
            return true;
        }
    }

    public class Chest
    {
        public Item[] item = new Item[40];
        public int maxItems = 40;
        public string name;

        public void Resize(int size)
        {
            Array.Resize(ref item, size);
            maxItems = size;
        }

        public static int FindChest(int x, int y)
        {
            return -1;
        }

        public static int CreateChest(int x, int y, int id)
        {
            return 0;
        }

        public static void RemoveChest(int chestIndex)
        {
        }
    }

    public class Item
    {
        public int type;
        public int stack;
        public byte prefix;
        public bool favorited;

        public bool IsAir => type <= 0 || stack <= 0;

        public void SetDefaults(int itemType)
        {
            type = itemType;
            if (stack <= 0)
                stack = 1;
        }

        public void Prefix(int prefixValue)
        {
            prefix = (byte)prefixValue;
        }

        public static int NewItem(object source, int x, int y, int width, int height, int type, int stack, bool noBroadcast, int prefixGiven, bool noGrabDelay)
        {
            return 0;
        }
    }

    public static class WorldGen
    {
        public static bool destroyObject;

        public static void RangeFrame(int startX, int startY, int endX, int endY)
        {
        }

        public static object GetItemSource_FromTileBreak(int x, int y)
        {
            return null;
        }

        public static void KillTile(int i, int j, bool fail = false, bool effectOnly = false, bool noItem = false)
        {
        }
    }

    public static class Lang
    {
        public static object[] _tileNameCache = new object[1024];
        public static object[] _mapLegendCache = new object[1024];
    }
}

namespace Terraria.ID
{
    public static class TileID
    {
        public static int Count = 692;
        public static ushort Dirt = 0;
        public static ushort Stone = 1;
        public static ushort GrayBrick = 38;

        public static class Sets
        {
            public static TileSetFactory Factory = new TileSetFactory(1024);
            public static bool[] DisableSmartCursor = new bool[1024];
            public static bool[] BasicChest = new bool[1024];
            public static bool[] BlockMergesWithMergeAllBlock = new bool[1024];
            public static bool[] ForcedDirtMerging = new bool[1024];
            public static bool[] MergesWithClouds = new bool[1024];
            public static bool[] ChecksForMerge = new bool[1024];
        }
    }

    public sealed class TileSetFactory
    {
        internal int _size;
        internal readonly Dictionary<int, bool[]> _boolBufferCache = new Dictionary<int, bool[]>();
        internal readonly Dictionary<int, int[]> _intBufferCache = new Dictionary<int, int[]>();
        internal readonly Dictionary<int, ushort[]> _ushortBufferCache = new Dictionary<int, ushort[]>();
        internal readonly Dictionary<int, float[]> _floatBufferCache = new Dictionary<int, float[]>();

        public TileSetFactory(int size)
        {
            _size = size;
        }
    }
}

namespace Terraria.DataStructures
{
    public struct AnchorData
    {
        public AnchorData(Terraria.Enums.AnchorType type, int tileCount, int checkStart)
        {
            typeValue = type;
            tileCountValue = tileCount;
            checkStartValue = checkStart;
        }

        public Terraria.Enums.AnchorType typeValue;
        public int tileCountValue;
        public int checkStartValue;
    }

    public struct Point16
    {
        public short X;
        public short Y;

        public Point16(short x, short y)
        {
            X = x;
            Y = y;
        }

        public Point16(int x, int y)
        {
            X = (short)x;
            Y = (short)y;
        }
    }
}

namespace Terraria.Enums
{
    [Flags]
    public enum AnchorType
    {
        SolidTile = 1,
        SolidWithTop = 2,
        Table = 4
    }
}

namespace Terraria.Localization
{
    public class LocalizedText
    {
        internal LocalizedText(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public string Value { get; }
    }
}

namespace Terraria.GameContent
{
    public static class TextureAssets
    {
        public static object[] Tile = new object[1024];
    }
}

namespace Terraria.IO
{
    public static class WorldFile
    {
        public static void SaveWorld()
        {
        }

        public static void SaveWorld(bool useCloudSaving, bool resetTime)
        {
        }

        public static void LoadWorld()
        {
        }
    }
}

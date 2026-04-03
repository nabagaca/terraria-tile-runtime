using System;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Runtime-owned tile definition contract.
    /// This starts intentionally small so the runtime boundary can be proven first.
    /// </summary>
    public class TileDefinition
    {
        public string DisplayName { get; set; }
        public string TexturePath { get; set; }

        public bool Solid { get; set; }
        public bool SolidTop { get; set; }
        public bool Brick { get; set; }
        public bool NoAttach { get; set; }
        public bool Table { get; set; }
        public bool Lighted { get; set; }
        public bool LavaDeath { get; set; } = true;
        public bool FrameImportant { get; set; } = true;
        public bool NoFail { get; set; }
        public bool Cut { get; set; }
        public TileMergeCategory[] MergeCategories { get; set; }
        public bool DisableSmartCursor { get; set; }
        public bool SmartInteract { get; set; }
        public bool DisableSmartInteract { get; set; }
        public bool HasOutline { get; set; }
        public bool AutoGenerateOutline { get; set; } = true;
        public string OutlineTexturePath { get; set; }

        // Tile refs can be vanilla names like "Stone" or custom refs like "mod-id:tile-name".
        public string[] MergeWith { get; set; }

        public byte MapColorR { get; set; } = 180;
        public byte MapColorG { get; set; } = 180;
        public byte MapColorB { get; set; } = 180;

        public int DustType { get; set; } = -1;
        public int HitSoundStyle { get; set; } = -1;

        public int Width { get; set; } = 1;
        public int Height { get; set; } = 1;
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int CoordinateWidth { get; set; } = 16;
        public int CoordinatePadding { get; set; } = 2;
        public int[] CoordinateHeights { get; set; }
        public bool StyleHorizontal { get; set; } = true;
        public int StyleWrapLimit { get; set; }
        public int StyleMultiplier { get; set; } = 1;

        public bool IsContainer { get; set; }
        public bool RegisterAsBasicChest { get; set; } = true;
        public bool ContainerInteractable { get; set; } = true;
        public bool ContainerRequiresEmptyToBreak { get; set; } = true;
        public int ContainerCapacity { get; set; } = 40;
        public string ContainerName { get; set; }
        public string DropItemId { get; set; }

        // Animation
        public int AnimationFrameCount { get; set; }        // 0 = static (default)
        public int AnimationTicksPerFrame { get; set; } = 5; // ticks between frame advances
        public bool AnimateFromGif { get; set; }             // true = TexturePath is a .gif
        public bool AnimationTriggered { get; set; }         // true = play one cycle on trigger, not looping

        public Func<object, int, int, bool> OnRightClick { get; set; }
        public Action<int, int> OnPlace { get; set; }
        public Action<int, int> OnBreak { get; set; }

        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
                return "DisplayName is required";

            if (Width <= 0 || Height <= 0)
                return "Width and Height must be positive";

            if (CoordinateWidth <= 0)
                return "CoordinateWidth must be positive";

            if (CoordinatePadding < 0)
                return "CoordinatePadding must be non-negative";

            if (StyleMultiplier <= 0)
                return "StyleMultiplier must be positive";

            if (ContainerCapacity < 1)
                return "ContainerCapacity must be >= 1";

            if (AnimationFrameCount < 0)
                return "AnimationFrameCount must be >= 0";

            if (AnimationFrameCount > 0 && AnimationTicksPerFrame <= 0)
                return "AnimationTicksPerFrame must be > 0 when AnimationFrameCount > 0";

            if (MergeWith != null)
            {
                for (int i = 0; i < MergeWith.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(MergeWith[i]))
                        return "MergeWith entries must be non-empty";
                }
            }

            if (MergeCategories != null)
            {
                for (int i = 0; i < MergeCategories.Length; i++)
                {
                    if (!Enum.IsDefined(typeof(TileMergeCategory), MergeCategories[i]))
                        return "MergeCategories contains an invalid value";
                }
            }

            return null;
        }
    }
}

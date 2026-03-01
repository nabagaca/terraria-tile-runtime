using System;
using System.Collections.Generic;

namespace TerrariaModder.Core
{
    using TerrariaModder.Core.Config;
    using TerrariaModder.Core.Logging;

    public interface IMod
    {
        string Id { get; }
        string Name { get; }
        string Version { get; }
        void Initialize(ModContext context);
        void OnWorldLoad();
        void OnWorldUnload();
        void Unload();
    }

    public class ModContext
    {
        public ILogger Logger { get; set; }
        public IModConfig Config { get; set; }
        public string ModFolder { get; set; }
        public ModManifest Manifest { get; set; } = new ModManifest();
    }

    public class ModManifest
    {
        public string Id { get; set; }
    }
}

namespace TerrariaModder.Core.Config
{
    public interface IModConfig
    {
        T Get<T>(string key);
        T Get<T>(string key, T defaultValue);
        void Set<T>(string key, T value);
        void Save();
    }
}

namespace TerrariaModder.Core.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public interface ILogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
        void Error(string message, Exception ex);
        LogLevel MinLevel { get; set; }
        string ModId { get; }
    }
}

namespace TerrariaModder.Core.Events
{
    public static class FrameEvents
    {
        public static event Action OnPostUpdate;

        public static void RaisePostUpdate()
        {
            OnPostUpdate?.Invoke();
        }
    }
}

namespace TerrariaModder.Core.Assets
{
    public static class ItemRegistry
    {
        public static int VanillaItemCount => 5452;

        public static int ResolveItemType(string itemRef)
        {
            return -1;
        }

        public static string GetFullId(int runtimeType)
        {
            return null;
        }

        public static int GetRuntimeType(string fullId)
        {
            return -1;
        }
    }

    public static class ModdataFile
    {
        public sealed class ItemEntry
        {
            public string Location { get; set; }
            public int Slot { get; set; }
            public string ItemId { get; set; }
            public int Stack { get; set; }
            public int Prefix { get; set; }
            public bool Favorited { get; set; }
        }

        public static List<ItemEntry> Read(string path)
        {
            return new List<ItemEntry>();
        }

        public static bool Write(string path, List<ItemEntry> entries)
        {
            return true;
        }

        public static void Delete(string path)
        {
        }
    }
}

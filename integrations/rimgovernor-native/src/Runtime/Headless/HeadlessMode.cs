using System;
using System.Linq;

namespace HeadlessRim
{
    /// <summary>
    /// The one headless predicate. Waylith runs windowless only when launched
    /// with -Headless (no window, no Vulkan device, null audio). The Unity
    /// signals it stubs (Application.isBatchMode, SystemInfo.graphicsDeviceType)
    /// never change, so nothing here may read them. A launch without -Headless
    /// (Case.Graphics) opens a window and keeps the game's own UI and map meshes.
    /// </summary>
    public static class HeadlessMode
    {
        public static readonly bool Active = Environment.GetCommandLineArgs().Contains("-Headless");
    }
}

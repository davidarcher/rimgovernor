#nullable enable
// Only the game identity access seam is substituted. Tests compile the production
// authority state unchanged and supply its injected context and monotonic clock.
namespace Verse
{
    public sealed class Game
    {
        public HomeBridge.BridgeTools.ColonyIdentity Identity = new HomeBridge.BridgeTools.ColonyIdentity();
        public T? GetComponent<T>() where T : class => Identity as T;
    }
    public sealed class Map { public int uniqueID; }
    public static class Current { public static Game? Game; }
    public sealed class TickManager { public int TicksGame; }
    public static class Find { public static Map? CurrentMap; public static TickManager? TickManager; }
}
namespace HomeBridge.BridgeTools
{
    public sealed class ColonyIdentity
    {
        public string ColonyId = "colony";
        public string LoadToken = "load";
    }
}

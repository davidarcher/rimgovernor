// Deliberately small test seams: these do not model native SDK or game behavior.
using System;
using System.Collections.Generic;
namespace RimBridgeServer.Sdk { public interface IRimBridgeContext { } }
namespace Verse {
    public class Map { public int uniqueID; }
    public class Game { public object Identity; public T GetComponent<T>() where T:class { return Identity as T; } }
    public static class Current { public static Game Game; }
    public static class Find { public static Map CurrentMap; public static TickManager TickManager; }
    public class TickManager { public int TicksGame; }
}
namespace HomeBridge.BridgeTools {
    internal static class BridgeCommon {
        internal static IDictionary<string,object> Arguments;
        internal static IDictionary<string,object> RawArguments(RimBridgeServer.Sdk.IRimBridgeContext ctx, out string unavailable) {
            unavailable = null; return Arguments;
        }
    }
    internal sealed class ColonyIdentity { public string ColonyId = ""; public string LoadToken = ""; }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Odyssey facts: the colony map's tile mutators; the defs ride the def
    // mirror. Read from the game objects and absent without Odyssey.
    internal static class NativeOdysseyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);

        // The colony map's world-tile mutators; empty without Odyssey.
        internal static IEnumerable<string> MapMutators(Map map) =>
            !ModsConfig.OdysseyActive || map.TileInfo == null ? Enumerable.Empty<string>() : Names(map.TileInfo.Mutators).ToList();
    }
}

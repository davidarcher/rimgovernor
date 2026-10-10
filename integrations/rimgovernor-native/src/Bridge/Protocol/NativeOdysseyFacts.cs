#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Odyssey facts: the building row block (hack progress, portal state) and
    // the colony map's tile mutators; the defs ride the def mirror and the stockpile
    // type enum rides GameConstants. Read from the game objects and absent
    // without Odyssey.
    internal static class NativeOdysseyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static IEnumerable<string> Names(IEnumerable<Def>? defs) =>
            (defs ?? Enumerable.Empty<Def>()).Select(d => Id(d?.defName)).Where(n => n != null).Select(n => n!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);

        // The colony map's world-tile mutators; empty without Odyssey.
        internal static IEnumerable<string> MapMutators(Map map) =>
            !ModsConfig.OdysseyActive || map.TileInfo == null ? Enumerable.Empty<string>() : Names(map.TileInfo.Mutators).ToList();

        private static Obs.ReadIssue Failed(string field, Exception ex) => new Obs.ReadIssue { Field = field,
            Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };

        // The building row block; null without Odyssey or when the building
        // is neither hackable nor a portal. A sub-read that throws leaves its
        // block absent and adds a ReadIssue.
        internal static Obs.OdysseyBuilding? Building(Thing thing)
        {
            if (!ModsConfig.OdysseyActive) return null;
            var row = new Obs.OdysseyBuilding();
            try
            {
                if (thing.TryGetComp<CompHackable>() is CompHackable hack)
                {
                    var state = new Obs.HackableState { Hacked = hack.IsHacked, LockedOut = hack.LockedOut, Autohack = hack.Autohack };
                    if (Finite(hack.ProgressPercent)) state.ProgressPercent = hack.ProgressPercent;
                    if (Finite(hack.defence)) state.Defence = hack.defence;
                    row.Hackable = state;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("hackable", ex)); }
            try
            {
                if (thing is MapPortal portal)
                {
                    var state = new Obs.PortalState { PocketMapExists = portal.PocketMapExists };
                    if (portal.PocketMapExists) state.PocketMapId = portal.PocketMap.uniqueID;
                    if (portal is AncientHatch hatch)
                    {
                        if (Id(hatch.stockpileType.ToString()) is string type) state.StockpileType = type;
                        if (Id(hatch.layout?.defName) is string layout) state.Layout = layout;
                    }
                    row.Portal = state;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("portal", ex)); }
            return row.Hackable == null && row.Portal == null && row.Issues.Count == 0 ? null : row;
        }
    }
}

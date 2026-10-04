#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // The primary ideoligion's current state for the snapshot frame's
    // "ideology" section (#1654); absent without Ideology. The static defs
    // (memes, precepts, ritual patterns and behaviors) are rows of the
    // definition catalog's generated def mirror.
    internal static class NativeIdeologyObservation
    {
        private static IEnumerable<string> Names<T>(IEnumerable<T>? defs) where T : Def =>
            (defs ?? Enumerable.Empty<T>()).Where(d => d != null).Select(d => Id(d.defName));

        private static IEnumerable<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);

        // The player faction's primary ideoligion as it stands; null without
        // Ideology or a primary ideoligion.
        internal static Obs.IdeologySnapshot? Build(Common.ObservationContext context)
        {
            if (!ModsConfig.IdeologyActive) return null;
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
            if (ideo == null) return null;
            var row = new Obs.IdeologySnapshot { Context = context, IdeoId = Id(ideo.GetUniqueLoadID()), ObligationsActive = ideo.ObligationsActive,
                Believers = ideo.ColonistBelieverCountCached, MinBelieversForObligations = Ideo.MinBelieversToEnableObligations };
            row.Memes.Add(Sorted(Names(ideo.memes)));
            foreach (var precept in ideo.PreceptsListForReading.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)) {
                var id = Id(precept.GetUniqueLoadID());
                var def = Id(precept.def.defName);
                switch (precept) {
                    case Precept_Role role:
                        var held = new Obs.IdeoRole { Id = id, DefName = def, Active = role.Active };
                        held.Pawns.Add(role.ChosenPawns().OrderBy(p => p.thingIDNumber).Select(Ref));
                        row.Roles.Add(held);
                        break;
                    case Precept_Ritual ritual:
                        row.Rituals.Add(new Obs.IdeoRitual { Id = id, DefName = def, Pattern = ritual.sourcePattern == null ? null : Id(ritual.sourcePattern.defName),
                            LastFinishedTick = ritual.lastFinishedTick, ActiveObligations = ritual.activeObligations?.Count ?? 0, RepeatPenaltyActive = ritual.RepeatPenaltyActive, Running = NativeRitualBegin.Running(ritual) });
                        break;
                    case Precept_Building building:
                        row.Buildings.Add(new Obs.IdeoBuilding { Id = id, DefName = def, Building = building.ThingDef == null ? null : Id(building.ThingDef.defName) });
                        break;
                    default:
                        row.Precepts.Add(new Obs.IdeoPrecept { Id = id, DefName = def });
                        break;
                }
            }
            return row;
        }
    }
}

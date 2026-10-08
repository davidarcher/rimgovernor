#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestThreats
    {
        private static readonly FieldInfo? IncidentParmsField = typeof(QuestPart_Incident)
            .GetField("incidentParms", BindingFlags.Instance | BindingFlags.NonPublic);

        // Generated incident budgets or actual scripted raider combat power.
        internal static double? Read(Quest quest)
        {
            var found = false;
            double points = 0;
            var arrived = new HashSet<Pawn>();
            foreach (var part in quest.PartsListForReading)
            {
                if(part is QuestPart_PawnsArrive arrival) {
                    foreach(var pawn in arrival.pawns) {
                        if(pawn==null || !pawn.HostileTo(Faction.OfPlayer) || !arrived.Add(pawn)) continue;
                        var power=pawn.kindDef?.combatPower;
                        if(!power.HasValue || float.IsNaN(power.Value) || float.IsInfinity(power.Value) || power.Value<0) return null;
                        found=true; points+=power.Value;
                    }
                }
                if (!(part is QuestPart_Incident incident)) continue;
                if (incident.incident?.category != IncidentCategoryDefOf.ThreatBig
                    && incident.incident?.category != IncidentCategoryDefOf.ThreatSmall) continue;
                if (!(IncidentParmsField?.GetValue(incident) is IncidentParms parms)
                    || float.IsNaN(parms.points) || float.IsInfinity(parms.points) || parms.points < 0) return null;
                found = true;
                points += parms.points;
            }
            return found ? points : (double?)null;
        }
    }
}

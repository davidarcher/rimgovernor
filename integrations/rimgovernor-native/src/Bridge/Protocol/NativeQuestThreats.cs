using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    internal static class NativeQuestThreats
    {
        private static readonly FieldInfo? IncidentParmsField = typeof(QuestPart_Incident)
            .GetField("incidentParms", BindingFlags.Instance | BindingFlags.NonPublic);

        // The quest's generated threat-incident budgets plus the actual combat power of its scripted hostile arrivals;
        // 0 for a quest with neither (found is then false). False when a budget or a combat power cannot be read.
        internal static bool TryRead(Quest quest, out double points) => TryRead(quest, out points, out _);

        internal static bool TryRead(Quest quest, out double points, out bool found)
        {
            points = 0;
            found = false;
            var arrived = new HashSet<Pawn>();
            foreach (var part in quest.PartsListForReading)
            {
                if (part is QuestPart_PawnsArrive arrival)
                {
                    foreach (var pawn in arrival.pawns)
                    {
                        if (pawn == null || !pawn.HostileTo(Faction.OfPlayer) || !arrived.Add(pawn)) continue;
                        if (pawn.kindDef == null) return false;
                        var power = pawn.kindDef.combatPower;
                        if (float.IsNaN(power) || float.IsInfinity(power) || power < 0) return false;
                        found = true;
                        points += power;
                    }
                }
                if (!(part is QuestPart_Incident incident)) continue;
                if (incident.incident?.category != IncidentCategoryDefOf.ThreatBig
                    && incident.incident?.category != IncidentCategoryDefOf.ThreatSmall) continue;
                if (!(IncidentParmsField?.GetValue(incident) is IncidentParms parms)
                    || float.IsNaN(parms.points) || float.IsInfinity(parms.points) || parms.points < 0) return false;
                found = true;
                points += parms.points;
            }
            return true;
        }
    }
}

#nullable enable
using System;
using System.Reflection;
using RimWorld;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestThreats
    {
        private static readonly FieldInfo? IncidentParmsField = typeof(QuestPart_Incident)
            .GetField("incidentParms", BindingFlags.Instance | BindingFlags.NonPublic);

        // These are the generated quest's incident budgets, not storyteller guesses.
        internal static double? Read(Quest quest)
        {
            var found = false;
            double points = 0;
            foreach (var part in quest.PartsListForReading)
            {
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

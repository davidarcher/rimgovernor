using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal static class ZoneSettingsContract
    {
        internal static object Read(ThingFilter filter)
        {
            if (filter == null) return null;
            try
            {
                return new Dictionary<string, object>
                {
                    { "version", 1 },
                    { "allowedDefs", filter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToArray() },
                    { "disallowedSpecial", DefDatabase<SpecialThingFilterDef>.AllDefs.Where(d => !filter.Allows(d)).Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToArray() },
                    { "hitPoints", new[] { filter.AllowedHitPointsPercents.min, filter.AllowedHitPointsPercents.max } },
                    { "quality", new[] { (int)filter.AllowedQualityLevels.min, (int)filter.AllowedQualityLevels.max } },
                    { "mentalBreakChance", new[] { filter.AllowedMentalBreakChance.min, filter.AllowedMentalBreakChance.max } }
                };
            }
            catch { return null; }
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Current-condition inputs only. No work orders or simulated future yields.
    internal static class ForecastFacts
    {
        internal static Snapshot Read(Map map, List<Pawn> people, List<Thing> things)
        {
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                && p.Faction == Faction.OfPlayerSilentFail && p.needs?.food != null).ToList();
            var food = things.Where(FoodSupplyFacts.SharedFood).ToList();
            var patients = people.Select(p => new Patient {
                id = p.GetUniqueLoadID(),
                mood = ReadNumber(() => p.needs.mood?.CurLevelPercentage),
                moodTarget = ReadNumber(() => p.needs.mood?.CurInstantLevel)
            }).ToList();
            return new Snapshot { readable = true, tick = Find.TickManager.TicksGame,
                combinedFoodSupply = FoodSupplyFacts.Read(people.Concat(animals).ToList(), food),
                patients = patients };
        }

        internal sealed class Snapshot {
            public bool readable { get; set; }
            public int tick { get; set; }
            public FoodSupplyFacts.Snapshot combinedFoodSupply { get; set; } = new FoodSupplyFacts.Snapshot();
            public List<Patient> patients { get; set; } = new List<Patient>();
        }
        internal sealed class Patient {
            public string? id { get; set; }
            public float? mood { get; set; }
            public float? moodTarget { get; set; }
        }
        private static float? ReadNumber(Func<float?> read)
        {
            try { var value = read(); return value.HasValue && !float.IsNaN(value.Value)
                && !float.IsInfinity(value.Value) ? value : null; }
            catch { return null; }
        }
    }
}

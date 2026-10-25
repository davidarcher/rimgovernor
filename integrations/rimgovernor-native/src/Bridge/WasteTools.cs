
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    public sealed class HomeWasteTools
    {
        private static string Id(Thing thing) => thing.GetUniqueLoadID();

        // CorpseOf is a corpse's inner pawn class: colonist for the
        // player faction's humanlike, stranger for any other humanlike,
        // animal otherwise.
        private static RimGovernor.Brain.GameFacts.Common.CorpseClass CorpseOf(Pawn inner)
        {
            if (!inner.RaceProps.Humanlike) return RimGovernor.Brain.GameFacts.Common.CorpseClass.Animal;
            return inner.Faction == Faction.OfPlayer ? RimGovernor.Brain.GameFacts.Common.CorpseClass.Colonist : RimGovernor.Brain.GameFacts.Common.CorpseClass.Stranger;
        }

        // Project is the corpse census on the typed wire
        // (ColonyFactsSnapshot.waste): every visible spawned corpse in load
        // ID order, then every buried corpse with its grave.
        internal static RimGovernor.Brain.GameFacts.Observations.WasteSnapshot Project(Map map)
        {
            var items = new List<RimGovernor.Brain.GameFacts.Observations.WasteItem>();
            var spawned = new List<Corpse>();
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (thing is Corpse corpse && !corpse.Position.Fogged(map)) spawned.Add(corpse);
            foreach (var corpse in spawned.OrderBy(Id))
                items.Add(new RimGovernor.Brain.GameFacts.Observations.WasteItem { Thing = NativeRef.Thing(corpse), CorpseClass = CorpseOf(corpse.InnerPawn ?? throw new InvalidOperationException("Corpse pawn unavailable.")), Grave = null });
            foreach (var grave in map.listerThings.AllThings.OfType<Building_Grave>().Where(g => !g.Position.Fogged(map)))
                foreach (var body in grave.GetDirectlyHeldThings().OfType<Corpse>())
                    items.Add(new RimGovernor.Brain.GameFacts.Observations.WasteItem { Thing = NativeRef.Thing(body), CorpseClass = CorpseOf(body.InnerPawn ?? throw new InvalidOperationException("Buried corpse pawn unavailable.")), Grave = NativeRef.Of(Id(grave)) });
            return new RimGovernor.Brain.GameFacts.Observations.WasteSnapshot { Items = [.. items] };
        }
    }
}

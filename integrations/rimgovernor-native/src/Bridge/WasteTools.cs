#nullable enable

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class HomeWasteTools
    {
        private static string Id(Thing thing) => thing.GetUniqueLoadID();

        // CorpseOf is a corpse's inner pawn class: colonist for the
        // player faction's humanlike, stranger for any other humanlike,
        // animal otherwise.
        private static Common.CorpseClass CorpseOf(Pawn inner)
        {
            if (!inner.RaceProps.Humanlike) return Common.CorpseClass.Animal;
            return inner.Faction == Faction.OfPlayer ? Common.CorpseClass.Colonist : Common.CorpseClass.Stranger;
        }

        // Project is the corpse census on the typed wire
        // (ColonyFactsSnapshot.waste): every visible spawned corpse in load
        // ID order, then every buried corpse with its grave.
        internal static Obs.WasteReply Project(Map map, Common.ObservationContext context)
        {
            var items = new List<Obs.WasteItem>();
            var spawned = new List<Corpse>();
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (thing is Corpse corpse && corpse.InnerPawn != null && !corpse.Position.Fogged(map)) spawned.Add(corpse);
            foreach (var corpse in spawned.OrderBy(Id))
                items.Add(new Obs.WasteItem { Thing = NativeRef.Thing(corpse), CorpseClass = CorpseOf(corpse.InnerPawn) });
            foreach (var grave in map.listerThings.AllThings.OfType<Building_Grave>().Where(g => !g.Position.Fogged(map)))
                foreach (var body in grave.GetDirectlyHeldThings().OfType<Corpse>())
                    if (body.InnerPawn != null)
                        items.Add(new Obs.WasteItem { Thing = NativeRef.Thing(body), CorpseClass = CorpseOf(body.InnerPawn), Grave = NativeRef.Of(Id(grave)) });
            return new Obs.WasteReply { Outcome = new Obs.WasteSnapshot { Items = [.. items] } };
        }
    }
}

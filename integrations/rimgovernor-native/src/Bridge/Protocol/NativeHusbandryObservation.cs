#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // The dedicated ReadHusbandry observation: the herd's settings/census
    // tokens and training, tame, release and safe-to-slaughter eligibility
    // facts, from the same NativeHusbandryOperations rules HusbandryIntent
    // applies with.
    // Single complete page, like ReadStatus/ReadPopulation/ReadPrisonerInteraction;
    // a paginated herd is deferred to whatever candidate search eventually
    // drives a routine herd planner.
    internal static class NativeHusbandryObservation
    {
        internal static Obs.HusbandryReply HandleReadHusbandry(Obs.HusbandryRequest request)
        {
            if (!ValidateHusbandry(request, out var failure0)) return new Obs.HusbandryReply { Outcome = new Obs.HusbandryReplyOutcome.FailureCase(failure0) };
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.HusbandryReply { Outcome = new Obs.HusbandryReplyOutcome.FailureCase(failure) };
            try { return new Obs.HusbandryReply { Outcome = Husbandry(map, request, context) }; }
            catch (Exception) { return new Obs.HusbandryReply { Outcome = Unavailable(Common.UnavailableReason.ReadFailed, "Native husbandry facts could not be read completely.") }; }
        }

        internal static bool ValidateHusbandry(Obs.HusbandryRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope is required.");
            return request?.Scope?.ExpectedIdentity != null;
        }

        private static Obs.HusbandrySnapshot Husbandry(Map map, Obs.HusbandryRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == player).OrderBy(p => p.thingIDNumber).ToList();
            // include_wild adds the factionless animals a tame order can
            // target, so a tame target reads through the
            // same read the player herd uses.
            if (request.IncludeWild.GetValueOrDefault())
                animals.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == null && !p.Dead).OrderBy(p => p.thingIDNumber));
            var snapshot = new Obs.HusbandrySnapshot { Context = context };
            foreach (var a in animals)
            {
                var row = new Obs.HusbandryAnimal { Pawn = NativePawnObservationTools.Ref(a), Animal = NativePawnDetails.Animal(a) };
                snapshot = snapshot with { Animals = [.. snapshot.Animals, row] };
            }
            return snapshot;
        }

        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}

#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The dedicated ReadHusbandry observation: the herd's settings/census
    // tokens and training, tame, release and safe-to-slaughter eligibility
    // facts, from the same NativeHusbandryOperations rules HusbandryIntent
    // applies with.
    // Single complete page, like ReadStatus/ReadPopulation/ReadPrisonerInteraction;
    // a paginated herd is deferred to whatever candidate search eventually
    // drives a routine herd planner.
    public sealed class NativeHusbandryObservation
    {
        [Tool("rimgovernor/observations_read_husbandry", Title = "Read colony husbandry census",
            Description = "Official HusbandryRequest ProtoJSON. Read current map player animals (plus factionless animals with include_wild): exact settings/census CAS tokens, training, tame, release and safe-to-slaughter eligibility facts. Single complete page 1..256; unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations HusbandryReply ProtoJSON.", Always = true)]
        public async Task<object> ReadHusbandry(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a HusbandryRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_husbandry", request!, Obs.HusbandryRequest.Parser, out var parsed, out var failure)
                || !ValidateHusbandry(parsed, out failure)) return ProtoBoundary.Encode(new Obs.HusbandryReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.HusbandryReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.HusbandryReply { Observed = Husbandry(map, parsed, context) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.HusbandryReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native husbandry facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
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
            if (request.IncludeWild)
                animals.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == null && !p.Dead).OrderBy(p => p.thingIDNumber));
            var snapshot = new Obs.HusbandrySnapshot { Context = context };
            foreach (var a in animals)
            {
                var pawnRow = NativeObservationTools.PawnRow(a, false, context);
                pawnRow.Pawn.Label = NativePawnObservationTools.Text(a.LabelCap);
                var row = new Obs.HusbandryAnimal { Pawn = pawnRow, Animal = AnimalRow(a) };
                snapshot.Animals.Add(row);
            }
            return snapshot;
        }

        private static Obs.AnimalState AnimalRow(Pawn animal)
        {
            // Gender, age and the other herd facts (#875) come only from the
            // colony upkeep census (NativeHusbandryOperations.HerdFacts, #885).
            var row = new Obs.AnimalState { BodySize = Number(animal.RaceProps.baseBodySize) };
            if (animal.training != null)
            {
                foreach (var def in DefDatabase<TrainableDef>.AllDefsListForReading)
                {
                    var report = animal.training.CanAssignToTrain(def, out var visible);
                    var entry = new Obs.TrainingEntry { DefName = def.defName, Learned = animal.training.HasLearned(def), Wanted = animal.training.GetWanted(def), Available = report.Accepted && visible };
                    row.Training.Add(entry);
                }
            }
            if (animal.MapHeld?.designationManager != null)
            {
                var designations = animal.MapHeld.designationManager.AllDesignationsOn(animal);
                row.Slaughter = designations.Any(d => d.def == DesignationDefOf.Slaughter);
                row.Release = designations.Any(d => d.def == DesignationDefOf.ReleaseAnimalToWild);
                row.Tame = designations.Any(d => d.def == DesignationDefOf.Tame);
            }
            row.SafeToSlaughter = NativeHusbandryOperations.Eligible(animal) && NativeHusbandryOperations.SafeToSlaughter(animal);
            row.SafeToRelease = NativeHusbandryOperations.Eligible(animal) && NativeHusbandryOperations.SafeToRelease(animal);
            row.Tameable = NativeHusbandryOperations.Tameable(animal);
            if (NativeHusbandryOperations.Eligible(animal))
            {
                row.AllowedAreaId = NativeHusbandryOperations.AreaId(animal);
                row.MasterId = NativeHusbandryOperations.MasterId(animal);
                row.FollowDrafted = animal.playerSettings?.followDrafted ?? false;
                row.FollowFieldwork = animal.playerSettings?.followFieldwork ?? false;
                row.Obedient = NativeHusbandryOperations.Obedient(animal);
                row.SupportsAllowedAreas = NativeHusbandryOperations.SupportsAllowedAreas(animal);
            }
            var pregnancy = animal.health?.hediffSet?.hediffs.OfType<Hediff_Pregnant>().FirstOrDefault();
            if (pregnancy != null) { row.Pregnant = true; row.Gestation = Number(pregnancy.Severity); }
            var milk = animal.GetComp<CompMilkable>(); if (milk != null) row.MilkFullness = Number(milk.Fullness);
            var wool = animal.GetComp<CompShearable>(); if (wool != null) row.WoolFullness = Number(wool.Fullness);
            return row;
        }

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
    }
}

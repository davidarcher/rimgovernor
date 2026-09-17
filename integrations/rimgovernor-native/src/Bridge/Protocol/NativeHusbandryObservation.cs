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
    // The dedicated ReadHusbandry observation NativeHusbandryOperations'
    // SetAnimalTraining/SlaughterAnimal writes need immediately before preview:
    // bridge.ReadHusbandryTarget's own read, refreshing the exact settings/
    // census CAS tokens and training/safe-to-slaughter eligibility facts a
    // selected method is re-validated against. Reuses
    // NativeHusbandryOperations.Settings/Census/SafeToSlaughter/Eligible so the
    // tokens observed here are byte-identical to the ones execute checks.
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
                try { return Encode(new Obs.HusbandryReply { Observed = Husbandry(map, parsed, context) }); }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.HusbandryReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.HusbandryReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native husbandry facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static bool ValidateHusbandry(Obs.HusbandryRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and page 1..256 without cursor are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            var page = request.Page;
            return page == null || (!page.HasLimit || page.Limit >= 1 && page.Limit <= 256) && (!page.HasCursor || page.Cursor.Length == 0);
        }

        private static Obs.HusbandrySnapshot Husbandry(Map map, Obs.HusbandryRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var animals = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == player).OrderBy(p => p.thingIDNumber).ToList();
            // include_wild adds the factionless animals a TameAnimal write can
            // target, so a tame target refreshes its CAS tokens through the
            // same read the player herd uses.
            if (request.IncludeWild)
                animals.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Animal && p.Faction == null && !p.Dead).OrderBy(p => p.thingIDNumber));
            var limit = request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
            if (animals.Count > limit) throw new ReadLimit("Complete husbandry census exceeds the requested bound; paging is unavailable.");
            var snapshot = new Obs.HusbandrySnapshot { Context = context };
            foreach (var a in animals)
            {
                var pawnRow = NativeObservationTools.PawnRow(a, false, context);
                pawnRow.Pawn.Label = NativePawnObservationTools.Text(a.LabelCap);
                var row = new Obs.HusbandryAnimal { Pawn = pawnRow, Animal = AnimalRow(a) };
                row.SettingsSnapshot = new Obs.SnapshotRef { Context = context, EntityId = pawnRow.Pawn.Id, Token = NativeHusbandryOperations.Settings(a) };
                row.CensusSnapshot = new Obs.SnapshotRef { Context = context, EntityId = pawnRow.Pawn.Id, Token = NativeHusbandryOperations.Census(a) };
                snapshot.Animals.Add(row);
            }
            snapshot.Completeness = Complete(snapshot.Animals.Count);
            return snapshot;
        }

        private static Obs.AnimalState AnimalRow(Pawn animal)
        {
            var row = new Obs.AnimalState { Gender = animal.gender.ToString(), BodySize = Number(animal.RaceProps.baseBodySize) };
            if (animal.ageTracker != null) row.AgeYears = Number(animal.ageTracker.AgeBiologicalYearsFloat);
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
            var pregnancy = animal.health?.hediffSet?.hediffs.OfType<Hediff_Pregnant>().FirstOrDefault();
            if (pregnancy != null) { row.Pregnant = true; row.Gestation = Number(pregnancy.Severity); }
            var milk = animal.GetComp<CompMilkable>(); if (milk != null) row.MilkFullness = Number(milk.Fullness);
            var wool = animal.GetComp<CompShearable>(); if (wool != null) row.WoolFullness = Number(wool.Fullness);
            return row;
        }

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(Obs.HusbandryReply reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Husbandry reply exceeds one MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}

#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
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
    // Dedicated per-cycle population census: PopulationTool.cs's legacy
    // home/population read ported onto the official protobuf boundary, the
    // same migration NativeHusbandryOperations/NativePrisonerInteractionOperations
    // already made for the write side. Population-*'s recruit/maintain deficit
    // facts (recruitable, current interaction) live only here -- the generic
    // colony/upkeep census NativeUpkeepFacts populates has no guest section --
    // so this is the one native read Go's routine planner needs to detect and
    // select a prisoner-interaction write. Single complete page, like
    // ReadStatus/ReadHusbandry/ReadPrisonerInteraction; a paginated population
    // is deferred to whatever candidate search eventually needs one.
    public sealed class NativePopulationObservation
    {
        [Tool("rimgovernor/observations_read_population", Title = "Read colony population and custody",
            Description = "Official PopulationRequest ProtoJSON. Read current map humanlike population: admission, guest/prisoner status, recruitment eligibility, current exclusive interaction and owned bed. Single complete page 1..256; unavailable instead of truncation.")]
        [ToolResponse("payload", "string", "Official observations PopulationReply ProtoJSON.", Always = true)]
        public async Task<object> ReadPopulation(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a PopulationRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/observations_read_population", request!, Obs.PopulationRequest.Parser, out var parsed, out var failure)
                || !ValidatePopulation(parsed, out failure)) return ProtoBoundary.Encode(new Obs.PopulationReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.PopulationReply { Failure = failure });
                try { return Encode(new Obs.PopulationReply { Observed = Population(map, parsed, context) }); }
                catch (ReadLimit error) { return ProtoBoundary.Encode(new Obs.PopulationReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, error.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.PopulationReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Native population facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        // The population as a bundle section (issue #180): the same rows the
        // tool answers, or false for any read failure the bundle then omits.
        internal static bool TryRead(Map map, Obs.PopulationRequest request, Common.ObservationContext context, [NotNullWhen(true)] out Obs.PopulationSnapshot? snapshot)
        {
            snapshot = null;
            try { snapshot = Population(map, request, context); return true; }
            catch (Exception) { return false; }
        }

        internal static bool ValidatePopulation(Obs.PopulationRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Valid identity scope and page 1..256 without cursor are required.");
            if (request?.Scope?.ExpectedIdentity == null) return false;
            var page = request.Page;
            return page == null || (!page.HasLimit || page.Limit >= 1 && page.Limit <= 256) && (!page.HasCursor || page.Cursor.Length == 0);
        }

        private static Obs.PopulationSnapshot Population(Map map, Obs.PopulationRequest request, Common.ObservationContext context)
        {
            var player = Faction.OfPlayerSilentFail ?? throw new InvalidOperationException("Player faction missing.");
            var people = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.thingIDNumber).ToList();
            var limit = request.Page?.HasLimit == true ? (int)request.Page.Limit : 256;
            if (people.Count > limit) throw new ReadLimit("Complete population census exceeds the requested bound; paging is unavailable.");
            var snapshot = new Obs.PopulationSnapshot { Context = context };
            foreach (var p in people)
            {
                var row = NativeObservationTools.PawnRow(p, false, context);
                row.Pawn.Label = NativePawnObservationTools.Text(p.LabelCap);
                var person = new Obs.PopulationPerson { Pawn = row, Admitted = p.IsFreeColonist && p.Faction == player, Guest = p.HostFaction == player };
                if (p.guest != null)
                {
                    person.Recruitable = p.guest.Recruitable;
                    person.Resistance = Number(p.guest.Resistance);
                    var interaction = p.guest.ExclusiveInteractionMode?.defName;
                    if (interaction != null) person.Interaction = NativePawnObservationTools.Id(interaction);
                }
                if (p.ownership?.OwnedBed != null) person.OwnedBed = new Obs.BuildingState { Building = NativePawnObservationTools.Entity(p.ownership.OwnedBed) };
                if (p.needs?.food != null) person.NutritionPerDay = Number(p.needs.food.FoodFallPerTickAssumingCategory(HungerCategory.Fed, true) * 60000f);
                // The prisoner-interaction settings token: what SetPrisonerInteraction
                // compares expected_snapshot_token against.
                row.Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = row.Pawn.Id, Token = NativePrisonerInteractionOperations.Settings(p) };
                snapshot.Persons.Add(person);
            }
            // The installed subset of the modes SetPrisonerInteraction accepts.
            foreach (var name in new[] { "AttemptRecruit", "MaintainOnly", "ReduceResistance", "Release", "Enslave", "Convert" })
            {
                var def = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
                if (def != null) snapshot.SupportedInteractions.Add(new Obs.DefinitionRef { DefName = NativePawnObservationTools.Id(def.defName), Label = NativePawnObservationTools.Text(def.label) });
            }
            snapshot.Completeness = Complete(snapshot.Persons.Count);
            return snapshot;
        }

        private static double Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(Obs.PopulationReply reply)
        {
            if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes) throw new ReadLimit("Population reply exceeds one MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) { } }
    }
}

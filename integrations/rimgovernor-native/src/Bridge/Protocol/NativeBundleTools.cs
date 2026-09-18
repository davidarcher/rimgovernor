#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using Verse;
using Clock = RimGovernor.Protocol.Clock;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The scheduler's batched per-step read (issue #127): the observation
    /// scope lifecycle_read_tick reports plus, as requested, the owned clock
    /// status, the emergency status and one clock events page, each the same
    /// read its dedicated tool answers, taken in one main-thread hop so every
    /// section describes one tick. Without a scope the bundle describes the
    /// current map; with one, a changed identity is the StaleIdentity failure
    /// the dedicated reads report. An events section long-polls like
    /// clock_read_events: the first hop registers the waiter, and the hop
    /// after the wake re-reads the whole bundle without waiting.
    ///
    /// A planning step also asks for the routine census's families (issue
    /// #180): the planning colony facts, the population, the research state
    /// and the pawn detail of the emergency section's colonists, each the
    /// exact read the scheduler would otherwise issue after the bundle. They
    /// are best effort: a family that fails to read is omitted, and when the
    /// families push the reply past the envelope they are all omitted, so the
    /// dedicated reads then serve them as before and the bundle itself never
    /// fails on their account.
    /// </summary>
    public sealed class NativeBundleTools
    {
        private const string ToolName = "rimgovernor/observations_read_bundle";

        [Tool(ToolName, Title = "Read scheduler bundle",
            Description = "Official BundleRequest ProtoJSON. One read of the current scope plus, as requested, the owned clock status, the emergency status (colonists and threats), one clock events page (cursor>=0, limit1..128, wait_ms<=5000) and, best effort, the planning colony facts, population, research and colonist pawn detail families, all from one tick. Read-only.")]
        [ToolResponse("payload", "string", "Official observations BundleReply ProtoJSON.", Always = true)]
        public async Task<object> ReadBundle(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a BundleRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.BundleRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.BundleReply { Failure = failure });
            var waitMs = parsed.Events != null && parsed.Events.HasWaitMs ? (int)parsed.Events.WaitMs : 0;
            Task<bool>? wake = null;
            var first = await ProtoBoundary.OnMainThread(ctx, () => Read(parsed, waitMs, out wake), cancellationToken).ConfigureAwait(false);
            if (wake == null) return first;
            await Supervisor.AwaitWake(wake, waitMs, cancellationToken).ConfigureAwait(false);
            return await ProtoBoundary.OnMainThread(ctx, () => Read(parsed, 0, out _), cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Obs.BundleRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bundle events require explicit cursor>=0, limit1..128 and wait_ms<=" + NativeClockTools.MaxWaitMs + ".");
            if (request.Scope != null && !ProtoBoundary.Complete(request.Scope.ExpectedIdentity))
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A bundle scope must carry a complete valid colony, load and map identity.");
                return false;
            }
            if (request.HasColonistPawns && request.ColonistPawns && !(request.HasEmergency && request.Emergency))
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Bundle colonist pawns require the emergency section.");
                return false;
            }
            var events = request.Events;
            return events == null || events.HasAfterCursor && events.AfterCursor >= 0 && events.HasLimit && events.Limit >= 1 && events.Limit <= 128
                && (!events.HasWaitMs || events.WaitMs <= NativeClockTools.MaxWaitMs);
        }

        // On the main thread. Every section shares the context read here.
        private static object Read(Obs.BundleRequest request, int waitMs, out Task<bool>? wake)
        {
            wake = null;
            Map? map;
            Common.ObservationContext? context;
            if (request.Scope != null)
            {
                if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out map, out context, out var failure))
                    return ProtoBoundary.Encode(new Obs.BundleReply { Failure = failure });
            }
            else
            {
                map = Find.CurrentMap;
                if (!ProtoBoundary.TryReadContext(map, out context, out var unavailable))
                    return ProtoBoundary.Encode(new Obs.BundleReply { Unavailable = unavailable });
                if (map == null)
                    return ProtoBoundary.Encode(new Obs.BundleReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." } });
            }
            var observed = new Obs.BundleSnapshot { Context = context, Paused = Find.TickManager.Paused };
            if (request.HasClockStatus && request.ClockStatus) observed.ClockStatus = NativeClockTools.Read(context);
            if (request.HasEmergency && request.Emergency)
            {
                var status = new Obs.StatusRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                    Colonists = true, Threats = true, ColonistDetail = false, Page = new Common.PageRequest { Limit = 256 } };
                if (!NativeObservationTools.TryStatus(map, status, context, out var emergency, out var unavailable))
                    return ProtoBoundary.Encode(new Obs.BundleReply { Unavailable = unavailable });
                observed.Emergency = emergency;
            }
            if (request.Events != null)
            {
                var events = new Clock.EventsRequest { Identity = context.Identity.Clone(), AfterCursor = request.Events.AfterCursor, Limit = request.Events.Limit };
                if (waitMs > 0) events.WaitMs = (uint)waitMs;
                var page = Supervisor.TypedEvents(events, context, waitMs, out wake);
                if (page.Failure != null) { wake = null; return ProtoBoundary.Encode(new Obs.BundleReply { Failure = page.Failure }); }
                observed.Events = page.Page;
            }
            var families = wake == null && ReadFamilies(map, request, context, observed);
            var reply = new Obs.BundleReply { Observed = observed };
            if (families && Oversized(reply))
            {
                observed.ColonyFacts = null; observed.Population = null; observed.Research = null; observed.ColonistPawns = null;
            }
            if (Oversized(reply))
            {
                wake = null;
                return ProtoBoundary.Encode(new Obs.BundleReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted, "Bundle exceeds the bounded reply envelope; no rows were omitted.") });
            }
            return ProtoBoundary.Encode(reply, compact: true);
        }

        private static bool Oversized(Obs.BundleReply reply) =>
            new System.Text.UTF8Encoding(false, true).GetByteCount(ProtoBoundary.Format(reply, compact: true)) > ProtoBoundary.MaximumEnvelopeBytes;

        // On the main thread. Adds the requested census families to observed,
        // each keyed as its dedicated read, omitting any that fails; reports
        // whether any was added. A waiting events hop carries none: the hop
        // after the wake reads them, so the census describes the woken tick.
        private static bool ReadFamilies(Map map, Obs.BundleRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            var added = false;
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            if (request.HasColonyFacts && request.ColonyFacts
                && NativeColonyObservationTools.TryRead(map, new Obs.ColonyFactsRequest { Scope = Scope(), Planning = true, Page = new Common.PageRequest { Limit = 256 } }, context, out var colony))
            { observed.ColonyFacts = colony; added = true; }
            if (request.HasPopulation && request.Population
                && NativePopulationObservation.TryRead(map, new Obs.PopulationRequest { Scope = Scope() }, context, out var population))
            { observed.Population = population; added = true; }
            if (request.HasResearch && request.Research
                && NativeResearchObservationTools.TryRead(map, new Obs.ResearchRequest { Scope = Scope(), IncludeLocked = true, IncludeFinished = true, Page = new Common.PageRequest { Limit = 256 } }, context, out var research))
            { observed.Research = research; added = true; }
            if (request.HasColonistPawns && request.ColonistPawns && observed.Emergency?.Colonists != null
                && observed.Emergency.Colonists.Completeness?.Page?.Complete == true && observed.Emergency.Colonists.Pawns.Count > 0)
            {
                var pawns = new Obs.ListPawnsRequest {
                    Scope = Scope(),
                    Filter = new Obs.PawnFilter { IncludeDead = true },
                    Details = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = false, Social = false, Animals = true, Work = true, Schedule = true },
                    Page = new Common.PageRequest { Limit = (uint)observed.Emergency.Colonists.Pawns.Count },
                };
                foreach (var row in observed.Emergency.Colonists.Pawns) pawns.Filter.Ids.Add(row.Pawn.Id);
                if (NativePawnObservationTools.TryRead(map, pawns, context, out var detail)) { observed.ColonistPawns = detail; added = true; }
            }
            return added;
        }
    }
}

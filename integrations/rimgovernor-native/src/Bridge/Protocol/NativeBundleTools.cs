#nullable enable
using System;
using System.Collections.Generic;
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
    /// fails on their account. A family's field mask (issue #360:
    /// colonist_pawn_fields, population_fields, research_fields) is handed to
    /// the family's reader, which skips the sub-blocks it does not include at
    /// source (#648, NativeBundleMasks); an absent mask keeps the family whole.
    /// </summary>
    public sealed class NativeBundleTools
    {
        private const string ToolName = "rimgovernor/observations_read_bundle";

        // Every section's main-thread read is timed into the hop's
        // observation account (#642) under the name it carries in
        // BundleSnapshot, with the rows it returned. The account is the
        // production path's own instrumentation, not a diagnostic mode: a
        // hop outside ProtoBoundary's scope records nothing.
        private static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        [Tool(ToolName, Title = "Read scheduler bundle",
            Description = "Official BundleRequest ProtoJSON. One read of the current scope plus, as requested, the owned clock status, the emergency status (colonists and threats), one clock events page (cursor>=0, limit1..128, wait_ms<=5000) and, best effort, the planning colony facts, population, research and colonist pawn detail families, all from one tick. Read-only.")]
        [ToolResponse("payload", "string", "Official observations BundleReply ProtoJSON.", Always = true)]
        public async Task<object> ReadBundle(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a BundleRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Obs.BundleRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.BundleReply { Failure = failure });
            var waitMs = parsed.Events != null && parsed.Events.HasWaitMs ? (int)parsed.Events.WaitMs : 0;
            // An encoder slot is reserved before the capture is admitted, so
            // no captured reply ever waits for an encoder (#644).
            var lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
            if (lease == null) return Saturated();
            try
            {
                var first = await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed, waitMs), cancellationToken).ConfigureAwait(false);
                ReplyEncoder.Lease owned;
                if (first.Value.Wake == null)
                {
                    owned = lease; lease = null;
                    return await ProtoBoundary.EncodeDetached(first, owned, Encode, cancellationToken).ConfigureAwait(false);
                }
                // A held poll's page is discarded, so it holds no encoder
                // while it waits; the hop after the wake reserves again.
                lease.Dispose(); lease = null;
                await Supervisor.AwaitWake(first.Value.Wake, waitMs, cancellationToken).ConfigureAwait(false);
                lease = await ReplyEncoder.Reserve(cancellationToken).ConfigureAwait(false);
                if (lease == null) return Saturated();
                var woken = await ProtoBoundary.CaptureOnMainThread(ctx, () => Read(parsed, 0), cancellationToken).ConfigureAwait(false);
                owned = lease; lease = null;
                return await ProtoBoundary.EncodeDetached(woken, owned, Encode, cancellationToken).ConfigureAwait(false);
            }
            finally { lease?.Dispose(); }
        }

        private static object Saturated() => ProtoBoundary.Encode(new Obs.BundleReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
            "Bundle reply encoders stayed saturated for " + ReplyEncoder.ReserveTimeoutMs + " ms; nothing was read.") });

        // What one game-thread hop captured: the detached reply, which step
        // and census groups it may drop for the envelope, and a held poll's
        // wake. The reply owns everything it references: every section is
        // built for this hop from the game (events are parsed from the
        // journal's stored text, the clock status clones its epoch), so no
        // live game object, engine collection or shared cache crosses to the
        // encoder.
        private sealed class Capture
        {
            internal readonly Obs.BundleReply Reply;
            internal readonly bool Step, Families;
            internal readonly Task<bool>? Wake;
            internal Capture(Obs.BundleReply reply, bool step = false, bool families = false, Task<bool>? wake = null)
            { Reply = reply; Step = step; Families = families; Wake = wake; }
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
        // Only reads: formatting and the envelope bound are Encode's.
        private static Capture Read(Obs.BundleRequest request, int waitMs)
        {
            Task<bool>? wake = null;
            Map? map;
            Common.ObservationContext? context;
            if (request.Scope != null)
            {
                if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out map, out context, out var failure))
                {
                    ObservationWork.Outcome("failure");
                    return new Capture(new Obs.BundleReply { Failure = failure });
                }
            }
            else
            {
                map = Find.CurrentMap;
                if (!ProtoBoundary.TryReadContext(map, out context, out var unavailable))
                {
                    ObservationWork.Outcome("unavailable");
                    return new Capture(new Obs.BundleReply { Unavailable = unavailable });
                }
                if (map == null)
                {
                    ObservationWork.Outcome("unavailable");
                    return new Capture(new Obs.BundleReply { Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." } });
                }
            }
            Supervisor.NoteControllerRead();
            var observed = new Obs.BundleSnapshot { Context = context, Paused = Find.TickManager.Paused };
            if (request.HasClockStatus && request.ClockStatus)
            {
                var began = Now();
                observed.ClockStatus = NativeClockTools.Capture(context);
                ObservationWork.Captured("clockStatus", Now() - began);
            }
            if (request.HasEmergency && request.Emergency)
            {
                var status = new Obs.StatusRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                    Colonists = true, Threats = true, ColonistDetail = false, Page = new Common.PageRequest { Limit = 256 } };
                var began = Now();
                var read = NativeObservationTools.TryStatus(map, status, context, out var emergency, out var unavailable);
                ObservationWork.Captured("emergency", Now() - began, read && emergency!.Colonists != null ? emergency.Colonists.Pawns.Count : 0);
                if (!read)
                {
                    ObservationWork.Outcome("unavailable");
                    return new Capture(new Obs.BundleReply { Unavailable = unavailable });
                }
                observed.Emergency = emergency;
            }
            if (request.Events != null)
            {
                var events = new Clock.EventsRequest { Identity = context.Identity.Clone(), AfterCursor = request.Events.AfterCursor, Limit = request.Events.Limit };
                if (waitMs > 0) events.WaitMs = (uint)waitMs;
                var began = Now();
                var page = Supervisor.TypedEvents(events, context, waitMs, out wake);
                ObservationWork.Captured("events", Now() - began, page.Page != null ? page.Page.Events.Count : 0);
                if (page.Failure != null)
                {
                    ObservationWork.Outcome("failure");
                    return new Capture(new Obs.BundleReply { Failure = page.Failure });
                }
                observed.Events = page.Page;
                // A held poll's page is discarded: the hop after the wake
                // reads the whole bundle, families included.
                if (wake != null) return new Capture(new Obs.BundleReply { Observed = observed }, wake: wake);
            }
            var families = ReadFamilies(map, request, context, observed);
            var step = ReadStepFamilies(map, request, context, observed);
            return new Capture(new Obs.BundleReply { Observed = observed }, step, families);
        }

        // On an encoder worker (#644), once per hop: formats the captured
        // reply once, and only when it outgrows the envelope drops the step
        // families, then the census families, formatting again after each
        // actual removal. Still oversized, the bundle is a capacity failure.
        // A failure or unavailable capture encodes as it always has.
        private static Dictionary<string, object?> Encode(Capture capture)
        {
            var observed = capture.Reply.Observed;
            if (observed == null) return ProtoBoundary.Encode(capture.Reply);
            if (observed.ClockStatus != null) observed.ClockStatus = NativeClockTools.Bounded(observed.ClockStatus, observed.Context);
            NativeColonyObservationTools.Bound(observed.ColonyFacts);
            var drops = new List<Func<int>>(2);
            if (capture.Step) drops.Add(() => DropStepFamilies(observed));
            if (capture.Families) drops.Add(() => DropFamilies(observed));
            var envelope = ProtoBoundary.EncodeBounded(capture.Reply, drops, () => new Obs.BundleReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                "Bundle exceeds the bounded reply envelope; no rows were omitted.") }, out var fits);
            ObservationWork.Outcome(fits ? "ok" : "failure");
            return envelope;
        }

        private static int DropStepFamilies(Obs.BundleSnapshot observed)
        {
            var dropped = (observed.Buildings != null ? 1 : 0) + (observed.BuiltBuildings != null ? 1 : 0) + (observed.Bills != null ? 1 : 0)
                + (observed.Zones != null ? 1 : 0) + (observed.Traders != null ? 1 : 0) + (observed.WorldProgression != null ? 1 : 0)
                + observed.ResourceSources.Count + (observed.PlanningWindow != null ? 1 : 0);
            observed.Buildings = null; observed.BuiltBuildings = null; observed.Bills = null; observed.Zones = null;
            observed.Traders = null; observed.WorldProgression = null; observed.ResourceSources.Clear(); observed.PlanningWindow = null;
            return dropped;
        }

        private static int DropFamilies(Obs.BundleSnapshot observed)
        {
            var dropped = (observed.ColonyFacts != null ? 1 : 0) + (observed.Population != null ? 1 : 0)
                + (observed.Research != null ? 1 : 0) + (observed.ColonistPawns != null ? 1 : 0);
            observed.ColonyFacts = null; observed.Population = null; observed.Research = null; observed.ColonistPawns = null;
            return dropped;
        }

        // On the main thread. Adds the requested census families to observed,
        // each keyed as its dedicated read, omitting any that fails; reports
        // whether any was added. A waiting events hop carries none: the hop
        // after the wake reads them, so the census describes the woken tick.
        private static bool ReadFamilies(Map map, Obs.BundleRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            var added = false;
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            if (request.HasColonyFacts && request.ColonyFacts)
            {
                var began = Now();
                var read = NativeColonyObservationTools.TryRead(map, new Obs.ColonyFactsRequest { Scope = Scope(), Planning = true, Page = new Common.PageRequest { Limit = 256 } }, context, out var colony);
                ObservationWork.Captured("colonyFacts", Now() - began, read ? colony!.Resources.Count : 0);
                if (read) { observed.ColonyFacts = colony; added = true; }
            }
            if (request.HasPopulation && request.Population)
            {
                var began = Now();
                var read = NativePopulationObservation.TryRead(map, new Obs.PopulationRequest { Scope = Scope() }, context, request.PopulationFields, out var population);
                ObservationWork.Captured("population", Now() - began, read ? population!.Persons.Count : 0);
                if (read) { observed.Population = population; added = true; }
            }
            if (request.HasResearch && request.Research)
            {
                var began = Now();
                var read = NativeResearchObservationTools.TryRead(map, new Obs.ResearchRequest { Scope = Scope(), IncludeLocked = true, IncludeFinished = true, Page = new Common.PageRequest { Limit = 256 } }, context, request.ResearchFields, out var research);
                ObservationWork.Captured("research", Now() - began, read ? research!.Projects.Count : 0);
                if (read) { observed.Research = research; added = true; }
            }
            if (request.HasColonistPawns && request.ColonistPawns && observed.Emergency?.Colonists != null
                && observed.Emergency.Colonists.Completeness?.Page?.Complete == true && observed.Emergency.Colonists.Pawns.Count > 0)
            {
                var pawns = new Obs.ListPawnsRequest {
                    Scope = Scope(),
                    Filter = new Obs.PawnFilter { IncludeDead = true },
                    Details = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true },
                    Page = new Common.PageRequest { Limit = (uint)observed.Emergency.Colonists.Pawns.Count },
                };
                foreach (var row in observed.Emergency.Colonists.Pawns) pawns.Filter.Ids.Add(row.Pawn.Id);
                var began = Now();
                var read = NativePawnObservationTools.TryRead(map, pawns, context, request.ColonistPawnFields, out var detail);
                // The colonists asked for are the candidates; the detail rows
                // returned are what the section produced.
                ObservationWork.Captured("colonistPawns", Now() - began, read ? detail!.Pawns.Count : 0, pawns.Filter.Ids.Count);
                if (read) { observed.ColonistPawns = detail; added = true; }
            }
            return added;
        }

        // On the main thread. Adds the requested step families (#593), each
        // the exact read its dedicated tool answers for the request shape the
        // controller's step issues, omitting any that fails or is not
        // observed; reports whether any was added. They drop before the
        // census families when the reply outgrows the envelope.
        private static bool ReadStepFamilies(Map map, Obs.BundleRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            var added = false;
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            if (request.HasBuildings && request.Buildings)
            {
                var began = Now();
                var buildings = NativeBuildingObservationTools.Read(map, new Obs.ListBuildingsRequest { Scope = Scope(), PlayerOnly = true, Category = "artificial", Page = new Common.PageRequest { Limit = 256 } }, context).Observed;
                ObservationWork.Captured("buildings", Now() - began, buildings != null ? buildings.Buildings.Count : 0);
                if (buildings != null) { observed.Buildings = buildings; added = true; }
            }
            if (request.HasBuiltBuildings && request.BuiltBuildings)
            {
                var built = new Obs.ListBuildingsRequest { Scope = Scope(), PlayerOnly = true, Category = "artificial", Page = new Common.PageRequest { Limit = 256 } };
                built.Statuses.Add("built");
                var began = Now();
                var buildings = NativeBuildingObservationTools.Read(map, built, context).Observed;
                ObservationWork.Captured("builtBuildings", Now() - began, buildings != null ? buildings.Buildings.Count : 0);
                if (buildings != null) { observed.BuiltBuildings = buildings; added = true; }
            }
            if (request.HasBills && request.Bills)
            {
                var began = Now();
                var bills = NativeBillsObservationTools.Read(map, new Obs.BillsRequest { Scope = Scope(), Page = new Common.PageRequest { Limit = 256 } }, context).Observed;
                ObservationWork.Captured("bills", Now() - began, bills != null ? bills.Benches.Count : 0);
                if (bills != null) { observed.Bills = bills; added = true; }
            }
            if (request.HasZones && request.Zones)
            {
                var began = Now();
                var zones = NativeZoneObservationTools.Read(map, new Obs.ListZonesRequest { Scope = Scope(), Page = new Common.PageRequest { Limit = 16 } }, context).Observed;
                ObservationWork.Captured("zones", Now() - began, zones != null ? zones.Zones.Count : 0);
                if (zones != null) { observed.Zones = zones; added = true; }
            }
            if (request.HasTraders && request.Traders)
            {
                var began = Now();
                try { observed.Traders = NativeTradeObservation.Traders(map, context); added = true; } catch (System.Exception) { }
                ObservationWork.Captured("traders", Now() - began, observed.Traders != null ? observed.Traders.Traders.Count : 0);
            }
            if (request.HasWorldProgression && request.WorldProgression)
            {
                var began = Now();
                try { observed.WorldProgression = NativeWorldProgressionObservation.Build(context, false); added = true; } catch (System.Exception) { }
                ObservationWork.Captured("worldProgression", Now() - began);
            }
            foreach (var resource in request.ResourceSources)
            {
                if (!ProtoBoundary.IsIdentifier(resource)) continue;
                var began = Now();
                var sources = NativeResourceSourcesTool.Read(map, new Obs.ResourceSourcesRequest { Scope = Scope(), Resource = resource, IncludeDevelopment = false }, context).Observed;
                ObservationWork.Captured("resourceSources", Now() - began, sources != null ? sources.Sources.Count : 0);
                if (sources != null) { observed.ResourceSources.Add(sources); added = true; }
            }
            var window = request.PlanningWindow;
            if (window?.Region?.Minimum != null && window.Region.Maximum != null)
            {
                var width = (long)window.Region.Maximum.X - window.Region.Minimum.X + 1;
                var height = (long)window.Region.Maximum.Z - window.Region.Minimum.Z + 1;
                if (width >= 1 && height >= 1 && width * height <= 65536)
                {
                    var cells = new Obs.GetCellsRequest { Scope = Scope(), Rectangle = window.Region.Clone(), Compact = true,
                        Fields = new Obs.CellFields { Terrain = false, Roof = true, Visibility = true, Traversal = true, Zone = true, Areas = false, Things = false, Designations = false, Room = true, Growth = true },
                        Page = new Common.PageRequest { Limit = (uint)(width * height) } };
                    if (window.HasChangedSinceTick && window.ChangedSinceTick > 0) cells.ChangedSinceTick = window.ChangedSinceTick;
                    if (NativeObservationTools.ValidateCells(cells, out _))
                    {
                        var began = Now();
                        var snapshot = NativeObservationTools.ReadCells(map, cells, context).Observed;
                        // The window's cells are the candidates; a
                        // changed_since_tick read returns only those that moved.
                        var rows = snapshot == null ? 0 : snapshot.Cells.Count + (snapshot.Compact != null ? snapshot.Compact.Rows.Count : 0);
                        ObservationWork.Captured("planningWindow", Now() - began, rows, width * height);
                        if (snapshot != null) { observed.PlanningWindow = snapshot; added = true; }
                    }
                }
            }
            return added;
        }
    }
}

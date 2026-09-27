#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using Verse;
using Clock = RimGovernor.Protocol.Clock;
using Common = RimGovernor.Protocol.Common;
using Mirror = RimGovernor.Protocol.Mirror;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// rimgovernor/mirror_poll (#795): the long poll that feeds the
    /// controller's colony mirror. One main-thread hop reads the clock
    /// journal page after the client's cursor and every asked section, all
    /// at one context, so the page is consistent across them.
    ///
    /// The entity sections (buildings, bills, zones) are the unfiltered list
    /// read their dedicated tool answers, tracked by EntityTracking (the
    /// buildings' and bills' trackers are the #358 ones their list reads
    /// share; zones get the poll's own, since the zone list keeps tick-only
    /// tombstones): a keyframe, or the rows changed strictly after the ask's
    /// (tick, seq) watermark plus the ids removed since. The pawn and colony
    /// facts sections are the read the ask names, answered as their #773
    /// changed-since read answers it (SectionDelta, the tracker shared with
    /// the dedicated tool). An epoch mismatch, an empty ask or an ask older
    /// than the tombstones reach is a keyframe (stale rule C). An ask with
    /// resync also carries, beside its delta, the keyframe at the same mark
    /// for the client's drift backstop.
    ///
    /// When nothing is past its watermark the hop registers the journal's
    /// waiter (or, without a journal ask, just sleeps) and the hop after the
    /// wake or wait_ms reads once more without waiting. Sections have no
    /// hooks: on a waiting poll a section is compared at most once per
    /// MinCompareTicks of running game, so a running colony whose buildings
    /// change every tick does not turn the client's immediate re-poll into
    /// a projection per frame; an immediate poll (wait_ms 0, a review's)
    /// always compares. The hop runs under the mirror admission rank, after
    /// control and observation hops; the wait holds no main-thread slot.
    /// </summary>
    public sealed class NativeMirrorPollTools
    {
        private const string ToolName = "rimgovernor/mirror_poll";
        private const string PawnsTool = "rimgovernor/observations_list_pawns";
        private const string ZonesShape = ToolName + "|zones";
        internal const uint MaxByteBudget = 768 * 1024;
        private const int JournalLimit = 128;
        // A section compared less than this many ticks ago on a running game
        // is not compared again by a waiting poll (a paused game compares
        // every hop).
        internal const int MinCompareTicks = 250;

        // This process's instance token: the epoch's first element, so a
        // client reconnecting to a restarted game gets keyframes.
        private static readonly string ProcessToken = Stopwatch.GetTimestamp().ToString("x", CultureInfo.InvariantCulture)
            + "-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        // The last compare of each section per map, for MinCompareTicks.
        private static readonly Dictionary<string, EntityTracking.Mark> Compared = new Dictionary<string, EntityTracking.Mark>(StringComparer.Ordinal);

        [Tool(ToolName, Title = "Poll the colony mirror",
            Description = "Official MirrorPollRequest ProtoJSON. One page of the colony mirror: per asked section a keyframe or the rows changed after its (tick, seq) watermark with tombstones, plus the clock events page after journal_after_cursor, from one tick. Waits up to wait_ms<=5000 while nothing is past its watermark. Read-only.")]
        [ToolResponse("payload", "string", "Official mirror MirrorPollReply ProtoJSON.", Always = true)]
        public async Task<object> Poll(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a MirrorPollRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request!, Mirror.MirrorPollRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = failure });
            var waitMs = parsed.HasWaitMs ? (int)parsed.WaitMs : 0;
            Task<bool>? wake = null;
            var idle = false;
            var first = await ProtoBoundary.OnMainThread(ctx, () => Serve(parsed, waitMs, out wake, out idle), cancellationToken).ConfigureAwait(false);
            if (!idle) return first;
            if (wake != null) await Supervisor.AwaitWake(wake, waitMs, cancellationToken).ConfigureAwait(false);
            else await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
            return await ProtoBoundary.OnMainThread(ctx, () => Serve(parsed, 0, out _, out _), cancellationToken).ConfigureAwait(false);
        }

        private static bool Validate(Mirror.MirrorPollRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A mirror poll requires a complete identity, distinct known sections, byte_budget 1.." + MaxByteBudget
                + ", wait_ms<=" + NativeClockTools.MaxWaitMs + ", journal_after_cursor>=0, a window of 1.."
                + NativeMirrorCellGrid.MaxCells + " cells on exactly the planning_cells ask, and a valid read scoped to the poll's identity on exactly the pawns and colony_facts asks.");
            if (!ProtoBoundary.Complete(request.Identity)) return false;
            if (!request.HasByteBudget || request.ByteBudget < 1 || request.ByteBudget > MaxByteBudget) return false;
            if (request.HasWaitMs && request.WaitMs > NativeClockTools.MaxWaitMs) return false;
            if (request.HasJournalAfterCursor && request.JournalAfterCursor < 0) return false;
            var seen = new HashSet<Mirror.Section>();
            foreach (var ask in request.Asks)
            {
                if (!ask.HasSection || !Known(ask.Section) || !seen.Add(ask.Section)) return false;
                if ((ask.Section == Mirror.Section.PlanningCells) != (ask.Window != null)) return false;
                if (ask.Window != null && !NativeMirrorCellGrid.ValidWindow(ask.Window)) return false;
                if ((ask.Section == Mirror.Section.Pawns) != (ask.Pawns != null)) return false;
                if ((ask.Section == Mirror.Section.ColonyFacts) != (ask.ColonyFacts != null)) return false;
                if (ask.Pawns != null && (!NativePawnObservationTools.Validate(ask.Pawns, out _) || !request.Identity.Equals(ask.Pawns.Scope?.ExpectedIdentity))) return false;
                if (ask.ColonyFacts != null && (!NativeColonyObservationTools.Validate(ask.ColonyFacts, out _) || !request.Identity.Equals(ask.ColonyFacts.Scope?.ExpectedIdentity))) return false;
            }
            return true;
        }

        private static bool Known(Mirror.Section section) => section == Mirror.Section.Buildings || section == Mirror.Section.Bills || section == Mirror.Section.PlanningCells
            || section == Mirror.Section.Zones || section == Mirror.Section.Pawns || section == Mirror.Section.ColonyFacts
            || section == Mirror.Section.CombatPawns || section == Mirror.Section.CombatEvents;

        // On the main thread. idle is set when nothing was past its
        // watermark and the caller should wait (wake is the journal's
        // waiter, if one was registered) and serve again.
        private static object Serve(Mirror.MirrorPollRequest request, int waitMs, out Task<bool>? wake, out bool idle)
        {
            wake = null;
            idle = false;
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out Map? map, out var context, out var failure))
                return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = failure });
            Supervisor.NoteControllerRead();
            var epoch = new Mirror.Epoch { Process = ProcessToken, Identity = context.Identity.Clone() };
            if (context.HasNativeGeneration) epoch.NativeGeneration = context.NativeGeneration;
            var same = request.Epoch != null && request.Epoch.Equals(epoch);
            var page = new Mirror.MirrorPage { Epoch = epoch };
            var changed = !same;
            var complete = context.Tick;
            // Only a waiting poll (the controller's loop) is throttled.
            var throttled = request.HasWaitMs && request.WaitMs > 0;
            if (request.HasJournalAfterCursor)
            {
                var events = new Clock.EventsRequest { Identity = context.Identity.Clone(), AfterCursor = request.JournalAfterCursor, Limit = JournalLimit };
                var journal = Supervisor.TypedEvents(events, context, same ? waitMs : 0, out wake);
                if (journal.Failure != null) return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = journal.Failure });
                page.Journal = journal.Page;
                if (journal.Page.Events.Count > 0 || journal.Page.Gap) changed = true;
            }
            var budget = (int)request.ByteBudget;
            foreach (var ask in request.Asks)
            {
                var since = same && ask.Since != null && (ask.Since.Tick != 0 || ask.Since.Seq != 0)
                    ? new EntityTracking.Mark { Tick = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, ask.Since.Tick)), Seq = ask.Since.Seq } : (EntityTracking.Mark?)null;
                var key = ask.Section + "|" + map.uniqueID.ToString(CultureInfo.InvariantCulture);
                // The combat events are a ring lookup, compared every hop; the
                // combat pawns every hop a hook dirtied them, else at their
                // own shorter interval (#851).
                var interval = ask.Section == Mirror.Section.CombatPawns ? CombatMirror.MinCombatCompareTicks : MinCompareTicks;
                var exempt = ask.Section == Mirror.Section.CombatEvents || ask.Section == Mirror.Section.CombatPawns && CombatMirror.Dirty;
                if (throttled && since != null && !exempt && !Due(key, since.Value, (int)context.Tick, interval))
                {
                    complete = Math.Min(complete, since.Value.Tick);
                    continue;
                }
                var section = ask.Section == Mirror.Section.PlanningCells ? NativeMirrorCellGrid.Read(map, context, ask.Window, since, ask.Resync) : Read(map, ask, context, since);
                if (section == null)
                {
                    complete = Math.Min(complete, since?.Tick ?? 0);
                    continue;
                }
                Compared[key] = EntityTracking.Current;
                if (section.Keyframe != null || Rows(section.Delta) > 0) changed = true;
                var size = section.CalculateSize();
                if (size > budget && page.Sections.Count(s => s.BodyCase != Mirror.SectionPage.BodyOneofCase.None) > 0)
                {
                    page.Sections.Add(new Mirror.SectionPage { Section = ask.Section, More = true });
                    complete = Math.Min(complete, since?.Tick ?? 0);
                    changed = true;
                    continue;
                }
                budget -= size;
                page.Sections.Add(section);
            }
            // Every section served this hop is stamped at the hop's newest
            // mark: nothing noted after it, and the hop is one main-thread
            // section.
            var mark = new Mirror.Watermark { Tick = EntityTracking.Current.Tick, Seq = EntityTracking.Current.Seq };
            foreach (var section in page.Sections)
            {
                if (section.Keyframe != null) section.Keyframe.At = mark.Clone();
                if (section.Delta != null) section.Delta.To = mark.Clone();
                if (section.Resync != null) section.Resync.At = mark.Clone();
            }
            page.CompleteThroughTick = complete;
            if (!changed && waitMs > 0)
            {
                idle = true;
                return null!;
            }
            if (wake != null) { Supervisor.CancelWake(wake); wake = null; }
            try { return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Page = page }); }
            catch (InvalidOperationException)
            {
                return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = ProtoBoundary.Fail(Common.FailureCode.CapacityExhausted,
                    "The mirror page exceeds the reply envelope; read the section with its list tool.") });
            }
        }

        // Rows counts what a delta carries: zero is a delta with no change.
        private static int Rows(Mirror.Delta? delta) => delta == null ? 0
            : delta.Buildings.Count + delta.Bills.Count + delta.Tombstones.Count + delta.CombatPawns.Count + delta.CombatEvents.Count + (delta.Zones?.Zones.Count ?? 0) + NativeMirrorCellGrid.Arrays(delta.Cells);

        // Due reports whether a section asked since a watermark is compared
        // this hop: always on a paused game or when the client is behind the
        // last compare, else once MinCompareTicks have run since it.
        private static bool Due(string key, EntityTracking.Mark since, int tick, int interval)
        {
            if (!Compared.TryGetValue(key, out var last) || since.CompareTo(last) < 0) return true;
            return tick == last.Tick || tick - last.Tick >= interval;
        }

        // Read is one section as a keyframe or as the delta after since;
        // null when its read did not answer.
        private static Mirror.SectionPage? Read(Map map, Mirror.SectionAsk ask, Common.ObservationContext context, EntityTracking.Mark? since)
        {
            var page = new Mirror.SectionPage { Section = ask.Section };
            switch (ask.Section)
            {
                case Mirror.Section.Buildings:
                {
                    var rows = new List<Obs.BuildingState>();
                    var request = new Obs.ListBuildingsRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                        PlayerOnly = true, Category = "artificial", Page = new Common.PageRequest { Limit = 256 } };
                    for (var pages = 0; pages < 256; pages++)
                    {
                        var reply = NativeBuildingObservationTools.Read(map, request, context);
                        if (reply.Observed == null) return null;
                        rows.AddRange(reply.Observed.Buildings);
                        var next = reply.Observed.Completeness?.Page;
                        if (next == null || next.Complete) break;
                        request.Page.Cursor = next.NextCursor;
                    }
                    var tracking = EntityTracking.Peek(map, NativeBuildingObservationTools.ToolName + request.PlayerOnly + request.Category);
                    return Entities(page, ask, tracking, since, rows, row => row.Building.Id, (k, r) => k.Buildings.AddRange(r), (d, r) => d.Buildings.AddRange(r));
                }
                case Mirror.Section.Bills:
                {
                    var rows = new List<Obs.BillStack>();
                    var request = new Obs.BillsRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() }, Page = new Common.PageRequest { Limit = 256 } };
                    for (var pages = 0; pages < 256; pages++)
                    {
                        var reply = NativeBillsObservationTools.Read(map, request, context);
                        if (reply.Observed == null) return null;
                        rows.AddRange(reply.Observed.Benches);
                        var next = reply.Observed.Completeness?.Page;
                        if (next == null || next.Complete) break;
                        request.Page.Cursor = next.NextCursor;
                    }
                    var tracking = EntityTracking.Peek(map, NativeBillsObservationTools.BillsToolName + request.AllFactions);
                    return Entities(page, ask, tracking, since, rows, row => row.Bench.Id, (k, r) => k.Bills.AddRange(r), (d, r) => d.Bills.AddRange(r));
                }
                case Mirror.Section.Zones:
                {
                    // The whole census, paged at the zone list's bound; the
                    // header is the first page's.
                    Obs.ZonesSnapshot? census = null;
                    var request = new Obs.ListZonesRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() }, Page = new Common.PageRequest { Limit = 16 } };
                    for (var pages = 0; pages < 256; pages++)
                    {
                        var reply = NativeZoneObservationTools.Read(map, request, context);
                        if (reply.Observed == null) return null;
                        census ??= new Obs.ZonesSnapshot { Context = reply.Observed.Context, MapSnapshot = reply.Observed.MapSnapshot };
                        census.Zones.AddRange(reply.Observed.Zones);
                        var next = reply.Observed.Completeness?.Page;
                        if (next == null || next.Complete) break;
                        request.Page.Cursor = next.NextCursor;
                    }
                    if (census == null) return null;
                    var tracking = EntityTracking.For(map, ZonesShape);
                    foreach (var row in census.Zones) tracking.Note(row.Id, row);
                    tracking.Sweep(new HashSet<string>(census.Zones.Select(row => row.Id)));
                    var rows = census.Zones.ToList();
                    var header = census.Clone();
                    header.Zones.Clear();
                    return Entities(page, ask, tracking, since, rows, row => row.Id,
                        (k, r) => { k.Zones = header.Clone(); k.Zones.Zones.AddRange(r); },
                        (d, r) => { d.Zones = header.Clone(); d.Zones.Zones.AddRange(r); });
                }
                case Mirror.Section.Pawns:
                {
                    if (!NativePawnObservationTools.TryRead(map, ask.Pawns, context, out var snapshot)) return null;
                    var shape = ask.Pawns.Clone(); shape.ChangedSince = null;
                    snapshot.Delta = SectionDelta.Apply(SectionDelta.Shape(PawnsTool, context.Identity, shape), ask.Pawns.ChangedSince, snapshot, context);
                    page.Keyframe = new Mirror.Keyframe { Pawns = snapshot };
                    return page;
                }
                case Mirror.Section.CombatPawns:
                    Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
                    return CombatMirror.Pawns(map, ask, since);
                case Mirror.Section.CombatEvents:
                    Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
                    return CombatMirror.Events(map, since);
                case Mirror.Section.ColonyFacts:
                {
                    if (!NativeColonyObservationTools.TryRead(map, ask.ColonyFacts, context, out var snapshot)) return null;
                    try { NativeColonyObservationTools.Bound(snapshot); }
                    catch (Exception) { return null; }
                    var shape = ask.ColonyFacts.Clone(); shape.ChangedSince = null;
                    snapshot.Delta = SectionDelta.Apply(SectionDelta.Shape(NativeColonyObservationTools.ToolName, context.Identity, shape), ask.ColonyFacts.ChangedSince, snapshot, context);
                    page.Keyframe = new Mirror.Keyframe { ColonyFacts = snapshot };
                    return page;
                }
            }
            return null;
        }

        // Entities is an entity section's page from its whole list and its
        // tracker: a keyframe when there is no since or the tombstones do not
        // reach it, else the rows changed after since and the ids removed
        // since, with the keyframe beside it when the ask resyncs.
        private static Mirror.SectionPage Entities<T>(Mirror.SectionPage page, Mirror.SectionAsk ask, EntityTracking? tracking, EntityTracking.Mark? since, List<T> rows, Func<T, string> id,
            Action<Mirror.Keyframe, IEnumerable<T>> keyframe, Action<Mirror.Delta, IEnumerable<T>> delta)
        {
            if (since == null || tracking == null || !tracking.Covers(since.Value.Tick))
            {
                page.Keyframe = new Mirror.Keyframe();
                keyframe(page.Keyframe, rows);
                return page;
            }
            page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since.Value.Tick, Seq = since.Value.Seq } };
            delta(page.Delta, rows.Where(row => tracking.ChangedAfter(id(row), since.Value)));
            page.Delta.Tombstones.AddRange(tracking.RemovedAfter(since.Value));
            if (ask.Resync)
            {
                page.Resync = new Mirror.Keyframe();
                keyframe(page.Resync, rows);
            }
            return page;
        }
    }
}

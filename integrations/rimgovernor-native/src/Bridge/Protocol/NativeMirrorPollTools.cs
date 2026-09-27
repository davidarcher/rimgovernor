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
    /// at one context, so the page is consistent across them. A section is
    /// the unfiltered list read its dedicated tool answers (the entity
    /// tracking of #358 is shared with it), served as a keyframe or as the
    /// rows changed strictly after the ask's (tick, seq) watermark plus the
    /// ids removed since. An epoch mismatch, an empty ask or an ask older
    /// than the tombstones reach is a keyframe (stale rule C).
    ///
    /// When nothing is past its watermark the hop registers the journal's
    /// waiter (or, without a journal ask, just sleeps) and the hop after the
    /// wake or wait_ms reads once more without waiting. Sections have no
    /// hooks: a section is compared on the poll, at most once per
    /// MinCompareTicks of running game, so a running colony whose buildings
    /// change every tick does not turn the client's immediate re-poll into
    /// a projection per frame. The hop runs under the mirror admission rank,
    /// after control and observation hops; the wait holds no main-thread
    /// slot.
    /// </summary>
    public sealed class NativeMirrorPollTools
    {
        private const string ToolName = "rimgovernor/mirror_poll";
        internal const uint MaxByteBudget = 768 * 1024;
        private const int JournalLimit = 128;
        // A section compared less than this many ticks ago on a running game
        // is not compared again (a paused game compares every hop).
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
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A mirror poll requires distinct known sections, byte_budget 1.." + MaxByteBudget
                + ", wait_ms<=" + NativeClockTools.MaxWaitMs + ", journal_after_cursor>=0, when present a complete identity, and a window of 1.."
                + NativeMirrorCellGrid.MaxCells + " cells on exactly the planning_cells ask.");
            if (request.Identity != null && !ProtoBoundary.Complete(request.Identity)) return false;
            if (!request.HasByteBudget || request.ByteBudget < 1 || request.ByteBudget > MaxByteBudget) return false;
            if (request.HasWaitMs && request.WaitMs > NativeClockTools.MaxWaitMs) return false;
            if (request.HasJournalAfterCursor && request.JournalAfterCursor < 0) return false;
            var seen = new HashSet<Mirror.Section>();
            foreach (var ask in request.Asks)
            {
                if (!ask.HasSection || !Known(ask.Section) || !seen.Add(ask.Section)) return false;
                if ((ask.Section == Mirror.Section.PlanningCells) != (ask.Window != null)) return false;
                if (ask.Window != null && !NativeMirrorCellGrid.ValidWindow(ask.Window)) return false;
            }
            return true;
        }

        private static bool Known(Mirror.Section section) => section == Mirror.Section.Buildings || section == Mirror.Section.Bills || section == Mirror.Section.PlanningCells;

        // On the main thread. idle is set when nothing was past its
        // watermark and the caller should wait (wake is the journal's
        // waiter, if one was registered) and serve again.
        private static object Serve(Mirror.MirrorPollRequest request, int waitMs, out Task<bool>? wake, out bool idle)
        {
            wake = null;
            idle = false;
            Map? map;
            Common.ObservationContext? context;
            if (request.Identity != null)
            {
                if (!ProtoBoundary.ValidateIdentity(request.Identity, out map, out context, out var failure))
                    return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = failure });
            }
            else
            {
                map = Find.CurrentMap;
                if (!ProtoBoundary.TryReadContext(map, out context, out var unavailable))
                    return ProtoBoundary.Encode(new Mirror.MirrorPollReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, unavailable.Detail) });
            }
            Supervisor.NoteControllerRead();
            var epoch = new Mirror.Epoch { Process = ProcessToken, Identity = context.Identity.Clone() };
            if (context.HasNativeGeneration) epoch.NativeGeneration = context.NativeGeneration;
            var same = request.Epoch != null && request.Epoch.Equals(epoch);
            var page = new Mirror.MirrorPage { Epoch = epoch };
            var changed = !same;
            var complete = context.Tick;
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
                if (since != null && ask.Section != Mirror.Section.PlanningCells && !Due(key, since.Value, (int)context.Tick))
                {
                    complete = Math.Min(complete, since.Value.Tick);
                    continue;
                }
                var section = ask.Section == Mirror.Section.PlanningCells ? NativeMirrorCellGrid.Read(map, context, ask.Window, since) : Read(map, ask.Section, context, since);
                if (section == null)
                {
                    complete = Math.Min(complete, since?.Tick ?? 0);
                    continue;
                }
                Compared[key] = EntityTracking.Current;
                if (section.Keyframe != null || section.Delta.Buildings.Count + section.Delta.Bills.Count + section.Delta.Tombstones.Count + NativeMirrorCellGrid.Arrays(section.Delta.Cells) > 0) changed = true;
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

        // Due reports whether a section asked since a watermark is compared
        // this hop: always on a paused game or when the client is behind the
        // last compare, else once MinCompareTicks have run since it.
        private static bool Due(string key, EntityTracking.Mark since, int tick)
        {
            if (!Compared.TryGetValue(key, out var last) || since.CompareTo(last) < 0) return true;
            return tick == last.Tick || tick - last.Tick >= MinCompareTicks;
        }

        // Read is one section, projected whole through its list read (which
        // notes every row in the section's tracker), as a keyframe or as the
        // delta after since; null when the list read did not answer.
        private static Mirror.SectionPage? Read(Map map, Mirror.Section section, Common.ObservationContext context, EntityTracking.Mark? since)
        {
            var page = new Mirror.SectionPage { Section = section };
            switch (section)
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
                    if (since == null || tracking == null || !tracking.Covers(since.Value.Tick))
                    {
                        page.Keyframe = new Mirror.Keyframe();
                        page.Keyframe.Buildings.AddRange(rows);
                        return page;
                    }
                    page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since.Value.Tick, Seq = since.Value.Seq } };
                    page.Delta.Buildings.AddRange(rows.Where(row => tracking.ChangedAfter(row.Building.Id, since.Value)));
                    page.Delta.Tombstones.AddRange(tracking.RemovedAfter(since.Value));
                    return page;
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
                    if (since == null || tracking == null || !tracking.Covers(since.Value.Tick))
                    {
                        page.Keyframe = new Mirror.Keyframe();
                        page.Keyframe.Bills.AddRange(rows);
                        return page;
                    }
                    page.Delta = new Mirror.Delta { From = new Mirror.Watermark { Tick = since.Value.Tick, Seq = since.Value.Seq } };
                    page.Delta.Bills.AddRange(rows.Where(row => tracking.ChangedAfter(row.Bench.Id, since.Value)));
                    page.Delta.Tombstones.AddRange(tracking.RemovedAfter(since.Value));
                    return page;
                }
            }
            return null;
        }
    }
}

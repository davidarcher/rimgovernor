using System;
using System.Collections.Generic;
using System.Diagnostics;
using HomeBridge.BridgeTools;

// Phase accounting for the observation capture path (#642). Every clock here
// is supplied, not measured: FrameAccounting.UpdateAt takes the monotonic
// timestamp and ObservationWork takes stopwatch tick counts, so the intervals,
// the slow-threshold counts and the millisecond conversions are exact and no
// assertion depends on this machine finishing anything within a deadline.
// It also pins the storage bounds: the section list and the worst-frame ring
// are capped, and a new game session resets the account.
internal static class NativeObservationWorkProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    // Stopwatch ticks for a given number of milliseconds, so a supplied
    // duration converts back to that many milliseconds.
    private static long Ticks(double ms) { return (long)Math.Round(ms * Stopwatch.Frequency / 1000.0); }

    // Milliseconds agree to within a tick of rounding: the conversion back
    // from supplied stopwatch ticks is exact only when the platform's
    // stopwatch frequency divides a millisecond.
    private static bool Near(object actual, double expected) { return Math.Abs((double)actual - expected) < 0.05; }

    private static Dictionary<string, object> Section(Dictionary<string, object> report, string name)
    {
        var sections = (Dictionary<string, object>)report["sections"];
        return (Dictionary<string, object>)sections[name];
    }

    internal static void Invoke()
    {
        HopAccount();
        HopReportsNothingWithoutWork();
        SectionsAreBounded();
        FrameIntervals();
        FrameRingIsBounded();
        SessionResets();
        Console.WriteLine("native-observation-work: " + checks + " checks passed");
    }

    // One hop's account: capture spans sum, sections keep their own rows and
    // candidates, formatting passes are counted apart from their duration,
    // and the payload byte count is the last envelope encoded.
    private static void HopAccount()
    {
        var hop = ObservationWork.Begin();
        ObservationWork.Captured("emergency", Ticks(2), 8);
        ObservationWork.Captured("planningWindow", Ticks(10), 300, 4096);
        ObservationWork.Captured("planningWindow", Ticks(5), 100, 1000);
        ObservationWork.Formatted(Ticks(1));
        ObservationWork.Formatted(Ticks(3));
        ObservationWork.SizeChecked(Ticks(0.5));
        ObservationWork.Payload(1000);
        ObservationWork.Payload(2048);
        ObservationWork.DroppedSections(2);
        ObservationWork.Outcome("ok");
        Check(ReferenceEquals(hop, ObservationWork.End()), "hop scope closes");
        Check(ObservationWork.Current == null, "scope cleared");
        var report = ObservationWork.Report(hop);
        Check(report != null, "account reported");
        Check(Near(report["captureMs"], 17), "capture sums the spans");
        Check(Near(report["formatMs"], 4.5), "format includes the size check");
        Check((int)report["formatPasses"] == 2, "size check is not a formatting pass");
        Check((long)report["payloadBytes"] == 2048, "the returned envelope's bytes win");
        Check((int)report["droppedSections"] == 2, "dropped sections reported");
        Check((string)report["outcome"] == "ok", "outcome reported");
        var window = Section(report, "planningWindow");
        Check(Near(window["ms"], 15) && (long)window["rows"] == 400 && (long)window["candidates"] == 5096, "repeated section accumulates");
        var emergency = Section(report, "emergency");
        Check(Near(emergency["ms"], 2) && (long)emergency["rows"] == 8, "emergency section");
        Check(!emergency.ContainsKey("candidates"), "no candidate total is absent, not zero");
        // Recording outside a scope is a no-op, never a failure.
        ObservationWork.Captured("stray", Ticks(5), 1);
        ObservationWork.Formatted(Ticks(5));
        Check(ObservationWork.Current == null, "recording outside a hop is dropped");
    }

    // A hop that only ran control work declares nothing, so its reply carries
    // no observation block at all.
    private static void HopReportsNothingWithoutWork()
    {
        var hop = ObservationWork.Begin();
        ObservationWork.End();
        Check(ObservationWork.Report(hop) == null, "an empty hop reports nothing");
        Check(ObservationWork.Report(null) == null, "no hop reports nothing");
    }

    // The section list is bounded: a request cannot grow the account past
    // MaxSections, and the sections already recorded keep accumulating.
    private static void SectionsAreBounded()
    {
        var hop = ObservationWork.Begin();
        for (var i = 0; i < ObservationWork.MaxSections + 20; i++) ObservationWork.Captured("section" + i, Ticks(1), 1);
        ObservationWork.Captured("section0", Ticks(1), 1);
        ObservationWork.End();
        var report = ObservationWork.Report(hop);
        Check(((Dictionary<string, object>)report["sections"]).Count == ObservationWork.MaxSections, "section list bounded");
        Check((long)Section(report, "section0")["rows"] == 2, "a known section still accumulates past the bound");
        // Every span is still charged to the capture total, bound or not.
        Check(Near(report["captureMs"], ObservationWork.MaxSections + 21), "capture counts the spans it could not name");
    }

    // Update intervals, slow counts and the observation work charged to them,
    // all from supplied timestamps.
    private static void FrameIntervals()
    {
        var session = new object();
        FrameAccounting.Started(session);
        var now = Ticks(1000);
        // The first update opens an interval; it closes no interval of its own.
        FrameAccounting.UpdateAt(session, 100, now);
        Check(FrameAccounting.OpenFrame() == 1, "the first interval is open");
        Check(FrameAccounting.Report() != null, "a hooked recorder reports");
        ulong updates; double elapsedMs, observationMs; ulong observations, cancelled;
        FrameAccounting.Totals(out updates, out elapsedMs, out observationMs, out observations, out cancelled);
        Check(updates == 0 && Near(elapsedMs, 0), "no interval before the second update");
        // 10 ms, then 20 ms with a 12 ms observation inside it, then 120 ms.
        now += Ticks(10);
        FrameAccounting.UpdateAt(session, 101, now);
        FrameAccounting.Observed(Ticks(12), "trace-a/1");
        FrameAccounting.Observed(Ticks(3), "trace-b/1");
        now += Ticks(20);
        FrameAccounting.UpdateAt(session, 102, now);
        now += Ticks(120);
        FrameAccounting.UpdateAt(session, 103, now);
        FrameAccounting.Cancelled();
        FrameAccounting.Totals(out updates, out elapsedMs, out observationMs, out observations, out cancelled);
        Check(updates == 3, "three intervals closed");
        Check(Near(elapsedMs, 150), "intervals sum to the supplied wall");
        Check(Near(observationMs, 15) && observations == 2 && cancelled == 1, "observation work and cancellations counted");
        var report = FrameAccounting.Report();
        Check((ulong)report["updates"] == 3, "report counts the intervals");
        Check(Near(report["maxUpdateMs"], 120), "widest interval");
        Check(Near(report["observationMs"], 15), "observation work reported");
        Check((ulong)report["cancelled"] == 1, "cancelled hops reported");
        Check((double)report["recorderMs"] >= 0, "the recorder reports its own cost");
        var slow = (List<object>)report["slow"];
        Check(slow.Count == FrameAccounting.Thresholds.Length, "every declared threshold is reported");
        // 20 ms and 120 ms exceed 16.7; 120 exceeds 33.3 and 100; none exceeds 250.
        var counts = new ulong[slow.Count];
        for (var i = 0; i < slow.Count; i++)
        {
            var bucket = (Dictionary<string, object>)slow[i];
            Check((double)bucket["thresholdMs"] == FrameAccounting.Thresholds[i], "threshold order");
            counts[i] = (ulong)bucket["count"];
        }
        Check(counts[0] == 2 && counts[1] == 1 && counts[2] == 1 && counts[3] == 0, "slow counts at the declared thresholds");
        var worst = (List<object>)report["worst"];
        Check(worst.Count == 3, "each closed interval is a candidate");
        var widest = (Dictionary<string, object>)worst[0];
        Check(Near(widest["intervalMs"], 120), "worst ring sorted widest first");
        // The 20 ms interval carried the observation work and names the most
        // expensive observation's trace.
        var carried = (Dictionary<string, object>)worst[1];
        Check(Near(carried["intervalMs"], 20) && Near(carried["observationMs"], 15), "work charged to its own interval");
        Check((string)carried["trace"] == "trace-a/1", "the most expensive observation names the interval");
        Check((int)carried["tick"] == 101, "the interval carries the tick it opened at");
        // Consecutive hops inside one update share one immutable report.
        Check(ReferenceEquals(report, FrameAccounting.Report()), "the report is memoized per update");
    }

    // The worst ring keeps a bounded number of intervals and keeps the widest.
    private static void FrameRingIsBounded()
    {
        var session = new object();
        FrameAccounting.Started(session);
        var now = Ticks(1000);
        FrameAccounting.UpdateAt(session, 0, now);
        for (var i = 1; i <= 200; i++)
        {
            now += Ticks(i);
            FrameAccounting.UpdateAt(session, i, now);
        }
        var worst = (List<object>)FrameAccounting.Report()["worst"];
        Check(worst.Count <= 8, "worst ring bounded");
        Check(Near(((Dictionary<string, object>)worst[0])["intervalMs"], 200), "the widest interval is kept");
        var least = (Dictionary<string, object>)worst[worst.Count - 1];
        Check(Near(least["intervalMs"], 200 - worst.Count + 1), "the ring keeps the widest, not the newest");
    }

    // A different game session is nobody else's frame: everything resets.
    private static void SessionResets()
    {
        var first = new object();
        FrameAccounting.Started(first);
        var now = Ticks(1000);
        FrameAccounting.UpdateAt(first, 0, now);
        FrameAccounting.UpdateAt(first, 1, now + Ticks(50));
        FrameAccounting.Observed(Ticks(9), "t/1");
        FrameAccounting.Cancelled();
        ulong updates; double elapsedMs, observationMs; ulong observations, cancelled;
        FrameAccounting.Totals(out updates, out elapsedMs, out observationMs, out observations, out cancelled);
        Check(updates == 1 && observations == 1 && cancelled == 1, "the first session accounted");
        var second = new object();
        FrameAccounting.UpdateAt(second, 0, now + Ticks(60));
        FrameAccounting.Totals(out updates, out elapsedMs, out observationMs, out observations, out cancelled);
        Check(updates == 0 && Near(elapsedMs, 0) && Near(observationMs, 0) && observations == 0 && cancelled == 0, "a new session resets the account");
        Check(((List<object>)FrameAccounting.Report()["worst"]).Count == 0, "a new session clears the ring");
    }
}

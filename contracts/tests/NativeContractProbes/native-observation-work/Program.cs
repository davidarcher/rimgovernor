using System;
using System.Collections.Generic;
using RimGovernor.Native;
using RimGovernor.Host.Sdk;

// The frame capture's section spans. Every duration here is a supplied
// stopwatch tick count, so no assertion depends on this machine finishing
// anything within a deadline. It also pins the storage bound: the section
// list is capped.
internal static class NativeObservationWorkProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }

    private static ObservationWork.Section Section(ObservationWork.Hop hop, string name)
    {
        foreach (var section in hop.Sections) if (section.Name == name) return section;
        throw new Exception("no section " + name);
    }

    internal static void Invoke()
    {
        Spans();
        FailedSectionIsLogged();
        RequiredBuildingsDoNotDisappear();
        CompletedFrameWork();
        SectionsAreBounded();
        Console.WriteLine("native-observation-work: " + checks + " checks passed");
    }

    // A capture's spans: a repeated section accumulates ticks and rows, a
    // detail span is marked apart from the families, and recording outside a
    // scope is dropped.
    private static void Spans()
    {
        var hop = ObservationWork.BeginCapture();
        ObservationWork.Captured("emergency", 2, 8);
        ObservationWork.Captured("planningWindow", 10, 300);
        ObservationWork.Captured("planningWindow", 5, 100);
        ObservationWork.Detail("planningWindow.rooms", 3, 7);
        ObservationWork.Captured("negative", -4, -1);
        Check(ReferenceEquals(hop, ObservationWork.End()), "the scope closes with its spans");
        Check(ObservationWork.End() == null, "the scope is cleared");
        var window = Section(hop, "planningWindow");
        Check(window.Ticks == 15 && window.Rows == 400 && !window.Detail, "repeated section accumulates");
        var emergency = Section(hop, "emergency");
        Check(emergency.Ticks == 2 && emergency.Rows == 8, "emergency section");
        Check(Section(hop, "planningWindow.rooms").Detail, "a detail span is marked");
        var negative = Section(hop, "negative");
        Check(negative.Ticks == 0 && negative.Rows == 0, "negative spans clamp to zero");
        ObservationWork.Captured("stray", 5, 1);
        Check(ObservationWork.End() == null, "recording outside a scope is dropped");
    }

    // A family that throws writes a mod log error row carrying the
    // exception's stack.
    private static void FailedSectionIsLogged()
    {
        ModLog.ResetForProbe();
        var rows = new List<(ModLogLevel Level, string Message)>();
        ModLog.Sink = (level, component, tick, message, suppressed) => rows.Add((level, message));
        try { throw new InvalidOperationException("forced traders failure"); }
        catch (Exception ex) { ObservationWork.Failed("traders", ex); }
        finally { ModLog.Sink = null; }
        Check(rows.Count == 1 && rows[0].Level == ModLogLevel.Error, "one error row");
        var line = rows[0].Message;
        Check(line.Contains("traders") && line.Contains("forced traders failure") && line.Contains("FailedSectionIsLogged"), "log line names the section and carries the stack");
    }

    private static void CompletedFrameWork()
    {
        // Vanilla adds a complete work tick before checking completion. A guard
        // can refuse completion without undoing that last increment (#3714).
        const float total = 135f;
        var done = 134.5f;
        Check(NativeConstructionWork.Remaining(total - done) == 0.5f, "unfinished frame retains its work");
        done += 2f;
        Check(NativeConstructionWork.Remaining(total - done) == 0f, "guarded overshoot has no labor left");
        Check(NativeConstructionWork.Remaining(0f) == 0f, "exact completion has no labor left");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var refused = false;
            try { NativeConstructionWork.Remaining(invalid); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "nonfinite construction work remains unavailable");
        }
    }

    private static void RequiredBuildingsDoNotDisappear()
    {
        var complete = new RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot
        {
            Context = new RimGovernor.Brain.GameFacts.Common.ObservationContext { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 1 },
            Buildings = [],
        };
        Check(ReferenceEquals(complete, SnapshotFrames.SnapshotSectionException.Read("Buildings", () => complete)), "known empty buildings stay observed");
        try
        {
            SnapshotFrames.SnapshotSectionException.Read<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot>("Buildings",
                () => throw new InvalidOperationException("building 42: Invalid native amount."));
            throw new Exception("a failed building read was accepted");
        }
        catch (SnapshotFrames.SnapshotSectionException error)
        {
            var frame = error.Frame();
            Check(frame.Buildings == null && frame.Failure != null, "failure frame never invents an empty census");
            Check((frame.Failure ?? throw new Exception("failure frame has no failure")).Detail?.Contains("Buildings") == true, "failure names the required section");
            Check((frame.Failure ?? throw new Exception("failure frame has no failure")).Detail?.Contains("building 42") == true, "failure retains native detail");
        }
    }

    // The section list is bounded: a shape cannot grow the account past
    // MaxSections, and the sections already recorded keep accumulating.
    private static void SectionsAreBounded()
    {
        var hop = ObservationWork.BeginCapture();
        for (var i = 0; i < ObservationWork.MaxSections + 20; i++) ObservationWork.Captured("section" + i, 1, 1);
        ObservationWork.Captured("section0", 1, 1);
        ObservationWork.End();
        Check(hop.Sections.Count == ObservationWork.MaxSections, "section list bounded");
        Check(Section(hop, "section0").Rows == 2, "a known section still accumulates past the bound");
    }
}

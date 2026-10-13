using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The adaptive-timetable cases (Go <c>schedule/schedule.go</c>): the work family's schedule planner writes each pawn's
/// timetable through the WorkSettingsIntent, and these cases read the written timetables back from native.
/// <c>schedule/timetables</c>: the recreation hour sits right before sleep; a pawn staged Drowsy (rest below RestEnter)
/// gets its Sleep block extended by SleepExtension hours at the wake end, and the extension reverts once the pawn is
/// staged rested. <c>schedule/meditate</c>: on a Royalty profile a psylinked pawn meditates in its recreation block
/// instead of Joy.
/// </summary>
internal static class ScheduleTimetables
{
    private const string SetupOp = "test/schedule_setup";

    internal static AcceptanceCase TimetablesCase() => new()
    {
        Name = "schedule/timetables",
        Scope = "Issue #1318: the work family writes each colonist a timetable whose Joy hour sits right before Sleep; "
            + "a colonist staged Drowsy gets two more Sleep hours at the wake end, and loses them once staged rested. "
            + "Each written timetable is read back from native.",
        Start = new LabStart(),
        Service = true,
        RequiredOps = [SetupOp],

        // Rest stays live so the fixture's staged level is what the planner reads.
        Keep = ["Rest"],
        Budget = TimeSpan.FromMinutes(12),
        Crew = new Crew { Size = 3 },
        Run = TimetablesAsync,
    };

    internal static AcceptanceCase MeditateCase() => new()
    {
        Name = "schedule/meditate",
        Scope = "Issue #1318: on a Royalty profile a colonist given a psylink gets Meditate in its recreation block "
            + "instead of Joy, read back from native; colonists without a psylink keep Joy.",
        Start = new FixtureStart
        {
            Op = SetupOp,
            Args = new JsonObject { ["rest"] = 0.9, ["psylink"] = true },
            On = new DebugStart { Size = new DebugStartSize { Seed = "schedule-meditate-1318" } },
        },
        Expansions = ["ludeon.rimworld.royalty"],
        NoKeep = true,
        Service = true,
        Keep = ["Rest"],
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = MeditateAsync,
    };

    /// <summary>One serve window's evidence: the timetable the work family wrote for the target pawn and every colonist's native timetable afterwards.</summary>
    private sealed record WindowRun(IReadOnlyList<string> Planned, IReadOnlyDictionary<string, IReadOnlyList<string>> Native);

    private static HashSet<int> Hours(IReadOnlyList<string> slots, string def)
    {
        var result = new HashSet<int>();
        for (var h = 0; h < slots.Count; h++)
        {
            if (slots[h] == def)
            {
                result.Add(h);
            }
        }

        return result;
    }

    /// <summary>The last hour of the Sleep block (the hour whose successor is not Sleep), or -1.</summary>
    private static int BlockEnd(IReadOnlyList<string> slots)
    {
        for (var h = 0; h < slots.Count; h++)
        {
            if (slots[h] == PolicyConstants.ScheduleSleep && slots[(h + 1) % 24] != PolicyConstants.ScheduleSleep)
            {
                return h;
            }
        }

        return -1;
    }

    private static string Show(IReadOnlyList<string> slots) => "[" + string.Join(' ', slots) + "]";

    /// <summary>Checks slots carry exactly one block hour, right before the Sleep block, and never Work.</summary>
    private static string? RecreationBeforeSleep(IReadOnlyList<string> slots, string block)
    {
        if (slots.Count != 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"timetable has {slots.Count} hours");
        }

        if (Hours(slots, "Work").Count > 0)
        {
            return $"timetable writes Work: {Show(slots)}";
        }

        var at = Hours(slots, block);
        if (at.Count != 1)
        {
            return $"want one {block} hour, got {Show(slots)}";
        }

        foreach (var h in at)
        {
            if (slots[(h + 1) % 24] != PolicyConstants.ScheduleSleep)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{block} hour {h} is not right before Sleep: {Show(slots)}");
            }
        }

        return null;
    }

    /// <summary>Checks now is base with the Sleep block SleepExtension hours longer at the wake end.</summary>
    private static string? Extended(IReadOnlyList<string> baseSlots, IReadOnlyList<string> now)
    {
        var end = BlockEnd(baseSlots);
        var want = baseSlots.ToArray();
        for (var i = 1; i <= PolicyConstants.SleepExtension; i++)
        {
            want[(((end + i) % 24) + 24) % 24] = PolicyConstants.ScheduleSleep;
        }

        return Same(want, now) ? null : $"want {Show(want)}, got {Show(now)}";
    }

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    /// <summary>Stages the target pawn through the fixture and returns its ID.</summary>
    private static async Task<string> SetupAsync(Harness h, string label, double rest, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, SetupOp, new JsonObject { ["rest"] = rest }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: {reply.ToJson()}");
        }

        return reply["pawn"].AsString();
    }

    /// <summary>Reads every free colonist's native timetable.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<string>>> ReadTimetablesAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["colonist"] = true },
                ["details"] = new JsonObject { ["schedule"] = true },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var raw in observed["pawns"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var pawn = row["pawn"].AsMap() ?? [];
            var settings = row["settings"].AsMap();
            var slots = new string[24];
            Array.Fill(slots, "");
            foreach (var s in settings?["schedule"].AsSlice() ?? [])
            {
                var slot = s.AsMap() ?? [];
                var hour = (int)slot["hour"].AsNumber();
                if (hour is >= 0 and < 24)
                {
                    slots[hour] = slot["assignmentDefName"].AsString();
                }
            }

            result[pawn["id"].AsString()] = slots;
        }

        if (result.Count == 0)
        {
            throw new AcceptanceException($"{label}: no colonists observed: {observed.ToJson()}");
        }

        return result;
    }

    /// <summary>Reports every action of the plan completed.</summary>
    private static bool Completed(RimGovernor.Brain.Store.PlanState plan) =>
        plan.Progress.Count != 0
        && plan.Progress.Count == plan.Spec.Actions.Count
        && plan.Progress.All(p => p.View.Stage == Stage.Completed);

    private static JsonArray ToArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)v)]);

    /// <summary>
    /// Serves the work family until a completed work plan not in <paramref name="seen"/> wrote <paramref name="pawn"/> a
    /// timetable <paramref name="want"/> accepts, then stops the service and reads the native timetables back.
    /// <paramref name="seen"/> gains every work plan the window observed.
    /// </summary>
    private static async Task<WindowRun> WindowAsync(
        ISession s,
        string label,
        string pawn,
        HashSet<string> seen,
        Func<IReadOnlyList<string>, string?> want,
        CancellationToken cancellationToken)
    {
        var report = s.Report;
        var identity = s.Identity;
        var service = await s.LaunchAsync(new ServiceLaunch { Families = ["work"] }, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? planned = null;
        Func<Task<JsonObject>>? stopKeep = null;
        try
        {
            var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            _ = await service.WaitAttachedAsync(identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            _ = await service.ResumeAsync(label, identity, token, report, cancellationToken).ConfigureAwait(false);
            stopKeep = new AuthorityKeepAlive(service, label, identity, token).Start(cancellationToken);
            using (var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false))
            {
                string? last = null;
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (planned is null)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        throw new AcceptanceException($"{label}: no completed work plan wrote {pawn} the wanted timetable (last: {last})");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    var plans = await Task.Run(() => journal.PlanHistoryWithMethods(64, ["work-*"], cancellationToken), cancellationToken).ConfigureAwait(false);
                    foreach (var plan in plans)
                    {
                        var id = plan.Spec.Id.Value;
                        if (seen.Contains(id) || !Completed(plan))
                        {
                            continue;
                        }

                        _ = seen.Add(id);
                        foreach (var action in plan.Spec.Actions)
                        {
                            if (action.WorkAssignment is not { } w || w.Pawn.Value != pawn || !w.HasSchedule)
                            {
                                continue;
                            }

                            last = want(w.Schedule);
                            if (last is null)
                            {
                                planned = [.. w.Schedule];
                                report[label + "_plan"] = id;
                            }
                        }
                    }
                }
            }

            report[label + "_planned"] = ToArray(planned);
        }
        finally
        {
            if (stopKeep is not null)
            {
                _ = await stopKeep().ConfigureAwait(false);
            }

            _ = await service.StopAsync().ConfigureAwait(false);
        }

        var h = s.Reattach();
        _ = await h.CallAsync(label + "-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var native = await ReadTimetablesAsync(h, label + "-native", identity, cancellationToken).ConfigureAwait(false);
        var nativeReport = new JsonObject();
        foreach (var (id, slots) in native)
        {
            nativeReport[id] = ToArray(slots);
        }

        report[label + "_native"] = nativeReport;
        if (!native.TryGetValue(pawn, out var own) || !Same(own, planned))
        {
            throw new AcceptanceException($"{label}: native timetable {(own is null ? "[]" : Show(own))} is not the written {Show(planned)}");
        }

        return new WindowRun(planned, native);
    }

    private static async Task TimetablesAsync(ISession s, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pawn = await SetupAsync(s.Harness, "rested", 0.9, cancellationToken).ConfigureAwait(false);
        s.Report["pawn"] = pawn;
        var baseRun = await WindowAsync(s, "base", pawn, seen, slots => RecreationBeforeSleep(slots, PolicyConstants.ScheduleJoy), cancellationToken).ConfigureAwait(false);

        // Every colonist the planner wrote (not only the target) has Joy right before Sleep: the lab has no recreation
        // places to stagger.
        foreach (var (id, slots) in baseRun.Native)
        {
            if (RecreationBeforeSleep(slots, PolicyConstants.ScheduleJoy) is { } problem)
            {
                throw new AcceptanceException($"colonist {id}: {problem}");
            }
        }

        _ = await SetupAsync(s.Harness, "drowsy", 0.1, cancellationToken).ConfigureAwait(false);
        _ = await WindowAsync(s, "drowsy", pawn, seen, slots => Extended(baseRun.Planned, slots), cancellationToken).ConfigureAwait(false);
        _ = await SetupAsync(s.Harness, "restored", 1.0, cancellationToken).ConfigureAwait(false);
        _ = await WindowAsync(
            s,
            "restored",
            pawn,
            seen,
            slots => Same(slots, baseRun.Planned) ? null : $"want base {Show(baseRun.Planned)}, got {Show(slots)}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MeditateAsync(ISession s, CancellationToken cancellationToken)
    {
        var pawn = s.Prepared?["pawn"].AsString() ?? "";
        if (pawn.Length == 0)
        {
            throw new AcceptanceException($"fixture named no pawn: {s.Prepared.ToJson()}");
        }

        s.Report["pawn"] = pawn;
        var got = await WindowAsync(
            s,
            "meditate",
            pawn,
            [],
            slots => Hours(slots, PolicyConstants.ScheduleMeditate).Count == 0 || Hours(slots, PolicyConstants.ScheduleJoy).Count > 0
                ? $"want Meditate in place of Joy, got {Show(slots)}"
                : null,
            cancellationToken).ConfigureAwait(false);
        foreach (var (id, slots) in got.Native)
        {
            if (id != pawn && Hours(slots, PolicyConstants.ScheduleMeditate).Count > 0)
            {
                throw new AcceptanceException($"colonist {id} without a psylink meditates: {Show(slots)}");
            }
        }
    }
}

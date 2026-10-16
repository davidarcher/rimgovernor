using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// sleeping/suites (Go <c>sleeping/suites.go</c>) starts from the same layout/grid fixture as sleeping/bedrooms with
/// one hut colonist turned Greedy, on a Royalty profile. Phase one: the sleeping planner builds a standard bedroom
/// wing, gives the Greedy colonist (whose standard room cannot reach slightly impressive on space) a suite and moves
/// them into it. Then the fixture grants them Baron, whose bedroom floor (70) needs more floor than the suite has.
/// Phase two: a suite never grows, so the planner sites a second, larger suite and shells it, and the first suite's
/// plan row stays exactly as it was.
/// </summary>
internal static partial class SleepingSuites
{
    private const string SuiteTitleOp = "test/layout_grid_title";

    // The title's bedroom floor (70) outgrows a suite sized for Greedy (50): 7x7 then, 7x8 after (layout_suite_role.go SuiteSize).
    private const string SuiteTitle = "Baron";

    [GeneratedRegex(@"^bedroom-shell-room(\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ShellMethod();

    /// <summary>The Greedy colonist's completed move into a suite.</summary>
    /// <param name="Assign">The assignment that moved them.</param>
    /// <param name="Room">The suite they moved into.</param>
    /// <param name="Standard">The completed standard-wing shells before it.</param>
    private sealed record SuiteMove(Assign Assign, PlannedRoom Room, int Standard);

    internal static AcceptanceCase Case() => new()
    {
        Name = "sleeping/suites",
        Scope = "Issue #1221: on the tribal " + SustainedFoodCase.BaselineSave + " colony at Masonry with a fixture hut and a Greedy colonist, "
            + "the sleeping planner builds a standard bedroom wing, gives the Greedy colonist a suite and moves them into it; "
            + "once a Baron title raises their target a second, larger suite is planned and shelled and the first suite "
            + "is left as it is. A snapshot test cannot cover it: the order is proven against real enclosure, roofing and native bed ownership.",
        Start = new FixtureStart
        {
            Op = "test/layout_grid_prepare",
            ArgsFrom = StarterSite.DefaultArgsAsync,
            Args = new JsonObject { ["sleepingSpots"] = 8, ["stoneBlocks"] = 1500, ["builders"] = true, ["greedy"] = true },
            On = new SaveStart { Name = SustainedFoodCase.BaselineSave },
        },
        RequiredOps = [SuiteTitleOp],
        Keep = ["Food"],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "sleeping-suites",
        },
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Reason = "two watches on a Royalty profile: the standard wing goes up one room per review (four rooms in ~236k ticks, 7 min, on the first run) before the suite and move, then the title-driven second suite",
        Run = RunAsync,
    };

    /// <summary>Reports every action of the plan completed, and the newest completion tick.</summary>
    private static bool Completed(PlanState plan)
    {
        if (plan.Progress.Count == 0 || plan.Progress.Count != plan.Spec.Actions.Count)
        {
            return false;
        }

        return plan.Progress.All(p => p.View.Stage == Stage.Completed);
    }

    /// <summary>The journal's bedroom plans, oldest first.</summary>
    private static IEnumerable<PlanState> History(Journal journal, CancellationToken cancellationToken) =>
        journal.PlanHistoryWithMethods(256, ["bedroom-*", "sleeping-assign-*"], cancellationToken).Reverse();

    /// <summary>The room of <paramref name="role"/> whose interior starts at x,z.</summary>
    private static PlannedRoom? RoomWithId(LayoutPlan plan, PlannedRole role, int id) =>
        plan.Rooms.FirstOrDefault(r => r.Role == role && r.Id == id);

    /// <summary>The journal's newest review and layout plan; null when either is not there yet.</summary>
    private static LayoutPlan? PlanOf(Journal journal, CancellationToken cancellationToken)
    {
        ReviewRecord review;
        try
        {
            review = journal.LoadRounds(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        return journal.LayoutPlan(review.Snapshot, review.Tick, cancellationToken)?.Plan;
    }

    /// <summary>Reads the journal for the pawn's completed move into a suite.</summary>
    private static SuiteMove? FindSuiteMove(Journal journal, string pawn, CancellationToken cancellationToken)
    {
        if (PlanOf(journal, cancellationToken) is not { } layout)
        {
            return null;
        }

        var plans = History(journal, cancellationToken);
        var suite = layout.Rooms.FirstOrDefault(r => r.Role == PolicyFunctions.PlannedSuite);
        if (suite is null)
        {
            return null;
        }

        // Every move is a sleeping-assign plan: the pawn's second completed one, out of the standard bed its first
        // gave it, is the suite move.
        var standard = 0;
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var method = plan.Method.Value;
            if (ShellMethod().Match(method) is { Success: true } shell)
            {
                var id = int.Parse(shell.Groups[1].Value, CultureInfo.InvariantCulture);
                if (RoomWithId(layout, PolicyFunctions.PlannedBedroom, id) is not null && Completed(plan))
                {
                    standard++;
                }

                continue;
            }

            if (!method.StartsWith("sleeping-assign-", StringComparison.Ordinal) || !Completed(plan))
            {
                continue;
            }

            foreach (var a in plan.Spec.Actions)
            {
                if (a.Assign is not { } assign || assign.Pawn.Value != pawn)
                {
                    continue;
                }

                if (moved.Contains(assign.Previous.Id))
                {
                    return new SuiteMove(assign, suite, standard);
                }

                moved.Add(assign.Thing);
            }
        }

        return null;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared;
        report["fixture"] = prepared?.DeepClone();
        var pawn = prepared?["greedyPawn"].AsString() ?? "";
        if (pawn.Length == 0)
        {
            throw new AcceptanceException($"fixture turned no colonist Greedy: {prepared.ToJson()}");
        }

        var path = ServiceJournal.PathOf(s.Config);
        SuiteMove? move = null;

        // Phase one: the wing, the suite and the move.
        Journal? live = null;
        try
        {
            _ = await SustainedFood.ObserveAsync(
                ObservedSession.Of(s),
                new SustainedObservation
                {
                    Watch = new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(16),
                        Extra = [ShelterFamily.Sleeping.Value],
                        Until = _ =>
                        {
                            live ??= TryOpen(path, cancellationToken);
                            return live is not null && Safe(() => FindSuiteMove(live, pawn, cancellationToken)) is { Standard: > 0 };
                        },
                    },
                    Audit = async (h, rep, ct) =>
                    {
                        live?.Dispose();
                        live = null;
                        using var journal = await SleepingBedrooms.OpenReopenedAsync(path, ct).ConfigureAwait(false);
                        move = FindSuiteMove(journal, pawn, ct) ?? throw new AcceptanceException($"the Greedy colonist {pawn} was never moved into a suite");
                        if (move.Standard == 0)
                        {
                            throw new AcceptanceException("no standard bedroom-wing shell completed before the suite move");
                        }

                        var inner = move.Room.Region.Bounds();
                        rep["suite_move"] = new JsonObject
                        {
                            ["pawn"] = pawn,
                            ["bed"] = move.Assign.Thing,
                            ["previous"] = move.Assign.Previous.Id,
                            ["suite"] = Bounds(inner),
                            ["standard_shells"] = move.Standard,
                        };
                        await OwnsBedAsync(h, s.Identity, pawn, move.Assign.Thing, "after the move", ct).ConfigureAwait(false);
                    },
                },
                cancellationToken).ConfigureAwait(false);

            // Phase two: the title raises the target; a second suite is planned and shelled, and the first one is left as it is.
            var first = move!;
            _ = await SustainedFood.ObserveAsync(
                ObservedSession.Of(s),
                new SustainedObservation
                {
                    Watch = new WatchConfig
                    {
                        Watch = TimeSpan.FromMinutes(8),
                        Extra = [ShelterFamily.Sleeping.Value],
                        Until = _ =>
                        {
                            live ??= TryOpen(path, cancellationToken);
                            return live is not null && Safe(() => NewSuiteShelled(live, first.Room, cancellationToken)) is not null;
                        },
                    },
                    Prepare = async (h, rep, ct) =>
                    {
                        var granted = await h.CallAsync("suite-title", SuiteTitleOp, new JsonObject { ["pawn"] = pawn, ["title"] = SuiteTitle }, ct).ConfigureAwait(false);
                        rep["title"] = granted.DeepClone();
                        if (granted["title"].AsString() != SuiteTitle)
                        {
                            throw new AcceptanceException($"fixture did not grant {SuiteTitle}: {granted.ToJson()}");
                        }
                    },
                    Audit = async (_, rep, ct) =>
                    {
                        live?.Dispose();
                        live = null;
                        using var journal = await SleepingBedrooms.OpenReopenedAsync(path, ct).ConfigureAwait(false);
                        AuditNewSuite(journal, rep, first, ct);
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            live?.Dispose();
        }
    }

    /// <summary>The journal when it opens; null while the service is still creating it (the watch retries).</summary>
    private static Journal? TryOpen(string path, CancellationToken cancellationToken)
    {
        try
        {
            return Journal.Open(path, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A watch predicate's journal read: a failed read is "not yet" (Go <c>err == nil &amp;&amp; ok</c>).</summary>
    private static T? Safe<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string Bounds(Rectangle r) => string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Z} {r.Width}x{r.Height}");

    /// <summary>Finds a suite other than <paramref name="first"/> whose shell plan completed.</summary>
    private static PlannedRoom? NewSuiteShelled(Journal journal, PlannedRoom first, CancellationToken cancellationToken)
    {
        if (PlanOf(journal, cancellationToken) is not { } layout)
        {
            return null;
        }

        foreach (var plan in History(journal, cancellationToken))
        {
            if (ShellMethod().Match(plan.Method.Value) is not { Success: true } shell)
            {
                continue;
            }

            var id = int.Parse(shell.Groups[1].Value, CultureInfo.InvariantCulture);
            if (id == first.Id)
            {
                continue;
            }

            if (RoomWithId(layout, PolicyFunctions.PlannedSuite, id) is { } room && Completed(plan))
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the journal and the plan: a second suite was planned and shelled, the first suite's plan row is exactly as
    /// it was when the colonist moved in, and nothing deepened a suite shell.
    /// </summary>
    private static void AuditNewSuite(Journal journal, Report report, SuiteMove first, CancellationToken cancellationToken)
    {
        var second = NewSuiteShelled(journal, first.Room, cancellationToken)
            ?? throw new AcceptanceException("the title raised the colonist's target but no new suite was shelled");
        var review = journal.LoadRounds(cancellationToken);
        var layout = journal.LayoutPlan(review.Snapshot, review.Tick, cancellationToken)?.Plan;
        var firstBounds = first.Room.Region.Bounds();
        var kept = layout is null ? null : RoomWithId(layout, PolicyFunctions.PlannedSuite, first.Room.Id);
        if (kept is null || !kept.Same(first.Room))
        {
            throw new AcceptanceException($"the first suite {Bounds(firstBounds)} changed or left the plan (now {(kept is null ? "absent" : Bounds(kept.Region.Bounds()))})");
        }

        foreach (var plan in History(journal, cancellationToken))
        {
            if (plan.Method.Value.StartsWith("bedroom-grow-", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"a suite was grown in place: {plan.Method.Value}");
            }
        }

        report["new_suite"] = Bounds(second.Region.Bounds());
    }

    /// <summary>Checks natively that the pawn owns the bed.</summary>
    private static async Task OwnsBedAsync(Harness h, JsonObject identity, string pawn, string bed, string when, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "pawn",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["ids"] = new JsonArray(pawn) },
                ["details"] = new JsonObject(),
            },
            cancellationToken).ConfigureAwait(false);
        var observed = Harness.Outcome(reply, "observed").Value;
        var rows = observed["pawns"].AsSlice();
        if (rows.Count != 1)
        {
            throw new AcceptanceException($"pawn read: {observed.ToJson()}");
        }

        var owned = rows[0].AsMap()?["ownedBed"].RefId() ?? "";
        if (owned != bed)
        {
            throw new AcceptanceException($"{when}: colonist {pawn} owns \"{owned}\", not the suite bed \"{bed}\"");
        }
    }
}

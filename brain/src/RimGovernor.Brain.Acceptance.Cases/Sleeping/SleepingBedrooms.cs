using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The individual-bedroom case (Go <c>sleeping/bedrooms.go</c>). sleeping/bedrooms starts from the layout/grid fixture
/// (tribal baseline, Stonecutting finished so the tier reads Masonry, a fixture hut with sleeping spots and stone
/// blocks beside it): the sleeping planner first beds everyone in the hut, then orders the layout plan's first
/// bedroom-wing room (the fixture raises its ring as soon as it is ordered), stages a bed in it and moves a colonist's
/// ownership there. The case asserts that move natively: the colonist owns the new bed, and the shelter bed they left
/// still stands as a spare.
/// </summary>
internal static class SleepingBedrooms
{
    /// <summary>Starts a bedroom room reconcile's methods; other bedroom- and sleeping- methods stage and assign beds (plans are recognised by method, not plan id).</summary>
    internal const string BedroomShellPrefix = "bedroom-shell-";

    /// <summary>Reports a bed staging or assignment method.</summary>
    internal static bool SleepingBedMethod(string method) =>
        (method.StartsWith("bedroom-", StringComparison.Ordinal) || method.StartsWith("sleeping-", StringComparison.Ordinal))
        && !method.StartsWith(BedroomShellPrefix, StringComparison.Ordinal)
        && !method.StartsWith("sleeping-shell", StringComparison.Ordinal);

    internal static AcceptanceCase Case() => new()
    {
        Name = "sleeping/bedrooms",
        Scope = "Issue #786: on the tribal " + SustainedFoodCase.BaselineSave + " colony at Masonry with a fixture hut, once every colonist "
            + "owns a bed the sleeping planner builds the layout plan's first bedroom-wing room and moves a colonist from the shelter "
            + "into it: the native pawn read shows the colonist owning the bedroom bed, and the shelter bed they left still "
            + "stands as a spare. A snapshot test cannot cover it: the move is proven by native bed ownership after real "
            + "construction encloses the planned room.",
        Start = new FixtureStart
        {
            Op = "test/layout_grid_prepare",
            ArgsFrom = StarterSite.DefaultArgsAsync,
            Args = new JsonObject { ["sleepingSpots"] = 8, ["stoneBlocks"] = 400, ["builders"] = true },
            On = new SaveStart { Name = SustainedFoodCase.BaselineSave },
        },
        Keep = ["Food"],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "sleeping"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "sleeping-bedrooms",
        },
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Reason = "one watch: the fixture raises the shell the planner orders, then the furnished, assigned bedroom",
        Run = RunAsync,
    };

    /// <summary>Reports two completed one-action sleeping plans after a bedroom shell plan: the bed staged in the bedroom, then the assignment.</summary>
    internal static bool Moved(JsonObject? sample)
    {
        var plans = sample?["plans"].Rows() ?? [];
        var retired = sample?["retired_plans"].Rows() ?? [];
        var shell = false;
        var done = 0;
        foreach (var plan in plans.Concat(retired))
        {
            var method = plan["method"].AsString();
            var actions = (int)plan["actions"].AsNumber();
            var completed = plan["stages"].Member("completed").AsNumber();
            if (method.StartsWith(BedroomShellPrefix, StringComparison.Ordinal) && actions > 0)
            {
                shell = true;
            }

            if (shell && SleepingBedMethod(method) && actions == 1 && completed == 1)
            {
                done++;
            }
        }

        return done >= 2;
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        report["fixture"] = s.Prepared?.DeepClone();
        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(2),
                    Extra = [ShelterFamily.Sleeping.Value],
                    Until = sample => Moved(sample[ShelterFamily.Sleeping.Value].AsMap()),
                },
                Audit = (h, rep, ct) => AuditAsync(s, h, rep, ct),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AuditAsync(ISession s, Harness h, Report report, CancellationToken cancellationToken)
    {
        Assign? move = null;
        using (var journal = await OpenReopenedAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false))
        {
            // The move retires once the colonist sleeps in the bed, so read history (newest first), not the active catalog.
            var plans = journal.PlanHistoryWithMethods(256, ["bedroom-*", "sleeping-*"], cancellationToken).Reverse();
            var shell = false;
            foreach (var plan in plans)
            {
                var method = plan.Method.Value;
                if (method.StartsWith(BedroomShellPrefix, StringComparison.Ordinal))
                {
                    shell = true;
                    continue;
                }

                if (!shell || !SleepingBedMethod(method))
                {
                    continue;
                }

                foreach (var a in plan.Spec.Actions)
                {
                    if (a.Assign is { } assign && assign.Previous.Id.Length != 0)
                    {
                        move = assign;
                    }
                }
            }
        }

        if (move is null)
        {
            throw new AcceptanceException("no bed assignment moving a colonist out of an owned bed followed a bedroom shell");
        }

        var previous = move.Previous.Id;
        report["move"] = new JsonObject { ["pawn"] = move.Pawn.Value, ["bed"] = move.Thing, ["previous"] = previous };
        var identity = s.Identity;
        var reply = await h.WireAsync(
            "pawn",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["ids"] = new JsonArray(move.Pawn.Value) },
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
        if (owned != move.Thing)
        {
            throw new AcceptanceException($"colonist {move.Pawn.Value} owns \"{owned}\", not the bedroom bed \"{move.Thing}\"");
        }

        reply = await h.WireAsync(
            "spare",
            "observations_list_buildings",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["ids"] = new JsonArray(previous),
            },
            cancellationToken).ConfigureAwait(false);
        observed = Harness.Outcome(reply, "observed").Value;
        if (observed["buildings"].AsSlice().Count != 1)
        {
            throw new AcceptanceException($"the shelter bed {previous} no longer stands as a spare: {observed.ToJson()}");
        }
    }

    /// <summary>Reopens the stopped service's journal (Go <c>store.Open</c> after the run).</summary>
    internal static async Task<Journal> OpenReopenedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Journal.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AcceptanceException($"reopen journal: {e.Message}", e);
        }
    }
}

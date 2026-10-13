using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>mood/relief</c> (Go <c>relief.go</c>): EnsureMood relief through the GiveJobIntent relieve_need on Actions/Apply,
/// including the resend, the player-forced current job and the mental-break refusal, with the joy recovery observed
/// through native ticks.
/// </summary>
internal static class Relief
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "mood/relief",
        Scope = "EnsureMood relief through the GiveJobIntent relieve_need on Actions/Apply: an actual JobGiver_GetJoy "
            + "job started for a deficient pawn, a resend that applies again without a second job, relief admitted over a "
            + "player-forced current job (#474), a refusal for a pawn in a mental break, and real need recovery observed via native ticks.",
        Start = new LabStart(),
        Keep = ["Joy", "Mood"],
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static double Joy(JsonObject row) => row["needs"].AsMap()?["joy"].Strict() ?? 0;

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Joy and Mood stay live: the fixture seeds a joy deficit and the assertion is that native recreation recovers it.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> PawnRowAsync(string label, string pawnId)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_pawns",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["filter"] = new JsonObject { ["ids"] = NativeReads.Strings(pawnId), ["includeDead"] = true },
                    ["details"] = new JsonObject { ["needs"] = true },
                },
                cancellationToken).ConfigureAwait(false);
            var observed = Harness.Outcome(reply, "observed").Value;
            var rows = observed["pawns"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected exactly one observed pawn, got {observed.ToJson()}");
            }

            var row = rows[0].AsMap() ?? [];
            if (row["pawn"].AsMap()?["id"].AsString() != pawnId)
            {
                throw new AcceptanceException($"{label}: unexpected pawn row: {row.ToJson()}");
            }

            return row;
        }

        async Task<string> SetupAsync(string label, string scenario)
        {
            var prepared = await h.CallAsync(label, "test/mood_setup", new JsonObject { ["scenario"] = scenario }, cancellationToken).ConfigureAwait(false);
            if (!prepared["success"].Flag())
            {
                throw new AcceptanceException($"{label}: mood_setup refused: {prepared.ToJson()}");
            }

            var pawnId = prepared["pawn"].AsString();
            return pawnId.Length == 0
                ? throw new AcceptanceException($"{label}: mood_setup: missing fixture pawn id: {prepared.ToJson()}")
                : pawnId;
        }

        Task<JsonObject> RelieveAsync(string label, string pawnId) =>
            ApplyIntents.ApplyOneAsync(
                h,
                label,
                identity,
                label,
                new JsonObject
                {
                    ["giveJob"] = new JsonObject
                    {
                        ["pawn"] = new JsonObject { ["id"] = pawnId },
                        ["options"] = new JsonObject { ["relieveNeed"] = "NEED_JOY" },
                    },
                },
                cancellationToken);

        static void Issued(string label, string pawnId, JsonObject result, bool want)
        {
            var job = ApplyIntents.AppliedJob(label, result);
            if (job["issued"].Flag() != want || job["pawnId"].AsString() != pawnId)
            {
                throw new AcceptanceException($"{label}: expected issued={(want ? "true" : "false")} for {pawnId}, got {job.ToJson()}");
            }
        }

        // --- Positive path: joy relief ---
        var pawn = await SetupAsync("setup-joy", "joy").ConfigureAwait(false);
        report["fixture_pawn"] = pawn;
        var before = await PawnRowAsync("target-before", pawn).ConfigureAwait(false);
        report["needs_joy_before"] = Joy(before);
        if (Joy(before) >= 0.5)
        {
            throw new AcceptanceException($"target-before: expected a deficient joy need, got {NativeReads.Fmt(Joy(before))}");
        }

        var result = await RelieveAsync("mood-relief-joy", pawn).ConfigureAwait(false);
        Issued("mood-relief-joy", pawn, result, true);

        // A resend while the recreation job runs applies again without a second job.
        var again = await RelieveAsync("mood-relief-joy-again", pawn).ConfigureAwait(false);
        Issued("mood-relief-joy-again", pawn, again, false);

        // Observe: run real game time forward until the joy need is actually observed at native's own 0.5 recovery
        // threshold; the applied job alone never proves recovery.
        JsonObject? after = null;
        _ = await NativeReads.RunUntilAsync(
            s,
            "observe-joy",
            Wait.TicksPerDay * 2,
            async ct =>
            {
                var row = await PawnRowAsync("observe-joy", pawn).ConfigureAwait(false);
                after = row;
                return (Joy(row).ToString("F2", CultureInfo.InvariantCulture), Joy(row) >= 0.5);
            },
            quiet: true,
            cancellationToken).ConfigureAwait(false);
        report["needs_joy_after"] = Joy(after!);

        // --- Ordered work: a player-forced current job is interruption evidence, not an eligibility veto, so relief
        // is applied over it. ---
        var forcedPawn = await SetupAsync("setup-forced", "forced").ConfigureAwait(false);
        var forcedRow = await PawnRowAsync("target-forced", forcedPawn).ConfigureAwait(false);
        var forcedJob = forcedRow["job"].AsMap();
        if (forcedJob?["playerForced"].Flag() != true)
        {
            throw new AcceptanceException($"target-forced: expected a player-forced current job, got {forcedJob.ToJson()}");
        }

        var forced = await RelieveAsync("mood-relief-forced", forcedPawn).ConfigureAwait(false);
        Issued("mood-relief-forced", forcedPawn, forced, true);
        report["ordered_work_pawn"] = forcedPawn;

        // --- Negative: a pawn in a mental break is refused. ---
        var mentalPawn = await SetupAsync("setup-mental", "mental").ConfigureAwait(false);
        var mental = await RelieveAsync("mood-relief-mental", mentalPawn).ConfigureAwait(false);
        ApplyIntents.Refused("mood-relief-mental", mental, "mental break");
        report["negative_mental_pawn"] = mentalPawn;
    }
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>lab/training-shooting</c> (Go <c>training.go</c>).</summary>
internal static class LabTraining
{
    // The native daily budget (RangeTraining.DailyXpBudget): one session never pays more than a day's worth.
    private const int MaxXp = 3000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "lab/training-shooting",
        Scope = "Native training job (#2610, open air since #2686): test/training_prepare stands one range column in open air (a stand, a dummy "
            + "8 cells across, no walls or partitions) and leaves a low-skill colonist whose only work is the RimGovernorTraining work type; with no "
            + "order from Go the vanilla work scan offers the stand, the colonist fires the unlocked practice weapon at the dummy, direct Learn raises "
            + "Shooting without touching xpSinceMidnight, the colonist's own weapon is back in hand with no practice weapon left anywhere, and a "
            + "sentinel wall off the line is unharmed. A Go snapshot test cannot see the work giver, the job driver, the weapon swap or the shots.",
        Start = new LabStart { Colonists = 3 },
        RequiredOps = ["test/training_prepare", "test/training_inspect"],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>
    /// The XP a skill gained since <paramref name="startLevel"/> at zero XP, on vanilla's curve below level 9 (a level
    /// costs 1000 x (level + 1)).
    /// </summary>
    internal static double Gained(int startLevel, int level, double xp)
    {
        var total = xp;
        for (var i = startLevel; i < level; i++)
        {
            total += 1000.0 * (i + 1);
        }

        return total;
    }

    /// <summary>Whether a job name is the native training job.</summary>
    internal static bool IsTrainingJob(JsonNode? job) => job.AsString().StartsWith("RimGovernor_Train", StringComparison.Ordinal);

    /// <summary>Whether a def is one of the practice weapons.</summary>
    internal static bool IsPracticeWeapon(string name) => name == "Bow_Training" || name.StartsWith("Gun_Practice", StringComparison.Ordinal);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var (cx, cz) = CaseSupport.Center(s);
        var prepared = await h.CallAsync("training-prepare", "test/training_prepare", new JsonObject { ["x"] = cx, ["z"] = cz }, cancellationToken).ConfigureAwait(false);
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"training_prepare: {prepared.ToJson()}");
        }

        var args = new JsonObject
        {
            ["pawnId"] = prepared["pawn"]?.DeepClone(),
            ["dummyId"] = prepared["dummy"]?.DeepClone(),
            ["sentinels"] = prepared["sentinels"]?.DeepClone(),
        };
        var real = prepared["weapon"].AsString();
        JsonObject last = [];
        _ = await RunAt.RunUntilAsync(
            h,
            "training",
            9000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var got = await h.CallAsync("training-inspect", "test/training_inspect", (JsonObject)args.DeepClone(), ct).ConfigureAwait(false);
                last = got;
                var gained = Gained(0, (int)got["shootingLevel"].AsNumber(), got["shootingXp"].AsNumber());
                var idle = !IsTrainingJob(got["job"]);
                return (Wait.Signature(gained, got["job"].AsString()), gained > 0 && idle);
            },
            cancellationToken).ConfigureAwait(false);
        s.Report["training"] = last;
        var total = Gained(0, (int)last["shootingLevel"].AsNumber(), last["shootingXp"].AsNumber());
        if (total <= 0 || total > MaxXp)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"shooting xp gained {total} outside (0, {MaxXp}]: {last.ToJson()}"));
        }

        if (last["shootingMidnightXp"].AsNumber() != 0)
        {
            throw new AcceptanceException($"direct Learn counted toward the daily soft cap: {last.ToJson()}");
        }

        if (last["shotsFired"].AsNumber() < 1)
        {
            throw new AcceptanceException($"no shot was fired: {last.ToJson()}");
        }

        // The shooter mends the dummy at the end of the session (#2687): it is whole again, not destroyed or worn.
        var hp = last["dummyHp"].AsNumber();
        if (hp <= 0 || hp != prepared["dummyMax"].AsNumber())
        {
            throw new AcceptanceException($"drilled dummy not mended to full: {last.ToJson()}");
        }

        // Nobody else drilled within chat range (#2710): no "trained with" thought.
        if (last["trainedWith"].AsBool() == true)
        {
            throw new AcceptanceException($"lone shooter got the trained-with thought: {last.ToJson()}");
        }

        if (last["primary"].AsString() != real)
        {
            throw new AcceptanceException($"real weapon {real} not restored to hand: {last.ToJson()}");
        }

        if (last["practiceOnMap"].AsNumber() != 0)
        {
            throw new AcceptanceException($"practice weapon left on the map: {last.ToJson()}");
        }

        foreach (var def in last["held"].AsSlice())
        {
            if (IsPracticeWeapon(def.AsString()))
            {
                throw new AcceptanceException($"practice weapon still held: {last.ToJson()}");
            }
        }

        if (last["sentinelHp"].AsNumber() != last["sentinelMax"].AsNumber())
        {
            throw new AcceptanceException($"damage outside the range: {last.ToJson()}");
        }
    }
}

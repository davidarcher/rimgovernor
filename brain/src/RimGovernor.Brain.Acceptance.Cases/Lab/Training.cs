using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/training-shooting</c>, and the range column every training case stages. test/training_prepare stays a bespoke
/// op: it picks violence-capable shooters, gives them a minor Shooting passion, the practice work type only and a fresh
/// autopistol, strips an earlier trained-with memory and drafts a mid-lane bystander, none of which the stage primitives
/// express. test/training_inspect is the recorder of one shooter, test/training_act the fault hook.
/// </summary>
internal static class LabTraining
{
    internal const string PrepareTool = "test/training_prepare";
    internal const string InspectTool = "test/training_inspect";
    internal const string ActTool = "test/training_act";

    // The native daily budget (RangeTraining.DailyXpBudget): one session never pays more than a day's worth.
    private const int MaxXp = 3000;

    // The column's own site: the stand at (8, 2), its dummy 8 cells north, sentinels 8 cells to either side, shooters
    // two cells south of the stand.
    internal static readonly Fx.StageSpec Site = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 17,
            Height = 11,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
        },
    };

    internal static TypedCase Case() => new()
    {
        Name = "lab/training-shooting",
        Scope = "Native training job (#2610, open air since #2686): one range column in open air (a stand, a dummy 8 cells "
            + "across, no walls or partitions) and a low-skill colonist whose only work is the RimGovernorTraining work type; "
            + "the vanilla work scan offers the stand, the colonist fires the unlocked practice weapon at the dummy, direct Learn "
            + "raises Shooting without touching xpSinceMidnight and matches the learning projection, the dummy is mended, the "
            + "colonist's own weapon is back in hand with no practice weapon left anywhere, and a sentinel wall off the line is "
            + "unharmed. A unit test cannot see the work giver, the job driver, the weapon swap or the shots.",
        Ticks = 9000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = Site,
        Ops = [PrepareTool, InspectTool],
        QuietWorld = true,
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
    internal static bool IsTrainingJob(string job) => job.StartsWith("RimGovernor_Train", StringComparison.Ordinal);

    /// <summary>Whether a def is one of the practice weapons.</summary>
    internal static bool IsPracticeWeapon(string name) => name == "Bow_Training" || name.StartsWith("Gun_Practice", StringComparison.Ordinal);

    /// <summary>Fails unless the real weapon is back in hand and no practice weapon is held or left on the map.</summary>
    internal static void RequireRestored(Fx.TrainingRead read, string weapon, string after)
    {
        if (read.Primary != weapon)
        {
            throw new AcceptanceException($"real weapon {weapon} not restored to hand {after}: holds \"{read.Primary}\"");
        }

        if (read.PracticeOnMap != 0)
        {
            throw new AcceptanceException($"{read.PracticeOnMap} practice weapons left on the map {after}");
        }

        if (read.Held.FirstOrDefault(IsPracticeWeapon) is { } held)
        {
            throw new AcceptanceException($"practice weapon {held} still held {after}");
        }
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new TrainingOptions(), cancellationToken).ConfigureAwait(false);
        var (last, _) = await t.UntilTrainedWithAsync(0, cancellationToken).ConfigureAwait(false);
        s.Record("training", ServedOps.Json(last));
        if (!last.LearningReadOnly || !last.ProjectionKnown || last.ProjectedLevel != last.ShootingLevel || Math.Abs(last.ProjectedXp - last.ShootingXp) > .1)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"native learning read/projection diverged: read-only {last.LearningReadOnly}, projected {last.ProjectedLevel}/{last.ProjectedXp} (known {last.ProjectionKnown}), actual {last.ShootingLevel}/{last.ShootingXp}"));
        }

        var total = Gained(0, last.ShootingLevel, last.ShootingXp);
        if (total <= 0 || total > MaxXp)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"shooting xp gained {total} outside (0, {MaxXp}]"));
        }

        if (last.ShootingMidnightXp != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"direct Learn counted {last.ShootingMidnightXp} toward the daily soft cap"));
        }

        if (last.ShotsFired < 1)
        {
            throw new AcceptanceException("no shot was fired");
        }

        // The shooter mends the dummy at the end of the session (#2687): it is whole again, not destroyed or worn.
        if (last.DummyHp <= 0 || last.DummyHp != t.Prepared.DummyMax)
        {
            throw new AcceptanceException($"drilled dummy at {last.DummyHp} of {t.Prepared.DummyMax} hit points, not mended to full");
        }

        // Nobody else drilled within chat range (#2710): no "trained with" thought.
        if (last.TrainedWith)
        {
            throw new AcceptanceException("lone shooter got the trained-with thought");
        }

        RequireRestored(last, t.Prepared.Weapon, "after the session");
        if (last.SentinelHp != last.SentinelMax)
        {
            throw new AcceptanceException($"damage outside the range: sentinels at {last.SentinelHp} of {last.SentinelMax}");
        }
    }
}

/// <summary>The range column's options (test/training_prepare): a starting Shooting level, a second column and a mid-lane bystander.</summary>
internal sealed record TrainingOptions(int Level = 0, bool Neighbour = false, bool Bystander = false);

/// <summary>A staged range column and the shooter it reads back.</summary>
internal sealed class TrainingRun
{
    private sealed record Query(string Pawn, string Dummy, string Sentinels, string Other, string Bystander);

    private sealed record PrepareArgs(Fx.StageCell At, TrainingOptions Options);

    private static readonly FixtureOp<PrepareArgs, Fx.TrainingPrepared> PrepareOp = new(
        LabTraining.PrepareTool,
        p => ServedOps.Args(("x", p.At.X), ("z", p.At.Z), ("level", p.Options.Level), ("neighbour", p.Options.Neighbour), ("bystander", p.Options.Bystander)),
        ServedOps.Reply<Fx.TrainingPrepared>(LabTraining.PrepareTool));

    private static readonly FixtureOp<Query, Fx.TrainingRead> InspectOp = new(
        LabTraining.InspectTool,
        q => ServedOps.Args(("pawnId", q.Pawn), ("dummyId", q.Dummy), ("sentinels", q.Sentinels), ("otherId", q.Other), ("bystanderId", q.Bystander)),
        ServedOps.Reply<Fx.TrainingRead>(LabTraining.InspectTool));

    private readonly TypedSession _s;
    private readonly Query _query;

    private TrainingRun(TypedSession s, Fx.TrainingPrepared prepared, Query query)
    {
        _s = s;
        Prepared = prepared;
        _query = query;
    }

    internal Fx.TrainingPrepared Prepared { get; }

    /// <summary>Stands the column at the staged site's (8, 2).</summary>
    internal static async Task<TrainingRun> PrepareAsync(TypedSession s, TrainingOptions options, CancellationToken cancellationToken)
    {
        var site = s.Staged?.Site ?? throw new AcceptanceException("test/stage replied no site");
        var prepared = await s.FixtureAsync("training-prepare", PrepareOp, new PrepareArgs(new Fx.StageCell(site.X + 8, site.Z + 2), options), cancellationToken).ConfigureAwait(false);
        return new TrainingRun(s, prepared, new Query(prepared.Pawn, prepared.Dummy, prepared.Sentinels, prepared.Neighbour, prepared.Bystander));
    }

    /// <summary>The run read back through the second column's shooter and dummy.</summary>
    internal TrainingRun Neighbour() => new(_s, Prepared, _query with { Pawn = Prepared.Neighbour, Dummy = Prepared.NeighbourDummy });

    internal Task<Fx.TrainingRead> InspectAsync(CancellationToken cancellationToken) =>
        _s.FixtureAsync("training-inspect", InspectOp, _query, cancellationToken);

    /// <summary>Runs until the shooter has gained XP above <paramref name="startLevel"/> and is idle again; also returns the practice weapon seen in hand while the job ran.</summary>
    internal async Task<(Fx.TrainingRead Last, string Weapon)> UntilTrainedWithAsync(int startLevel, CancellationToken cancellationToken)
    {
        Fx.TrainingRead? last = null;
        var weapon = "";
        _ = await _s.RunUntilAsync("training", 9000, async ct =>
        {
            var got = await InspectAsync(ct).ConfigureAwait(false);
            last = got;
            if (LabTraining.IsPracticeWeapon(got.Primary))
            {
                weapon = got.Primary;
            }

            var gained = LabTraining.Gained(startLevel, got.ShootingLevel, got.ShootingXp);
            return (Wait.Signature(gained, got.Job), gained > 0 && !LabTraining.IsTrainingJob(got.Job));
        }, cancellationToken).ConfigureAwait(false);
        return (last!, weapon);
    }

    /// <summary>Runs <paramref name="ticks"/> of game time and fails when the shooter drilled in them.</summary>
    internal async Task IdleForAsync(int ticks, CancellationToken cancellationToken)
    {
        Fx.TrainingRead? first = null;
        Fx.TrainingRead? last = null;
        _ = await _s.RunUntilAsync("training-idle", ticks + 600, async ct =>
        {
            var got = await InspectAsync(ct).ConfigureAwait(false);
            if (LabTraining.IsTrainingJob(got.Job))
            {
                throw new AcceptanceException($"drilled at the ceiling: job {got.Job} at tick {got.Tick}");
            }

            first ??= got;
            last = got;
            return (Wait.Signature(got.Tick), got.Tick - first.Tick >= ticks);
        }, cancellationToken).ConfigureAwait(false);
        if (last!.ShotsFired != first!.ShotsFired)
        {
            throw new AcceptanceException($"trained at the ceiling: {last.ShotsFired - first.ShotsFired} shots in {ticks} ticks");
        }
    }
}

using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The open-air range's verification cases (#2688): each stands the same column as lab/training-shooting plus one option
/// and asserts what only the native job, the verb and vanilla's social and fire rules can show.
/// </summary>
internal static class LabTrainingRange
{
    private static readonly string[] Ops = [LabTraining.PrepareTool, LabTraining.InspectTool, LabTraining.ActTool, FixtureStage.Tool];

    private sealed record Act(string Action, string Pawn = "", string Other = "", string Thing = "", int Level = 0);

    private static readonly FixtureOp<Act, Fx.FixtureDone> Disturb = new(LabTraining.ActTool, ArgsOf, ServedOps.Done(LabTraining.ActTool));

    private static readonly FixtureOp<Act, Fx.TrainingChat> Chat = new(LabTraining.ActTool, ArgsOf, ServedOps.Reply<Fx.TrainingChat>(LabTraining.ActTool));

    // Each interrupted session: the disturbance, its option and its Scope.
    private static readonly (string Name, string Action, string Scope)[] Interrupts =
    [
        ("lab/training-raid", "draft", "A raid drafts the shooter mid-drill (#2688): the finish action restores the real weapon and leaves no practice weapon "
            + "anywhere, and the interrupted session earns no trained-with thought although a neighbour drills beside it (#2710)."),
        ("lab/training-fire", "fire", "A fire on the open-air dummy mid-drill (#2688): whatever ends the session, the real weapon is restored and no practice weapon leaks."),
    ];

    internal static IEnumerable<TypedCase> Cases()
    {
        yield return Lab(
            "lab/training-midlane",
            "A drafted colonist standing between the stand and the dummy (#2688) takes no injury from the drill and the shooter still "
            + "completes its session: the verb is cast with friendly fire prevented, so an open lane needs no partition. A unit test cannot see the shots.",
            9000,
            MidlaneAsync);
        yield return Lab(
            "lab/training-chat",
            "Two colonists drilling at adjacent stands (#2688) sit inside vanilla's 6-cell line-of-sight chat rule and can chat while the "
            + "drill job runs: the drill does not bar social interaction.",
            9000,
            ChatAsync);
        yield return Lab(
            "lab/training-tiers",
            "Tier ceilings end to end (#2688): a Shooting 8 and a Shooting 10 colonist are both idle at the bow's ceiling; finishing "
            + "Gunsmithing lets the 8 train past 8 with the practice rifle while the 10 stays idle; finishing ChargedShot lets the 10 train past 10 with the "
            + "practice pulse rifle (separate colonists because the 3,000 XP daily budget would stop one). A unit test cannot see the native tier read, ceiling and weapon swap.",
            21000,
            TiersAsync);
        yield return Lab(
            "lab/training-company",
            "Two shooters at adjacent stands (#2710) each finish a completed session and each holds the shared trained-with thought, "
            + "which a lone shooter does not (lab/training-shooting). A unit test cannot see the native finish action, the 6-cell "
            + "line-of-sight company read or the memory.",
            12000,
            CompanyAsync);
        foreach (var (name, action, scope) in Interrupts)
        {
            yield return Lab(name, scope, 18000, (s, ct) => InterruptAsync(s, action, ct));
        }
    }

    private static JsonObject ArgsOf(Act a) =>
        ServedOps.Args(("action", a.Action), ("pawnId", a.Pawn), ("otherId", a.Other), ("thingId", a.Thing), ("level", a.Level));

    private static TypedCase Lab(string name, string scope, int ticks, Func<TypedSession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Scope = scope,
        Ticks = ticks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = LabTraining.Site,
        Ops = Ops,
        QuietWorld = true,
        Run = run,
    };

    private static async Task MidlaneAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new TrainingOptions(Bystander: true), cancellationToken).ConfigureAwait(false);
        var (last, _) = await t.UntilTrainedWithAsync(0, cancellationToken).ConfigureAwait(false);
        s.Record("training", ServedOps.Json(last));
        if (last.ShotsFired < 1)
        {
            throw new AcceptanceException("mid-lane pawn stalled the shooter: no shot fired");
        }

        if (last.BystanderHurt != 0 || last.BystanderHealth < 1)
        {
            throw new AcceptanceException($"mid-lane pawn was hit by a drill arrow: {last.BystanderHurt} injuries, health {last.BystanderHealth}");
        }
    }

    private static async Task ChatAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new TrainingOptions(Neighbour: true), cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("training-both", 9000, async ct =>
        {
            var got = await t.InspectAsync(ct).ConfigureAwait(false);
            return (Wait.Signature(got.Job, got.OtherJob), LabTraining.IsTrainingJob(got.Job) && LabTraining.IsTrainingJob(got.OtherJob));
        }, cancellationToken).ConfigureAwait(false);
        var chat = await s.FixtureAsync("training-chat", Chat, new Act("chat", Pawn: t.Prepared.Pawn, Other: t.Prepared.Neighbour), cancellationToken).ConfigureAwait(false);
        s.Record("chat", ServedOps.Json(chat));
        if (!chat.Good)
        {
            throw new AcceptanceException("adjacent drillers are outside the chat rule");
        }

        if (!chat.Chatted)
        {
            throw new AcceptanceException("adjacent driller could not chat");
        }
    }

    private static async Task TiersAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var low = await TrainingRun.PrepareAsync(s, new TrainingOptions(Level: 8, Neighbour: true), cancellationToken).ConfigureAwait(false);
        var high = low.Neighbour();
        _ = await s.FixtureAsync("training-level", Disturb, new Act("level", Pawn: low.Prepared.Neighbour, Level: 10), cancellationToken).ConfigureAwait(false);
        foreach (var (who, what) in new[] { (low, "level 8 at the bow ceiling"), (high, "level 10 below Gunsmithing") })
        {
            try
            {
                await who.IdleForAsync(600, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{what}: {e.Message}", e);
            }
        }

        foreach (var (who, research, weapon, level) in new[] { (low, "Gunsmithing", "Gun_PracticeRifle", 8), (high, "ChargedShot", "Gun_PracticePulseRifle", 10) })
        {
            _ = await s.StageAsync("training-research", new Fx.StageSpec { Research = [new() { Def = research }] }, cancellationToken).ConfigureAwait(false);
            Fx.TrainingRead last;
            string seen;
            try
            {
                (last, seen) = await who.UntilTrainedWithAsync(level, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"after {research}: {e.Message}", e);
            }

            s.Record("after_" + research, ServedOps.Json(last));
            if (seen != weapon)
            {
                throw new AcceptanceException($"after {research} trained with \"{seen}\", want {weapon}");
            }

            if (ReferenceEquals(who, low))
            {
                var got = await high.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (got.ShotsFired != 0)
                {
                    throw new AcceptanceException($"level 10 trained under the rifle ceiling: {got.ShotsFired} shots");
                }
            }
        }
    }

    private static async Task CompanyAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new TrainingOptions(Neighbour: true), cancellationToken).ConfigureAwait(false);
        var second = t.Neighbour();
        Fx.TrainingRead? first = null;
        Fx.TrainingRead? other = null;
        static bool Done(Fx.TrainingRead got) => LabTraining.Gained(0, got.ShootingLevel, got.ShootingXp) > 0 && !LabTraining.IsTrainingJob(got.Job);
        _ = await s.RunUntilAsync("training-both-done", 12000, async ct =>
        {
            first = await t.InspectAsync(ct).ConfigureAwait(false);
            other = await second.InspectAsync(ct).ConfigureAwait(false);
            return (Wait.Signature(first.Job, other.Job), Done(first) && Done(other));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("company", new JsonArray(ServedOps.Json(first!), ServedOps.Json(other!)));
        if (!first!.TrainedWith || !other!.TrainedWith)
        {
            throw new AcceptanceException($"a shooter finished beside another without the trained-with thought: first {first.TrainedWith}, second {other!.TrainedWith}");
        }
    }

    private static async Task InterruptAsync(TypedSession s, string action, CancellationToken cancellationToken)
    {
        // A drafted shooter has a neighbour drilling beside it (#2710): company, yet an interrupted session earns no thought.
        var t = await TrainingRun.PrepareAsync(s, new TrainingOptions(Neighbour: action == "draft"), cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("training-start", 9000, async ct =>
        {
            var got = await t.InspectAsync(ct).ConfigureAwait(false);
            return (Wait.Signature(got.Job, got.Primary), LabTraining.IsTrainingJob(got.Job) && LabTraining.IsPracticeWeapon(got.Primary));
        }, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("training-" + action, Disturb, new Act(action, Pawn: t.Prepared.Pawn, Thing: t.Prepared.Dummy), cancellationToken).ConfigureAwait(false);

        // A drafted shooter ends the job at once; a burning dummy ends it by destruction or when the session completes.
        // Either way the hands are restored.
        Fx.TrainingRead? last = null;
        _ = await s.RunUntilAsync("training-ended", 9000, async ct =>
        {
            last = await t.InspectAsync(ct).ConfigureAwait(false);
            return (Wait.Signature(last.Job), !LabTraining.IsTrainingJob(last.Job));
        }, cancellationToken).ConfigureAwait(false);
        s.Record("after", ServedOps.Json(last!));
        LabTraining.RequireRestored(last!, t.Prepared.Weapon, "after " + action);
        if (action == "draft" && last!.TrainedWith)
        {
            throw new AcceptanceException("an interrupted session earned the trained-with thought");
        }
    }
}

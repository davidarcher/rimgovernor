using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The open-air range's verification cases (#2688; Go <c>training_range.go</c>). Each stages the same fixture column as
/// lab/training-shooting plus one option and asserts what only the native job, the verb and vanilla's social and fire
/// rules can show. They run in the nightly bulk tier.
/// </summary>
internal static class LabTrainingRange
{
    private static readonly string[] Ops = ["test/training_prepare", "test/training_inspect", "test/training_act"];

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return Lab(
            "lab/training-midlane",
            "A drafted colonist standing between the stand and the dummy (#2688) takes no injury from the drill and the shooter still "
            + "completes its session: the verb is cast with friendly fire prevented, so an open lane needs no partition. A Go snapshot test cannot see the shots.",
            MidlaneAsync);
        yield return Lab(
            "lab/training-chat",
            "Two colonists drilling at adjacent stands (#2688) sit inside vanilla's 6-cell line-of-sight chat rule and can chat while the "
            + "drill job runs: the drill does not bar social interaction.",
            ChatAsync);
        yield return Lab(
            "lab/training-tiers",
            "Tier ceilings end to end (#2688): a Shooting 8 and a Shooting 10 colonist are both idle at the bow's ceiling; finishing "
            + "Gunsmithing lets the 8 train past 8 with the practice rifle while the 10 stays idle; finishing ChargedShot lets the 10 train past 10 with the "
            + "practice pulse rifle (separate colonists because the 3,000 XP daily budget would stop one). The native tier read, ceiling and weapon swap cannot be seen from a Go snapshot.",
            TiersAsync);
        yield return Lab(
            "lab/training-company",
            "Two shooters at adjacent stands (#2710) each finish a completed session and each holds the shared trained-with thought, "
            + "which a lone shooter does not (lab/training-shooting). The native finish action, the 6-cell line-of-sight company read and the memory "
            + "cannot be seen from a Go snapshot.",
            CompanyAsync);
        foreach (var (name, scope, action) in new[]
        {
            ("lab/training-raid", "A raid drafts the shooter mid-drill (#2688): the finish action restores the real weapon and leaves no practice weapon anywhere.", "draft"),
            ("lab/training-fire", "A fire on the open-air dummy mid-drill (#2688): whatever ends the session, the real weapon is restored and no practice weapon leaks.", "fire"),
        })
        {
            var interrupt = action;
            yield return Lab(name, scope, (s, ct) => InterruptAsync(s, interrupt, ct));
        }
    }

    private static AcceptanceCase Lab(string name, string scope, Func<ISession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Scope = scope,
        Start = new LabStart { Colonists = 3 },
        RequiredOps = Ops,
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = run,
    };

    private static async Task MidlaneAsync(ISession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new JsonObject { ["bystander"] = true }, cancellationToken).ConfigureAwait(false);
        var (last, _) = await t.UntilTrainedWithAsync(0, cancellationToken).ConfigureAwait(false);
        if (last["shotsFired"].AsNumber() < 1)
        {
            throw new AcceptanceException($"mid-lane pawn stalled the shooter: no shot fired: {last.ToJson()}");
        }

        if (last["bystanderHurt"].AsNumber() != 0 || last["bystanderHealth"].AsNumber() < 1)
        {
            throw new AcceptanceException($"mid-lane pawn was hit by a drill arrow: {last.ToJson()}");
        }
    }

    private static async Task ChatAsync(ISession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new JsonObject { ["neighbour"] = true }, cancellationToken).ConfigureAwait(false);
        _ = await RunAt.RunUntilAsync(
            t.H,
            "training-both",
            9000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var got = await t.InspectAsync(ct).ConfigureAwait(false);
                var both = LabTraining.IsTrainingJob(got["job"]) && LabTraining.IsTrainingJob(got["otherJob"]);
                return (Wait.Signature(got["job"].AsString(), got["otherJob"].AsString()), both);
            },
            cancellationToken).ConfigureAwait(false);
        var chat = await t.H.CallAsync(
            "training-chat",
            "test/training_act",
            new JsonObject
            {
                ["action"] = "chat",
                ["pawnId"] = t.Prepared["pawn"]?.DeepClone(),
                ["otherId"] = t.Prepared["neighbour"]?.DeepClone(),
            },
            cancellationToken).ConfigureAwait(false);
        s.Report["chat"] = chat;
        if (chat["good"].AsBool() != true)
        {
            throw new AcceptanceException($"adjacent drillers are outside the chat rule: {chat.ToJson()}");
        }

        if (chat["chatted"].AsBool() != true)
        {
            throw new AcceptanceException($"adjacent driller could not chat: {chat.ToJson()}");
        }
    }

    private static async Task TiersAsync(ISession s, CancellationToken cancellationToken)
    {
        var low = await TrainingRun.PrepareAsync(s, new JsonObject { ["level"] = 8, ["neighbour"] = true }, cancellationToken).ConfigureAwait(false);
        var high = low.Neighbour();
        await low.ActAsync(
            new JsonObject { ["action"] = "level", ["pawnId"] = low.Prepared["neighbour"]?.DeepClone(), ["level"] = 10 },
            cancellationToken).ConfigureAwait(false);
        try
        {
            await low.IdleForAsync(600, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"level 8 at the bow ceiling: {e.Message}", e);
        }

        try
        {
            await high.IdleForAsync(600, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"level 10 below Gunsmithing: {e.Message}", e);
        }

        foreach (var (who, research, weapon, level) in new[] { (low, "Gunsmithing", "Gun_PracticeRifle", 8), (high, "ChargedShot", "Gun_PracticePulseRifle", 10) })
        {
            await who.ActAsync(new JsonObject { ["action"] = "research", ["research"] = research }, cancellationToken).ConfigureAwait(false);
            JsonObject last;
            string seen;
            try
            {
                (last, seen) = await who.UntilTrainedWithAsync(level, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"after {research}: {e.Message}", e);
            }

            if (seen != weapon)
            {
                throw new AcceptanceException($"after {research} trained with \"{seen}\", want {weapon}: {last.ToJson()}");
            }

            if (ReferenceEquals(who, low))
            {
                var got = await high.InspectAsync(cancellationToken).ConfigureAwait(false);
                if (got["shotsFired"].AsNumber() != 0)
                {
                    throw new AcceptanceException($"level 10 trained under the rifle ceiling: {got.ToJson()}");
                }
            }
        }
    }

    private static async Task CompanyAsync(ISession s, CancellationToken cancellationToken)
    {
        var t = await TrainingRun.PrepareAsync(s, new JsonObject { ["neighbour"] = true }, cancellationToken).ConfigureAwait(false);
        var second = t.Neighbour();
        JsonObject first = [];
        JsonObject other = [];
        _ = await RunAt.RunUntilAsync(
            t.H,
            "training-both-done",
            12000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                first = await t.InspectAsync(ct).ConfigureAwait(false);
                other = await second.InspectAsync(ct).ConfigureAwait(false);
                static bool Done(JsonObject got) =>
                    LabTraining.Gained(0, (int)got["shootingLevel"].AsNumber(), got["shootingXp"].AsNumber()) > 0 && !LabTraining.IsTrainingJob(got["job"]);
                return (Wait.Signature(first["job"].AsString(), other["job"].AsString()), Done(first) && Done(other));
            },
            cancellationToken).ConfigureAwait(false);
        s.Report["company"] = new JsonArray(first.DeepClone(), other.DeepClone());
        foreach (var got in new[] { first, other })
        {
            if (got["trainedWith"].AsBool() != true)
            {
                throw new AcceptanceException($"a shooter finished beside another without the trained-with thought: {got.ToJson()}");
            }
        }
    }

    private static async Task InterruptAsync(ISession s, string action, CancellationToken cancellationToken)
    {
        // A drafted shooter has a neighbour drilling beside it (#2710): company, yet an interrupted session earns no thought.
        JsonObject? extra = action == "draft" ? new JsonObject { ["neighbour"] = true } : null;
        var t = await TrainingRun.PrepareAsync(s, extra, cancellationToken).ConfigureAwait(false);
        _ = await RunAt.RunUntilAsync(
            t.H,
            "training-start",
            9000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var got = await t.InspectAsync(ct).ConfigureAwait(false);
                var primary = got["primary"].AsString();
                var armed = LabTraining.IsPracticeWeapon(primary);
                return (Wait.Signature(got["job"].AsString(), primary), LabTraining.IsTrainingJob(got["job"]) && armed);
            },
            cancellationToken).ConfigureAwait(false);
        await t.ActAsync(
            new JsonObject
            {
                ["action"] = action,
                ["pawnId"] = t.Prepared["pawn"]?.DeepClone(),
                ["thingId"] = t.Prepared["dummy"]?.DeepClone(),
            },
            cancellationToken).ConfigureAwait(false);

        // A drafted shooter ends the job at once; a burning dummy ends it by destruction or when the session completes.
        // Either way the hands are restored.
        JsonObject last = [];
        _ = await RunAt.RunUntilAsync(
            t.H,
            "training-ended",
            9000,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var got = await t.InspectAsync(ct).ConfigureAwait(false);
                last = got;
                return (Wait.Signature(got["job"].AsString()), !LabTraining.IsTrainingJob(got["job"]));
            },
            cancellationToken).ConfigureAwait(false);
        s.Report["after"] = last;
        if (last["primary"].AsString() != t.Prepared["weapon"].AsString())
        {
            throw new AcceptanceException($"real weapon not restored after {action}: {last.ToJson()}");
        }

        if (action == "draft" && last["trainedWith"].AsBool() == true)
        {
            throw new AcceptanceException($"an interrupted session earned the trained-with thought: {last.ToJson()}");
        }

        if (last["practiceOnMap"].AsNumber() != 0)
        {
            throw new AcceptanceException($"practice weapon left on the map after {action}: {last.ToJson()}");
        }

        foreach (var def in last["held"].AsSlice())
        {
            if (LabTraining.IsPracticeWeapon(def.AsString()))
            {
                throw new AcceptanceException($"practice weapon still held after {action}: {last.ToJson()}");
            }
        }
    }

    /// <summary>A staged range column and the arguments that read it back (Go <c>trainingRun</c>).</summary>
    private sealed class TrainingRun
    {
        private readonly JsonObject _args;

        private TrainingRun(Harness h, JsonObject prepared, JsonObject args)
        {
            H = h;
            Prepared = prepared;
            _args = args;
        }

        internal Harness H { get; }

        internal JsonObject Prepared { get; }

        internal static async Task<TrainingRun> PrepareAsync(ISession s, JsonObject? extra, CancellationToken cancellationToken)
        {
            var h = s.Harness;
            var (cx, cz) = CaseSupport.Center(s);
            var args = new JsonObject { ["x"] = cx, ["z"] = cz };
            foreach (var (key, value) in extra ?? [])
            {
                args[key] = value?.DeepClone();
            }

            var prepared = await h.CallAsync("training-prepare", "test/training_prepare", args, cancellationToken).ConfigureAwait(false);
            if (prepared["success"].AsBool() != true)
            {
                throw new AcceptanceException($"training_prepare: {prepared.ToJson()}");
            }

            return new TrainingRun(
                h,
                prepared,
                new JsonObject
                {
                    ["pawnId"] = prepared["pawn"]?.DeepClone(),
                    ["dummyId"] = prepared["dummy"]?.DeepClone(),
                    ["sentinels"] = prepared["sentinels"]?.DeepClone(),
                    ["otherId"] = prepared["neighbour"]?.DeepClone(),
                    ["bystanderId"] = prepared["bystander"]?.DeepClone(),
                });
        }

        /// <summary>The run read back through the second column's shooter and dummy.</summary>
        internal TrainingRun Neighbour()
        {
            var args = (JsonObject)_args.DeepClone();
            args["pawnId"] = Prepared["neighbour"]?.DeepClone();
            args["dummyId"] = Prepared["neighbourDummy"]?.DeepClone();
            return new TrainingRun(H, Prepared, args);
        }

        internal Task<JsonObject> InspectAsync(CancellationToken cancellationToken) =>
            H.CallAsync("training-inspect", "test/training_inspect", (JsonObject)_args.DeepClone(), cancellationToken);

        internal async Task ActAsync(JsonObject args, CancellationToken cancellationToken)
        {
            var action = args["action"].ToJson();
            var got = await H.CallAsync("training-act", "test/training_act", args, cancellationToken).ConfigureAwait(false);
            if (got["success"].AsBool() != true)
            {
                throw new AcceptanceException($"training_act {action}: {got.ToJson()}");
            }
        }

        /// <summary>Runs until the shooter has gained XP above <paramref name="startLevel"/> and is idle again; also returns the practice weapon seen in hand while the job ran.</summary>
        internal async Task<(JsonObject Last, string Weapon)> UntilTrainedWithAsync(int startLevel, CancellationToken cancellationToken)
        {
            JsonObject last = [];
            var weapon = "";
            _ = await RunAt.RunUntilAsync(
                H,
                "training",
                9000,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var got = await InspectAsync(ct).ConfigureAwait(false);
                    last = got;
                    var p = got["primary"].AsString();
                    if (LabTraining.IsPracticeWeapon(p))
                    {
                        weapon = p;
                    }

                    var gained = LabTraining.Gained(startLevel, (int)got["shootingLevel"].AsNumber(), got["shootingXp"].AsNumber());
                    return (Wait.Signature(gained, got["job"].AsString()), gained > 0 && !LabTraining.IsTrainingJob(got["job"]));
                },
                cancellationToken).ConfigureAwait(false);
            return (last, weapon);
        }

        /// <summary>Runs <paramref name="ticks"/> of game time and fails when the shooter drilled in them.</summary>
        internal async Task IdleForAsync(ulong ticks, CancellationToken cancellationToken)
        {
            double first = 0;
            double shots = 0;
            JsonObject last = [];
            _ = await RunAt.RunUntilAsync(
                H,
                "training-idle",
                ticks + 3000,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var got = await InspectAsync(ct).ConfigureAwait(false);
                    last = got;
                    if (LabTraining.IsTrainingJob(got["job"]))
                    {
                        throw new AcceptanceException($"drilled at the ceiling: {got.ToJson()}");
                    }

                    if (first == 0)
                    {
                        first = got["tick"].AsNumber();
                        shots = got["shotsFired"].AsNumber();
                    }

                    return (Wait.Signature(got["tick"].AsNumber()), got["tick"].AsNumber() - first >= ticks);
                },
                cancellationToken).ConfigureAwait(false);
            if (last["shotsFired"].AsNumber() != shots)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"trained at the ceiling: {last.ToJson()}"));
            }
        }
    }
}

using System.Globalization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/productive-cooking</c>, <c>development-lifecycle</c> and <c>development-reassignment</c>: useful
/// simple-meal demand and near-next-level ordinary cooks, under the served brain. The shared <see cref="Kitchen"/> stage
/// builds a fueled stove with a finite simple-meal bill, loose rice, a meal stockpile and the named cooks at noon;
/// test/productive_cooking_prepare binds them by load id, sets the cooks' skill and needs (no worker or activity is
/// selected), verifies the native counterfactual cooking rates and records native learning, output and ownership; the
/// urge and satisfy ops inject a critical rest need and the withdrawn demand, which the primitives cannot express. The
/// reassignment's better cook is a staged Cooking level. Superpawns at skill 20 would make the level-5 cooking skill and
/// its learning unobservable.
/// </summary>
internal static class ProductionCookingDevelopment
{
    private const string Prepare = "test/productive_cooking_prepare";
    private const string ReadTool = "test/productive_cooking_read";
    private const string UrgeTool = "test/productive_cooking_urge";
    private const string SatisfyTool = "test/productive_cooking_satisfy";

    // The bill's prepared repeats; every completed one is a Method-owned cook's learning callback.
    private const int Repeats = 6;

    // The better cook's level: well above the prepared level-5 cooks, so its native work rate shortens every meal.
    private const int BetterCookLevel = 15;

    // The staged colonists; every one is an ordinary cook, and the fixture watches the first for output.
    private const int Cooks = 3;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepared = new(
        Prepare,
        r => ServedOps.Args(
            ("cook", r.Pawns[Cook(0)].Id),
            ("bench", r.Buildings["stove"].Ids.Single()),
            ("bill", r.Bills["meals"]),
            ("rice", r.Things["rice"].Ids.Single())),
        ServedOps.Done(Prepare));

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Urge = ServedOps.Inject(UrgeTool);
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Satisfy = ServedOps.Inject(SatisfyTool);
    private static readonly FixtureOp<NoArgs, Fx.ProductiveCookingRead> Read = ServedOps.Read<Fx.ProductiveCookingRead>(ReadTool);

    // A fueled stove with a six-meal bill dropping on the floor, rice for every repeat off the interaction cell, a
    // simple-meal stockpile and the cooks, at noon so the first hours are waking ones.
    private static readonly Fx.StageSpec Kitchen = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 12 },
        Site = new Fx.StageSite
        {
            Width = 11,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 11, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
        Buildings = [new Fx.StageBuilding { Name = "stove", Def = "FueledStove", Cells = [new(5, 3)], Fuel = 50 }],
        Bills = [new Fx.StageBill { Name = "meals", Building = "stove", Recipe = "CookMealSimple", RepeatCount = Repeats, DropOnFloor = true }],
        Things = [new Fx.StageThing { Name = "rice", Def = "RawRice", Count = 60, Cell = new(5, 5) }],
        Zones = [new Fx.StageZone { Name = "meals", Cells = [new(0, 0)], Allow = ["MealSimple"] }],
        Pawns = [.. Enumerable.Range(0, Cooks).Select(i => new Fx.StagePawn { Name = Cook(i), Colonist = i })],
    };

    // Natural recovery from critical fatigue is hours of game time; once sleep is observed the case stands in for it.
    private static readonly Fx.StageSpec Rested = new()
    {
        Pawns = [.. Enumerable.Range(0, Cooks).Select(i => new Fx.StagePawn { Name = Cook(i), Colonist = i, Needs = new Dictionary<string, float> { ["Rest"] = .9f } })],
    };

    internal static TypedCase Productive() => new()
    {
        Name = "production/productive-cooking",
        Scope = "Under the served brain ordinary cooks fill prepared simple-meal demand: native recipes complete more than once, "
            + "meals appear, a cook reaches the next Cooking level, and native learning matches the counterfactual per-level rates "
            + "read without changing live state. Unit tests prove the allocation tradeoffs, not native learning and execution.",
        Ticks = 18000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, ReadTool],
        Stage = Kitchen,
        Crew = new Crew { Size = Cooks, Ordinary = true },
        Keep = ["Food"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "productive-cooking" },
        Run = async (s, ct) =>
        {
            _ = await s.FixtureAsync("cooking-prepare", Prepared, s.Staged!, ct).ConfigureAwait(false);
            _ = await s.ServeUntilAsync("productive-cooking", s.TicksLeft, async token =>
            {
                var read = await ObserveAsync(s, token).ConfigureAwait(false);
                var done = read.Methods.Count > 0 && read.Output && read.SubsequentWork && read.Completions >= 2 && read.MaxCookingLevel >= 6;
                return (Signature(read), done);
            }, ct).ConfigureAwait(false);
        },
    };

    internal static TypedCase Lifecycle() => new()
    {
        Name = "production/development-lifecycle",
        Scope = "Under the served brain, cooking development that has produced work is postponed when every colonist is made "
            + "critically tired: no cooking job begins while tired beyond the tasks already handed out, and a colonist sleeps. "
            + "With rest restored, cooking resumes, and once the demand is withdrawn no colonist is left owned. Unit tests prove "
            + "the ordering; this proves postponement, resumption and release through native jobs.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, ReadTool, UrgeTool, SatisfyTool, FixtureStage.Tool],
        Stage = Kitchen,
        Crew = new Crew { Size = Cooks, Ordinary = true },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "development-lifecycle" },
        Run = LifecycleAsync,
    };

    internal static TypedCase Reassignment() => new()
    {
        Name = "production/development-reassignment",
        Scope = "Under the served brain, once cooking development has produced work and its cook is owned, a far better cook "
            + "joins (another colonist's Cooking level is raised): the brain moves the cooking to it. The first cook's Method "
            + "releases it, the better cook completes a meal under a coupled Method, and every completed repeat is exactly one "
            + "Method-owned completion, so nothing is cooked twice or outside a Method. Unit tests prove selection on rates; "
            + "this proves the brain's own reassignment through native jobs.",
        Ticks = 12000,
        Tier = CaseTier.OnDemand,
        Ops = [Prepare, ReadTool, FixtureStage.Tool],
        Stage = Kitchen,
        Crew = new Crew { Size = Cooks, Ordinary = true },
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "development-reassignment" },
        Run = ReassignmentAsync,
    };

    private static async Task LifecycleAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("cooking-prepare", Prepared, s.Staged!, cancellationToken).ConfigureAwait(false);
        bool urged = false, recovered = false, satisfied = false;
        _ = await s.ServeUntilAsync("development-lifecycle", s.TicksLeft, async ct =>
        {
            var read = await ObserveAsync(s, ct).ConfigureAwait(false);
            if (!urged)
            {
                if (read.Methods.Count > 0 && read.Completions >= 1)
                {
                    _ = await s.FixtureAsync("lifecycle-urge", Urge, NoArgs.Value, ct).ConfigureAwait(false);
                    urged = true;
                }

                return (Signature(read), false);
            }

            // A task already handed out when the obligation arrived may finish; no further cooking job may begin while tired.
            if (read.TiredCooks > read.CommittedAtUrge)
            {
                throw new AcceptanceException($"development was not postponed under the urgent rest need: {ServedOps.Json(read)}");
            }

            if (read.Slept && !recovered)
            {
                _ = await s.StageAsync("lifecycle-recover", Rested, ct).ConfigureAwait(false);
                recovered = true;
            }

            if (read.Slept && read.RestedCooksAfterUrge > 0 && !satisfied)
            {
                _ = await s.FixtureAsync("lifecycle-satisfy", Satisfy, NoArgs.Value, ct).ConfigureAwait(false);
                satisfied = true;
            }

            return (Signature(read), satisfied && Retired(read));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReassignmentAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("cooking-prepare", Prepared, s.Staged!, cancellationToken).ConfigureAwait(false);
        var cooks = Enumerable.Range(0, Cooks).ToDictionary(i => s.Staged!.Pawns[Cook(i)].Id, i => i, StringComparer.Ordinal);
        (string Pawn, string Method)? first = null;
        var better = "";
        var incapable = new HashSet<string>(StringComparer.Ordinal);
        _ = await s.ServeUntilAsync("development-reassignment", s.TicksLeft, async ct =>
        {
            var read = await ObserveAsync(s, ct).ConfigureAwait(false);

            // Each completed repeat is one learning callback of a Method-owned cook: none is uncounted or counted twice.
            if (read.Completions != Repeats - read.BillRemaining)
            {
                throw new AcceptanceException($"bill progress differs from Method-owned completions: {ServedOps.Json(read)}");
            }

            if (first is not { } owned)
            {
                // The cook that produced the first meal, while a Method owns it, and an idle colonist that has not cooked.
                var cook = read.CompletedBy.Keys.FirstOrDefault(read.Owners.ContainsKey);
                var idle = cooks.Keys.FirstOrDefault(p => !read.Owners.ContainsKey(p) && !read.CompletedBy.ContainsKey(p) && !incapable.Contains(p));
                if (cook is not null && idle is not null)
                {
                    var joined = new Fx.StagePawn
                    {
                        Name = Cook(cooks[idle]),
                        Colonist = cooks[idle],
                        Skills = new Dictionary<string, int> { ["Cooking"] = BetterCookLevel },
                    };
                    var staged = await s.StageAsync("reassignment-better-cook", new Fx.StageSpec { Pawns = [joined] }, ct).ConfigureAwait(false);
                    // A colonist whose backstory disables Cooking reads level 0 whatever is staged: it is no better cook (#3167).
                    if (staged.Pawns[joined.Name].Skills?.GetValueOrDefault("Cooking") != BetterCookLevel) incapable.Add(idle);
                    else
                    {
                        first = (cook, read.Owners[cook]);
                        better = idle;
                    }
                }

                return (Signature(read), false);
            }

            var released = read.Owners.GetValueOrDefault(owned.Pawn) != owned.Method;
            return (Signature(read), released && read.CompletedBy.GetValueOrDefault(better) > 0);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Fx.ProductiveCookingRead> ObserveAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var read = await s.FixtureAsync("cooking-observe", Read, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("observed", ServedOps.Json(read));
        return read.LearningFailures.Count != 0
            ? throw new AcceptanceException($"native learning differs from the counterfactual rates: {ServedOps.Json(read)}")
            : read;
    }

    // The Method retired: the bill is gone and nobody cooks or is owned.
    private static bool Retired(Fx.ProductiveCookingRead read) => !read.BillActive && read.Cooking == 0 && read.Owners.Count == 0;

    private static string Signature(Fx.ProductiveCookingRead read) => read.Tick.ToString(CultureInfo.InvariantCulture);

    private static string Cook(int index) => "cook" + index.ToString(CultureInfo.InvariantCulture);
}

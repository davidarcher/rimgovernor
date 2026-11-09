using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #4058: a due ritual is a priced coupled root of external attendee tasks per accepting site, following the trade pattern.
public sealed class RitualCandidateTests
{
    private const double Horizon = 20000;
    private static readonly Cell Spot = new(1, 1), Hall = new(2, 1);
    private const string Precept = "Precept_3", SpotRoot = "ritual/Precept_3/spot", HallRoot = "ritual/Precept_3/hall";

    internal static RitualOutcomeModel Model => new()
    {
        StartingQuality = .2, MinQuality = 0, MaxQuality = 1,
        Comps = [new(RitualQualityInput.Participants, [new(0, 0), new(4, .4)]), new(RitualQualityInput.Impressiveness, [new(0, 0), new(100, .4)])],
        Chances = [new(.5, true, 6, 3) { Memory = "GoodGathering", StackLimit = 1, Fades = true }, new(.2, false, -4, 2) { Memory = "BadGathering", StackLimit = 1 }, new(.1, false, 0, 0)],
    };

    internal static RitualDef Def(double interval = 0, RitualOutcomeModel? model = null) => new()
    {
        IntervalDaysMin = interval, RequiredBuildings = ["RitualSpot"], CanStartAnytime = true, DurationTicks = 6000,
        Roles = [new("leader", "", 1, true), new("helper_role", "", 1, false)], Outcome = model,
    };

    private static RitualSiteRead Site(string id, Cell at, bool helperSpectates = true, bool indoors = true) => new(Precept, "Gathering", id, at)
    {
        Usable = true, Refusal = "", Indoors = indoors, Impressiveness = 50, Cleanliness = 0,
        Candidates =
        [
            new("pawn") { Roles = [new("leader", true, ""), new("helper_role", false, "Leads")], Spectate = new("", true, "") },
            new("helper") { Roles = [new("leader", false, "Not a believer"), new("helper_role", false, "No role")], Spectate = new("", helperSpectates, helperSpectates ? "" : "Downed") },
        ],
    };

    private static CoupledRoute Route(string pawn, Cell to, double ticks) => new(pawn, new(0, 0), to, new RouteTicks.Reachable(ticks)) { FromStart = true };
    private static readonly CoupledRoute[] Routes = [Route("pawn", Spot, 10), Route("helper", Spot, 30), Route("pawn", Hall, 5), Route("helper", Hall, 5)];

    internal static ReviewFacts Facts(RitualDef def, RitualActivityRead read, IEnumerable<CoupledRoute> routes, bool calm = true, bool enabled = true)
    {
        var facts = ForecastPortfolioChoiceTests.Observed([], [], helper: true);
        var ideology = new Ideoligion(new() { Rituals = new Dictionary<string, RitualDef> { ["Gathering"] = def } },
            new IdeoligionFacts { Observed = IdeologyReadsTests.NoPrecepts with { Rituals = [IdeologyTestRows.Ritual(Precept, "Gathering")] } });
        return facts with
        {
            Hostiles = HostileCensus.Of(calm ? 0L : 2L), CriticalPatients = 0L, Ideology = new IdeologyMode.Installed(ideology),
            AvailableNeeds = EquatableArray.From(enabled ? facts.AvailableNeeds.Append(IdeologyFamily.Rituals).Distinct() : facts.AvailableNeeds.Where(n => n != IdeologyFamily.Rituals)),
            ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with { Rituals = read, Routes = EquatableArray.From(routes) } },
        };
    }

    private static ForecastCoupledCandidates Generate(ReviewFacts facts) => ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Priced();
    private static ForecastCommitment[] Roots(ForecastCoupledCandidates made) => made.Alternatives.Where(a => a.Id.StartsWith("ritual/", StringComparison.Ordinal)).OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
    private static string[] RitualFindings(ForecastCoupledCandidates made) => made.Findings.Where(f => f.StartsWith("ritual", StringComparison.Ordinal)).ToArray();
    private static ForecastActivityAllocation Allocation(string task, string pawn, double depart, double walk) =>
        new(task, pawn, depart, depart + walk, depart + walk, depart + walk + 6000, walk, 6000, 6000, 0);

    [Fact]
    public void ADueRitualIsOneExclusiveRootPerAcceptingSiteOfSynchronizedExternalAttendeeTasks()
    {
        var made = Generate(Facts(Def(model: Model), new() { Sites = [Site("spot", Spot), Site("hall", Hall)] }, Routes));
        var roots = Roots(made);
        Assert.Equal([HallRoot, SpotRoot], roots.Select(r => r.Id));
        Assert.All(roots, r => Assert.Equal("ritual/Precept_3", r.ExclusiveOutcome));
        Assert.Empty(RitualFindings(made));
        var spot = roots[1];
        Assert.Equal([SpotRoot + "/pawn", SpotRoot + "/helper"], spot.ActivityCompletions);
        var tasks = made.Context.Activities.Activities.Where(t => spot.ActivityCompletions.Contains(t.Id)).ToArray();
        Assert.All(tasks, t => Assert.Equal((30d, 30d, "cell/1/1"), (t.NotBefore, t.LatestStart, t.Location)));
        Assert.Equal(["ritual/Precept_3/helper", "ritual/Precept_3/pawn"], tasks.Select(t => t.ChoiceIdentity).Order(StringComparer.Ordinal));
        Assert.All(made.Context.Activities.Activities.Where(t => roots[0].ActivityCompletions.Contains(t.Id)), t => Assert.Equal(5d, t.NotBefore));
        var options = made.Context.Activities.Options.Where(o => spot.ActivityCompletions.Contains(o.Activity)).ToArray();
        Assert.Equal(2, options.Length);
        Assert.All(options, o => Assert.Equal((6000d, 0d), (o.WorkTicks, o.AbsenceTicks)));
        Assert.Equal(4, made.Context.RitualWork.Count);
        Assert.Equal(["helper", "pawn"], made.Context.RitualWork.Where(w => w.Root == SpotRoot).Select(w => w.Pawn).Order(StringComparer.Ordinal));
        var quality = RitualPricing.Quality(Model, 2, 50, 0);
        // Every row of the root carries the begin the review commits (#4059): the leader organizes, the helper spectates, at the site.
        var begin = made.Context.RitualWork.First(w => w.Root == SpotRoot).Begin;
        Assert.Equal((RitualVerb.Begin, "pawn", Precept, Spot), (begin.Verb, begin.Pawn.Value, begin.Kind.Value, begin.Spot));
        Assert.Equal([new RitualSlot("leader", EquatableArray.From(new[] { new PawnId("pawn") }))], begin.Slots);
        Assert.Equal([new PawnId("helper")], begin.Spectators);
        Assert.All(made.Context.RitualWork.Where(w => w.Root == SpotRoot), w => Assert.Equal(begin, w.Begin));
        Assert.Equal(FormattableString.Invariant($"Gathering due (0 days since last) at spot, 2 attending, quality {quality:0.00}"), made.Context.RitualWork.First(w => w.Root == SpotRoot).Premise);
        // Every outcome with a memory is granted to every attendee, its mood weighted by the draw probability at the site's quality.
        var weights = RitualPricing.OutcomeWeights(Model, quality);
        var expected = weights[0] * 6 + weights[1] * -4;
        Assert.True(quality > .2 && weights[0] > 0 && weights[1] > 0);
        foreach (var pawn in new[] { "pawn", "helper" })
        {
            var granted = spot.MoodMemories.Where(m => m.Pawn == pawn).ToArray();
            Assert.Equal(["BadGathering", "GoodGathering"], granted.Select(m => m.Memory.Id).Order(StringComparer.Ordinal));
            Assert.Equal(expected, granted.Sum(m => m.Memory.Offset), 9);
            var good = granted.Single(m => m.Memory.Id == "GoodGathering").Memory;
            Assert.Equal((new MemoryLife.Expires(3 * GameTime.TicksPerDay), true, 1), (good.Life, good.Fades, good.StackLimit));
        }
        // Outdoors the room-stat comp does not apply, so the quality is lower.
        Assert.True(RitualPricing.Quality(Model, 2, 50, 0, indoors: false) < quality);
    }

    [Fact]
    public void AttendeeTasksAreNeverDispatchedAndTheRootIsReadyOnlyWhenEveryAttendeeDepartsNowFirst()
    {
        var made = Generate(Facts(Def(model: Model), new() { Sites = [Site("spot", Spot)] }, Routes));
        var root = Assert.Single(Roots(made));
        var now = new ForecastActivityPlan([Allocation(SpotRoot + "/pawn", "pawn", 0, 10), Allocation(SpotRoot + "/helper", "helper", 0, 30)], 6030, 0);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(now, made.Context).Allocations);
        Assert.Empty(CoordinatedActivityBinding.Withheld(now, made.Context));
        var ready = ForecastRitualExecution.Ready(new([root.Id], now, [], 0, false), made.Context);
        Assert.Equal(["helper", "pawn"], ready.Select(w => w.Pawn).Order(StringComparer.Ordinal));
        Assert.All(ready, w => Assert.Equal(SpotRoot, w.Root));
        var later = new ForecastActivityPlan([Allocation(SpotRoot + "/pawn", "pawn", 0, 10),
            new("eat", "helper", 0, 0, 0, 5, 0, 5, 5, 0), Allocation(SpotRoot + "/helper", "helper", 5, 30)], 6035, 0);
        Assert.Empty(ForecastRitualExecution.Ready(new([root.Id], later, [], 0, false), made.Context));
        Assert.Empty(ForecastRitualExecution.Ready(new([], now, [], 0, false), made.Context));
    }

    [Fact]
    public void AnUnreachableAttendeeDropsThatSiteAndARefusedSpectatorIsLeftOutByName()
    {
        var made = Generate(Facts(Def(model: Model), new() { Sites = [Site("spot", Spot), Site("hall", Hall)] }, Routes.Where(r => r.Pawn != "helper" || r.To != Hall)));
        Assert.Equal(SpotRoot, Assert.Single(Roots(made)).Id);
        Assert.Equal([RitualMethodCandidates.AttendeeUnreachable + "Precept_3:hall:helper"], RitualFindings(made));
        made = Generate(Facts(Def(model: Model), new() { Sites = [Site("spot", Spot, helperSpectates: false)] }, Routes));
        var root = Assert.Single(Roots(made));
        Assert.Equal([SpotRoot + "/pawn"], root.ActivityCompletions);
        Assert.Equal(10d, Assert.Single(made.Context.Activities.Activities, t => t.Id == SpotRoot + "/pawn").NotBefore);
        Assert.Equal([RitualMethodCandidates.AttendeeRefused + "Precept_3:spot:helper:Downed"], RitualFindings(made));
        made = Generate(Facts(Def(model: Model), new() { Sites = [Site("spot", Spot) with { Usable = false, Refusal = "Occupied" }] }, Routes));
        Assert.Empty(Roots(made));
        Assert.Equal([RitualMethodCandidates.SiteRefused + "Precept_3:spot:Occupied"], RitualFindings(made));
    }

    [Fact]
    public void AnUnmodelledOutcomeAnUndueRitualADisabledNeedOrAnEmergencyPlansNothing()
    {
        var read = new RitualActivityRead { Sites = [Site("spot", Spot)] };
        var made = Generate(Facts(Def(model: null), read, Routes));
        Assert.Empty(Roots(made));
        Assert.Equal([RitualPricing.RitualUnmodelled + "Gathering"], RitualFindings(made));
        made = Generate(Facts(Def(model: Model with { Unread = "RitualOutcomeEffectWorker_Funeral" }), read, Routes));
        Assert.Equal([RitualPricing.RitualUnmodelled + "RitualOutcomeEffectWorker_Funeral"], RitualFindings(made));
        made = Generate(Facts(Def(interval: 15, model: Model), read, Routes));
        Assert.Empty(Roots(made));
        Assert.Empty(RitualFindings(made));
        made = Generate(Facts(Def(model: Model), read, Routes, enabled: false));
        Assert.Empty(Roots(made));
        Assert.Empty(RitualFindings(made));
        Assert.Empty(made.Context.RitualWork);
        made = Generate(Facts(Def(model: Model), read, Routes, calm: false));
        Assert.Empty(Roots(made));
        Assert.Equal([RitualMethodCandidates.HeldNotCalm], RitualFindings(made));
        made = Generate(Facts(Def(model: Model), CoupledFixtures.NoRituals, Routes));
        Assert.Empty(Roots(made));
        Assert.Equal([RitualMethodCandidates.NoSite + "Precept_3"], RitualFindings(made));
    }
}

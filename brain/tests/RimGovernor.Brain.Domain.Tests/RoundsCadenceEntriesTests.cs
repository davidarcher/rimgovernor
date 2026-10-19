using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The family entries on the rounder's cadence and the zone census declared only by its readers (#3556): a family
/// entry without a cadence of its own is due when the last committed rounds review has stood the rounds cadence,
/// whatever tick it last ran at, and an invalidation native narrows to zone ids marks the families naming the zone
/// census and no other.
/// </summary>
public sealed class RoundsCadenceEntriesTests
{
    private const long Every = ClockSchedulerConfig.DefaultRoundsEveryTicks;

    private static PlannerEntry Catalog(string name) => new()
    {
        Name = name,
        Class = PlannerClass.Optional,
        Configured = _ => true,
        Run = (_, _, _, _, _) => Task.FromResult(default(Verdict)),
    };

    private static PlannerEntry Family(FamilySchedule? schedule = null) =>
        ClockPlannerCatalog.FamilyEntry(new FamilyRunner<PowerFacts>(new PowerFamily(), (_, _) => Task.FromResult(new PowerFacts()), schedule));

    private static PlannerEntry[] Entries => [Catalog("defense"), Family(), Family() with { Name = "shelter" }];

    private static List<string> Selected(PlannerQueue queue, PlannerEntry[] catalog, PlannerSelectionResult sel) =>
        catalog.Where(e => sel.Pick?.Invoke(e) ?? sel.Planners).Select(e => e.Name).ToList();

    private static string Words<T>(IEnumerable<T> items) => string.Join(",", items);

    private static EquatableArray<T> From<T>(params T[] items) => EquatableArray.From(items);

    [Fact]
    public void AFamilyEntryIsDueWhenTheRounderIsNotAnHourAfterItsOwnRun()
    {
        var catalog = Entries;
        var queue = new PlannerQueue { Catalog = () => catalog };
        // The first step reviews everything at tick 0; the review stands until 2500.
        var full = queue.Selection(0, true, null);
        queue.Ran(full, catalog.Select(e => e.Name), _ => null, 0, null, Every);
        // A dirty step at 600 runs one family entry on the last review: it is still due at 2500 with the rounder,
        // not at 3100.
        queue.Mark("power");
        var dirty = queue.Selection(600, false, null);
        Assert.Equal("power", Words(Selected(queue, catalog, dirty)));
        queue.Ran(dirty, ["power"], _ => null, 600, null, Every);
        Assert.False(queue.Selection(2400, false, null).Planners);
        var due = queue.Selection(3000, false, null);
        Assert.Equal("defense,power,shelter", Words(Selected(queue, catalog, due)));
        Assert.Equal("due", due.Why!["power"]);
    }

    [Fact]
    public void AReviewTheRounderCommittedEarlyMovesEveryFamilyEntryToItsNextDueTick()
    {
        var catalog = Entries;
        var queue = new PlannerQueue { Catalog = () => catalog };
        queue.Ran(queue.Selection(0, true, null), catalog.Select(e => e.Name), _ => null, 0, null, Every);
        // A hazard at 1800 marks one family entry and the step reviews first: the review now stands until 4300, and
        // the entry that did not run moves with it instead of running alone on the last review at 3000.
        queue.Mark("power");
        var wake = queue.Selection(1800, false, null);
        queue.Ran(wake, ["power"], _ => null, 1800, null, 1800 + Every);
        var review = queue.Selection(3000, false, null);
        Assert.Equal("defense", Words(Selected(queue, catalog, review)));
        Assert.Equal("defense,power,shelter", Words(Selected(queue, catalog, queue.Selection(4800, false, null))));
    }

    [Fact]
    public void BeforeTheFirstReviewAndWithACadenceOfItsOwnAnEntryKeepsItsOwnClock()
    {
        var catalog = new[] { Family(), Family(new FamilySchedule { Every = new Tick(600) }) with { Name = "shelter" } };
        var queue = new PlannerQueue { Catalog = () => catalog };
        queue.Ran(queue.Selection(0, true, null), catalog.Select(e => e.Name), _ => null, 0, null, null);
        Assert.Equal("shelter", Words(Selected(queue, catalog, queue.Selection(600, false, null))));
        Assert.Equal("power,shelter", Words(Selected(queue, catalog, queue.Selection(Every, false, null))));
        Assert.False(catalog[1].RoundsCadence);
        Assert.True(catalog[0].RoundsCadence);
    }

    [Fact]
    public void ZoneIdsNarrowAColonyInvalidationToTheZoneCensus()
    {
        var zones = new Invalidation { Families = From(FactFamily.Colony), Ids = From("Zone_12", "Zone_7") };
        Assert.Equal("Zones", Words(zones.Sections()));
        var things = new Invalidation { Families = From(FactFamily.Colony), Ids = From("Thing_Wall4") };
        Assert.Equal("Buildings", Words(things.Sections()));
        // Ids of no known shape dirty every keyed section, as before; a rectangle dirties the cell section too.
        var unknown = new Invalidation { Families = From(FactFamily.Colony), Ids = From("bill-3") };
        Assert.Equal("Zones,Buildings,Bills", Words(unknown.Sections()));
        var edited = new Invalidation { Families = From(FactFamily.Colony), Ids = From("Zone_1"), Rect = new Rect(0, 0, 4, 4) };
        Assert.Equal("PlanningCells,Zones", Words(edited.Sections()));
        // A pawn id fits no pawn section: the family is dirty whole.
        var pawn = new Invalidation { Families = From(FactFamily.Pawns), Ids = From("Thing_Human9") };
        Assert.Equal("Population,Pawns", Words(pawn.Sections()));
    }

    [Fact]
    public void OnlyAFamilyNamingTheZoneCensusDeclaresIt()
    {
        var reader = Family(new FamilySchedule { Sections = [Section.Zones] });
        Assert.Contains(Section.Zones, reader.Sections);
        var other = Family();
        Assert.DoesNotContain(Section.Zones, other.Sections);
        Assert.Contains(Section.Colony, other.Sections);
        Assert.Contains(Section.Buildings, other.Sections);
        Assert.Contains(Section.Bills, other.Sections);

        var queue = new PlannerQueue { Catalog = () => [reader, other with { Name = "other" }] };
        queue.Wake(new StepReason { Cause = StepCause.Review, Families = [FactFamily.Colony], Sections = [Section.Zones] });
        var sel = queue.Selection(600, false, null);
        Assert.Equal("power", Words(sel.Why!.Keys.ToList()));
        queue.Wake(new StepReason { Cause = StepCause.Review, Families = [FactFamily.Colony], Sections = [Section.Colony] });
        Assert.Equal("other,power", Words(queue.Selection(600, false, null).Why!.Keys.Order().ToList()));
    }

    [Fact]
    public void TheZoneReadersNameTheZoneCensus()
    {
        Assert.Contains(Section.Zones, AdminRounds.Schedule.Sections);
        Assert.Contains(Section.Zones, FoodRounds.Schedule.Sections);
        Assert.Contains(Section.Zones, LayoutRounds.Schedule.Sections);
        Assert.Contains(Section.Zones, EconomyRounds.Schedule.Sections);
    }
}

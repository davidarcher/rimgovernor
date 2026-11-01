using RimGovernor.Brain.Domain;
using ExpectedTiming = RimGovernor.Brain.GameFacts.Observations.ExpectedTiming;

namespace RimGovernor.Brain.Policy;

/// <summary>The facility demand an expected change adds when it takes effect.</summary>
public enum ExpectedOwner
{
    /// <summary>A hosted sleeper's regular bed (an arriving lodger).</summary>
    Sleeper,

    /// <summary>A prison bed (an arriving prisoner).</summary>
    PrisonSleeper,

    /// <summary>A newborn's crib.</summary>
    Crib,

    /// <summary>A mother's bed rest after a birth.</summary>
    CareBed,

    /// <summary>A baby's own bed once it leaves the crib.</summary>
    CribLeaver,

    /// <summary>The hay a herd eats once the growing season ends.</summary>
    HayField,

    /// <summary>The watts a planned consumer draws once it stands.</summary>
    Power,
}

/// <summary>
/// What one day of an expected change's shortage costs: capability-days a day, or unassessed naming the fact the price
/// needs. An unassessed loss never justifies preparation (rule 9).
/// </summary>
public abstract record ExpectedLoss
{
    private protected ExpectedLoss() { }

    public sealed record PerDay(double CapabilityDays) : ExpectedLoss;

    public sealed record Unassessed(string Fact) : ExpectedLoss;
}

/// <summary>How long an expected change's demand lasts once it takes effect: for good, until a tick, or undated naming the fact.</summary>
public abstract record ExpectedStay
{
    private protected ExpectedStay() { }

    public sealed record Permanent : ExpectedStay;

    public sealed record Until(long Tick) : ExpectedStay;

    public sealed record Undated(string Fact) : ExpectedStay;
}

/// <summary>
/// One dated expectation: <paramref name="Units"/> of <paramref name="Owner"/>'s demand from <paramref name="Source"/> taking
/// effect <paramref name="At"/>, lasting <paramref name="Stay"/>, its shortage costing <paramref name="Loss"/> a day. demand(t) is
/// the current demand plus every change whose tick is at or before t.
/// </summary>
public sealed record ExpectedChange(string Source, ExpectedOwner Owner, double Units, ExpectedTiming At, ExpectedStay Stay, ExpectedLoss Loss);

/// <summary>
/// One way to provide an expected change: permanent (a build) or temporary (a bed share, a stand-in spot, a field spot, a
/// conversion), ready <paramref name="LeadDays"/> after it starts and costing <paramref name="CostDays"/> capability-days per unit of the change (two
/// lodgers take two beds' work and price), and for a
/// temporary one the capability-days a day it still falls short of the permanent service (<paramref name="ResidualPerDay"/>).
/// </summary>
public sealed record ProvisionOption(string Kind, bool Temporary, double LeadDays, double CostDays, double ResidualPerDay = 0);

/// <summary>The comparison's pick for one expected change.</summary>
public abstract record ProspectiveChoice
{
    private protected ProspectiveChoice() { }

    /// <summary>The price the pick carries in capability-days.</summary>
    public abstract double Price { get; }

    /// <summary>The pick's name on its prospective_capacity row: defer, temporary or prepare.</summary>
    public abstract string Verdict { get; }

    /// <summary>The deferral's reason or the chosen provision's kind.</summary>
    public abstract string Why { get; }

    /// <summary>Nothing is prepared: the shortage costs less than any provision, or <paramref name="Reason"/> names the fact the price lacks.</summary>
    public sealed record Defer(string Reason, double Shortage) : ProspectiveChoice
    {
        public override double Price => Shortage;

        public override string Verdict => "defer";

        public override string Why => Reason;

        public override bool Due(Tick now) => false;
    }

    /// <summary>A temporary provision covers the stay.</summary>
    public sealed record Temporary(string Kind, long StartTick, double Total) : ProspectiveChoice
    {
        public override double Price => Total;

        public override string Verdict => "temporary";

        public override string Why => Kind;

        public override bool Due(Tick now) => StartTick <= now.Value;
    }

    /// <summary>A permanent provision starts at <paramref name="StartTick"/>, so it is ready when the change takes effect when its lead allows.</summary>
    public sealed record Prepare(string Kind, long StartTick, double Total) : ProspectiveChoice
    {
        public override double Price => Total;

        public override string Verdict => "prepare";

        public override string Why => Kind;

        public override bool Due(Tick now) => StartTick <= now.Value;
    }

    /// <summary>Whether the pick is a provision whose start is due at <paramref name="now"/>; a deferral never is.</summary>
    public abstract bool Due(Tick now);
}

/// <summary>
/// The expected changes whose permanent provision is due now, by owner: the units that join the station's sized population.
/// </summary>
public sealed record ProspectiveAdmission
{
    public static readonly ProspectiveAdmission None = new();

    public int Sleepers { get; init; }

    public int PrisonSleepers { get; init; }

    public int CribLeavers { get; init; }
}

/// <summary>
/// Prospective facility demand (RG-HABITAT, task 6.3): known arrivals, births, babies leaving the crib, the season's end and a
/// chain's later step are dated changes, each priced through one comparison against deferring. A change takes effect at its tick;
/// each provision is priced by its cost and the shortage loss over [the change, the provision ready], a temporary one also by its
/// residual shortfall over the rest of the stay; deferring is priced by the shortage over the stay (a permanent stay over
/// <see cref="ColonyModelPolicy.DefaultHorizonDays"/>, the tier-2 horizon). Deferring wins ties, so preparation wins only for an
/// assessed loss above its cost. No empty-bed or spare-station quota sizes anything: a change that is not expected adds nothing.
/// </summary>
public static class ProspectiveCapacityRules
{
    /// <summary>The comparison over <paramref name="options"/> for <paramref name="change"/> at <paramref name="now"/>.</summary>
    public static ProspectiveChoice Choose(ExpectedChange change, Tick now, IReadOnlyList<ProvisionOption> options)
    {
        if (change.At is ExpectedTiming.Unread { Fact: var unread }) return new ProspectiveChoice.Defer(unread, 0);
        if (change.At is not ExpectedTiming.Dated { Tick: var at }) return new ProspectiveChoice.Defer("no expected change", 0);
        if (change.Loss is ExpectedLoss.Unassessed { Fact: var loss }) return new ProspectiveChoice.Defer(loss, 0);
        if (change.Stay is ExpectedStay.Undated { Fact: var stay }) return new ProspectiveChoice.Defer(stay, 0);
        var perDay = ((ExpectedLoss.PerDay)change.Loss).CapabilityDays * change.Units;
        if (!double.IsFinite(perDay) || perDay < 0) throw new DomainException("expected change " + change.Source + " has no finite shortage loss");
        var horizon = (long)(ColonyModelPolicy.DefaultHorizonDays * GameTime.TicksPerDay);
        var start = Math.Max(at, now.Value);
        if (start - now.Value > horizon) return new ProspectiveChoice.Defer("beyond horizon", 0);
        var end = change.Stay is ExpectedStay.Until { Tick: var until } ? until : start + horizon;
        double Days(long from, long to) => Math.Max(0, to - from) / (double)GameTime.TicksPerDay;
        ProspectiveChoice best = new ProspectiveChoice.Defer("shortage below every provision", perDay * Days(start, end));
        if (end <= start) return best;
        foreach (var o in options)
        {
            if (!double.IsFinite(o.LeadDays) || o.LeadDays < 0 || !double.IsFinite(o.CostDays) || o.CostDays < 0 || !double.IsFinite(o.ResidualPerDay) || o.ResidualPerDay < 0)
                throw new DomainException("provision " + o.Kind + " has no finite lead, cost or residual");
            var count = Math.Ceiling(change.Units);
            var lead = (long)Math.Ceiling(o.LeadDays * count * GameTime.TicksPerDay);
            var cost = o.CostDays * count;
            // A provision starts no earlier than it must to be ready at the change, and never before now.
            var begin = Math.Max(now.Value, at - lead);
            var ready = begin + lead;
            var shortage = perDay * Days(start, Math.Min(ready, end));
            ProspectiveChoice choice = o.Temporary
                ? new ProspectiveChoice.Temporary(o.Kind, begin, cost + shortage + o.ResidualPerDay * change.Units * Days(Math.Max(ready, start), end))
                : new ProspectiveChoice.Prepare(o.Kind, begin, cost + shortage);
            if (choice.Price < best.Price) best = choice;
        }
        return best;
    }

    /// <summary>
    /// The colony's dated sleeping changes: each accepted hosting quest's lodgers not here yet (a joiners' quest stays for good,
    /// another's until its dated departure), each human pregnancy's crib and maternal bed rest at its birth tick, and each baby's own
    /// bed at the tick it leaves the crib. A bed's loss is the rest a sleep gains in the primary bed over the ground
    /// (<paramref name="bed"/>'s rest effectiveness): the baby's own, a lodger's the least of the present sleepers'. A crib's loss is
    /// unassessed (the furniture catalog has no crib row), and so is a mother's bed rest (no owner values a maternal bed rest).
    /// </summary>
    public static IReadOnlyList<ExpectedChange> SleepingChanges(ReviewFacts f, SleepingBedFacts? bed)
    {
        var output = new List<ExpectedChange>();
        var present = f.Sleeping.People.Concat(f.Sleeping.Slaves).Concat(f.Sleeping.Guests).Select(p => p.Id)
            .Concat(f.Population.Prisoners.Select(p => p.Pawn)).ToHashSet();
        var sleeper = SleeperRestLoss(f, bed);
        foreach (var offer in f.QuestOffers)
        {
            if (offer.State != RimGovernor.Brain.GameFacts.Receipts.QuestStatus.Ongoing
                || f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile } || !QuestHospitality.HospitalityFamily(profile.Family) || profile.Family == QuestFamily.HospitalityAnimals)
            {
                continue;
            }
            foreach (var objective in offer.Objectives)
            {
                if (objective.Kind != RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.HostLodgers || !objective.Active) continue;
                var absent = objective.PawnIds.Where(id => !present.Contains(id)).ToArray();
                if (absent.Length == 0) continue;
                var source = "quest:" + offer.Id.Value;
                output.Add(new(source, profile.Family == QuestFamily.HospitalityPrisoners ? ExpectedOwner.PrisonSleeper : ExpectedOwner.Sleeper, absent.Length,
                    objective.Arrival ?? new ExpectedTiming.Unread("arrival tick:" + offer.Id.Value),
                    profile.Family == QuestFamily.HospitalityJoiners ? new ExpectedStay.Permanent() : StayOf(objective.Departure, offer.Id.Value), sleeper));
            }
        }
        foreach (var person in f.Sleeping.People)
        {
            var pawn = person.Id.Value;
            if (person.Native.Labor is not ExpectedTiming.None)
            {
                output.Add(new("birth:" + pawn, ExpectedOwner.Crib, 1, person.Native.Labor, new ExpectedStay.Permanent(), new ExpectedLoss.Unassessed("crib catalog row")));
                output.Add(new("birth:" + pawn, ExpectedOwner.CareBed, 1, person.Native.Labor, new ExpectedStay.Undated("bed rest end:" + pawn), new ExpectedLoss.Unassessed("maternal bed rest valuation")));
            }
            if (person.Native.LeavesCrib is not ExpectedTiming.None)
            {
                output.Add(new("crib:" + pawn, ExpectedOwner.CribLeaver, 1, person.Native.LeavesCrib, new ExpectedStay.Permanent(), BedRestLoss(f, pawn, bed)));
            }
        }
        return output;
    }

    // A lodger's stay: until its dated departure, else undated naming the departure's unread fact.
    private static ExpectedStay StayOf(ExpectedTiming? departure, string quest)
    {
        if (departure is ExpectedTiming.Dated { Tick: var tick }) return new ExpectedStay.Until(tick);
        if (departure is ExpectedTiming.Unread { Fact: var fact }) return new ExpectedStay.Undated(fact);
        return new ExpectedStay.Undated("departure tick:" + quest);
    }

    // An expected sleeper's loss a day without a bed: the rest a present sleeper's sleep frees in the bed over the ground, the
    // sleeper Need's valuation of a present unbedded occupant (ForecastHousing.SleeperOf's RestDaysFreed). A pawn not here yet has
    // no needs row, so the least such loss over the present non-baby sleepers is used: a lower bound, the mood term left out.
    private static ExpectedLoss SleeperRestLoss(ReviewFacts f, SleepingBedFacts? bed)
    {
        ExpectedLoss loss = new ExpectedLoss.Unassessed("rest need of a present sleeper");
        foreach (var person in f.Sleeping.People.Concat(f.Sleeping.Slaves).Concat(f.Sleeping.Guests).Where(p => !p.Baby))
        {
            if (BedRestLoss(f, person.Id.Value, bed) is not ExpectedLoss.PerDay { CapabilityDays: var days }) continue;
            if (loss is not ExpectedLoss.PerDay { CapabilityDays: var least } || days < least) loss = new ExpectedLoss.PerDay(days);
        }
        return bed is null ? new ExpectedLoss.Unassessed("primary bed rest effectiveness") : loss;
    }

    // The capability-days a day a pawn's sleep frees in the bed over the ground (ForecastHousing.RestDaysFreed).
    private static ExpectedLoss BedRestLoss(ReviewFacts f, string pawn, SleepingBedFacts? bed)
    {
        if (bed is null) return new ExpectedLoss.Unassessed("primary bed rest effectiveness");
        var row = f.ForecastObserved.Needs?.Pawns.FirstOrDefault(p => p.Pawn == pawn);
        if (row?.Needs.FirstOrDefault(n => n.Kind == "rest") is not { } rest) return new ExpectedLoss.Unassessed("rest need:" + pawn);
        var planned = rest.GainPerTickPerEffectiveness * bed.RestEffectiveness;
        if (!double.IsFinite(rest.FallPerTick) || rest.FallPerTick < 0 || !double.IsFinite(rest.UnservedGainPerTick) || rest.UnservedGainPerTick <= 0
            || !double.IsFinite(planned) || planned <= 0)
            return new ExpectedLoss.Unassessed("rest rates:" + pawn);
        return new ExpectedLoss.PerDay(Math.Max(0, ForecastHousing.RestDaysFreed(rest, rest.UnservedGainPerTick, planned)));
    }

    /// <summary>
    /// The sleeping changes whose provision is due at <paramref name="f"/>'s tick, by owner: a change joins the sized population
    /// only when the comparison prepares it and its start has come. The permanent bed is <see cref="ReviewFacts.ProspectiveBed"/>
    /// (null while its price is unread, which provides nothing), resting as the primary bed's least restful built row
    /// (<see cref="ReviewFacts.SleepingBeds"/>). A newborn's crib and a mother's bed rest carry unassessed losses, so a birth defers.
    /// </summary>
    public static ProspectiveAdmission Admitted(ReviewFacts f)
    {
        var admitted = ProspectiveAdmission.None;
        foreach (var (change, choice) in SleepingChoices(f))
        {
            if (!choice.Due(f.Tick)) continue;
            var units = (int)Math.Ceiling(change.Units);
            // A crib's or a bed rest's provision is not sized by the station graph; only beds join its population.
            if (change.Owner == ExpectedOwner.Sleeper) admitted = admitted with { Sleepers = admitted.Sleepers + units };
            if (change.Owner == ExpectedOwner.PrisonSleeper) admitted = admitted with { PrisonSleepers = admitted.PrisonSleepers + units };
            if (change.Owner == ExpectedOwner.CribLeaver) admitted = admitted with { CribLeavers = admitted.CribLeavers + units };
        }
        return admitted;
    }

    /// <summary>Each expected sleeping change with the comparison's pick: beds priced as <see cref="ReviewFacts.ProspectiveBed"/>, a crib with no provision.</summary>
    public static IReadOnlyList<(ExpectedChange Change, ProspectiveChoice Choice)> SleepingChoices(ReviewFacts f)
    {
        IReadOnlyList<ProvisionOption> beds = f.ProspectiveBed is { } option ? [option] : [];
        var primary = f.Shapes.Furniture.PrimaryBed();
        var bed = f.SleepingBeds.Where(b => b.Def == primary && !b.Ground).OrderBy(b => b.RestEffectiveness).FirstOrDefault();
        return [.. SleepingChanges(f, bed).Select(change => (change, Choose(change, f.Tick, change.Owner == ExpectedOwner.Crib ? [] : beds)))];
    }

    /// <summary>
    /// The primary bed as a permanent provision: its work and cost list priced in capability-days (<paramref name="priceDays"/>,
    /// LayoutClusterRules.PiecePrice) and ready after its work spread over the colony's builders. Null while no builder exists.
    /// </summary>
    public static ProvisionOption? BedOption(double workTicks, double priceDays, int builders) => PieceOption("bed", workTicks, priceDays, builders);

    /// <summary>
    /// A built piece as a permanent provision named <paramref name="kind"/>: its price in capability-days and its work spread over
    /// the colony's builders as its lead. Null while no builder exists or the work or price is not finite.
    /// </summary>
    public static ProvisionOption? PieceOption(string kind, double workTicks, double priceDays, int builders) =>
        builders <= 0 || !double.IsFinite(workTicks) || workTicks < 0 || !double.IsFinite(priceDays) || priceDays < 0
            ? null
            : new(kind, false, workTicks / GameTime.TicksPerDay / builders, priceDays);

    /// <summary>
    /// The season's end as a dated hay demand: the growing season's remaining days from now; unread as "growing season" when
    /// the climate does not sow outdoors now.
    /// </summary>
    public static ExpectedChange HayChange(double need, CropClimate climate, Tick now) =>
        new("season end", ExpectedOwner.HayField, need,
            climate.SowingOutdoors() && double.IsFinite(climate.DaysRemaining) && climate.DaysRemaining >= 0
                ? new ExpectedTiming.Dated(now.Value + (long)Math.Floor(climate.DaysRemaining * GameTime.TicksPerDay))
                : new ExpectedTiming.Unread("growing season"),
            new ExpectedStay.Permanent(), new ExpectedLoss.Unassessed("hay shortage loss"));

    /// <summary>
    /// The power a selected spot-to-table chain's later step draws (RG-HABITAT-005): for each recipe the plan hosts at a bridge and
    /// at a permanent host of another definition, the host's draw (<paramref name="drawW"/>, positive only) once it is built, dated
    /// now when the bridge stands (the later step is next), unread as "bridge stands:&lt;bench&gt;" before. A host already standing or
    /// already pending (<paramref name="pending"/>: counted in the pending demand) adds nothing. Consume-only: the power Need reads it.
    /// A day the host stands unpowered is priced by the chain step's own throughput (<see cref="ChainStepLoss"/>).
    /// </summary>
    public static IReadOnlyList<ExpectedChange> ChainPower(LayoutPlan? plan, IReadOnlyCollection<string> standing, IReadOnlyCollection<string> pending, Func<string, double> drawW,
        IReadOnlyDictionary<string, BenchSpeedModel> speeds, IReadOnlyDictionary<string, RecipeFlow> recipes, Tick now)
    {
        var output = new List<ExpectedChange>();
        if (plan is null) return output;
        var made = plan.Rooms.SelectMany(r => r.Production).ToArray();
        foreach (var bridge in made.Where(p => p.Step == HostStep.Bridge).OrderBy(p => p.Recipe, StringComparer.Ordinal))
        {
            if (made.FirstOrDefault(p => p.Recipe == bridge.Recipe && p.Step == HostStep.Host && p.Bench != bridge.Bench) is not { } host) continue;
            var w = drawW(host.Bench);
            if (w <= 0 || standing.Contains(host.Bench) || pending.Contains(host.Bench) || output.Any(c => c.Source == host.Bench)) continue;
            output.Add(new(host.Bench, ExpectedOwner.Power, w,
                standing.Contains(bridge.Bench) ? new ExpectedTiming.Dated(now.Value) : new ExpectedTiming.Unread("bridge stands:" + bridge.Bench),
                new ExpectedStay.Permanent(), ChainStepLoss(bridge, host, speeds, recipes)));
        }
        return output;
    }

    /// <summary>
    /// What a day of the chain's later host standing unpowered costs: its planned runs stay at the bridge, each taking the recipe's
    /// work at the bridge's base speed instead of the host's (BenchSpeedModel.Base, the def's own factor), in capability-days. None
    /// when the host is no faster; unassessed naming an unread speed or recipe work.
    /// </summary>
    public static ExpectedLoss ChainStepLoss(PlannedProduction bridge, PlannedProduction host, IReadOnlyDictionary<string, BenchSpeedModel> speeds, IReadOnlyDictionary<string, RecipeFlow> recipes)
    {
        if (!speeds.TryGetValue(bridge.Bench, out var from) || !(from.Base > 0) || !double.IsFinite(from.Base)) return new ExpectedLoss.Unassessed("workbench speed:" + bridge.Bench);
        if (!speeds.TryGetValue(host.Bench, out var to) || !(to.Base > 0) || !double.IsFinite(to.Base)) return new ExpectedLoss.Unassessed("workbench speed:" + host.Bench);
        if (!recipes.TryGetValue(host.Recipe, out var recipe) || !(recipe.Work > 0) || !double.IsFinite(recipe.Work)) return new ExpectedLoss.Unassessed("workshop recipe work:" + host.Recipe);
        if (!double.IsFinite(host.RunsPerDay) || host.RunsPerDay < 0) throw new DomainException("planned runs a day are not finite: " + host.Recipe);
        return new ExpectedLoss.PerDay(host.RunsPerDay * recipe.Work * Math.Max(0, (1 / from.Base) - (1 / to.Base)) / GameTime.TicksPerDay);
    }
}

using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Utility resilience (task 6.4 of redefine-habitat-requirements, RG-HABITAT-008): a power network short of energy compares
// generation, storage, routing to a surplus network, consumer configuration and passive thermal provision against deferring,
// each priced by what the interruption actually costs the consumers it leaves unpowered. Nothing ranks generators, keeps a
// reserve of days or adds a storage margin: the 24 h energy balance (GenerationShortfallW's day shape) says how much energy goes
// unserved, the consumers' service models say what an unserved day costs, and the cheapest alternative over the horizon wins.

/// <summary>What an unpowered consumer stops providing: the service model its interruption is priced by.</summary>
public enum UtilityService
{
    /// <summary>A cooler holding perishable stock below the outdoor temperature (its spoilage).</summary>
    [WireName("refrigeration")]
    Refrigeration,

    /// <summary>A medical facility or bed serving patients.</summary>
    [WireName("care")]
    Care,

    /// <summary>A heater or cooler holding a bedroom's sleep band.</summary>
    [WireName("sleep")]
    Sleep,

    /// <summary>A production bench working its bills.</summary>
    [WireName("workshop")]
    Workshop,

    /// <summary>A consumer no service model prices (a lamp, a turret, a pending build).</summary>
    [WireName("unmodelled")]
    Unmodelled,
}

/// <summary>What one day of a consumer's interruption costs: capability-days, or unassessed naming the fact the price needs (rule 9).</summary>
public abstract record InterruptionLoss
{
    private protected InterruptionLoss() { }

    /// <summary>
    /// <see cref="CapabilityDays"/> is lost for every day the consumer stands unpowered; <see cref="OnceDays"/> is lost once, by the
    /// pending work the consumer serves (a queued surgery at a medical facility) if it stands unpowered when that work is done.
    /// </summary>
    public sealed record PerDay(double CapabilityDays) : InterruptionLoss
    {
        public double OnceDays { get; init; }
    }

    public sealed record Unassessed(string Fact) : InterruptionLoss;
}

/// <summary>
/// One consumer on a network: its draw, the service it provides and what a day without power costs. Switchable is false for
/// a consumer the colony cannot switch off: one without a flick switch, one the player's preferences leave alone, and a pending
/// or expected draw that is not built yet.
/// </summary>
public sealed record ConsumerInterruption(string Building, double DrawW, UtilityService Service, InterruptionLoss Loss)
{
    public bool Switchable { get; init; } = true;
}

/// <summary>A consumer whose interruption the prices leave out: the service it provides and the fact its loss needs.</summary>
public sealed record UnassessedConsumer(UtilityService Service, string Fact);


/// <summary>
/// One way to answer a network's shortfall. Defer is the baseline; Configure switches low-value consumers off (6.5 executes it);
/// Route joins a surplus network by conduit; Generate builds a generator; Store builds a battery; Shelter roofs the network's
/// rain-exposed equipment; Passive replaces a powered temperature control with an unpowered provision the thermal equation shows
/// meets its band.
/// </summary>
public enum UtilityChoiceKind { Defer, Configure, Route, Generate, Store, Shelter, Passive }

/// <summary>
/// A network's common-cause outage: none, an expected rate of events each blacking the network out for a span (a rain short
/// circuit draining an exposed bank, ShortCircuitUtility.DrainBatteriesAndCauseExplosion), or unassessed naming the rate the
/// price lacks. Every alternative that does not clear it carries it.
/// </summary>
public abstract record UtilityExposure
{
    private protected UtilityExposure() { }

    public sealed record None : UtilityExposure;

    public sealed record Expected(double EventsPerDay, double OutageDays) : UtilityExposure;

    public sealed record Unassessed(string Fact) : UtilityExposure;
}

/// <summary>
/// What an alternative changes once ready: producers, storage and a routed constant supply it adds, the consumers it switches
/// off or takes over, its cost in capability-days, the days until it is ready (charged as interruption while it is built), how long
/// after ready the added producers keep running (a finite fuel stock's runway) and whether it clears the network's exposure.
/// </summary>
public sealed record UtilityEffect
{
    public IReadOnlyList<PowerProducer> Producers { get; init; } = [];
    public double StorageWD { get; init; }
    public double SupplyW { get; init; }
    public IReadOnlySet<string> SwitchedOff { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The powered consumers an unpowered provision takes over: their draw leaves the network and their service goes on.</summary>
    public IReadOnlySet<string> Replaced { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public double CostDays { get; init; }
    public double LeadDays { get; init; }
    public double RunwayDays { get; init; } = double.PositiveInfinity;
    public bool ClearsExposure { get; init; }
}

/// <summary>An alternative's standing: assessed with its effect, excluded by a named constraint, or unassessed naming an unread fact.</summary>
public abstract record UtilityStanding
{
    private protected UtilityStanding() { }

    public sealed record Assessed(UtilityEffect Effect) : UtilityStanding;

    public sealed record Excluded(string Reason) : UtilityStanding;

    public sealed record Unassessed(string Fact) : UtilityStanding;
}

/// <summary>One compared alternative: its kind, what it acts on (a generator def, a battery, a network, a consumer list) and its standing.</summary>
public sealed record UtilityAlternative(UtilityChoiceKind Kind, string Subject, UtilityStanding Standing);

/// <summary>A network's energy state as the comparison sees it: its live producers, installed storage, consumers and exposure.</summary>
public sealed record UtilityNetwork
{
    public string Network { get; init; } = "";
    public IReadOnlyList<PowerProducer> Producers { get; init; } = [];
    public double StorageWD { get; init; }
    public IReadOnlyList<ConsumerInterruption> Consumers { get; init; } = [];
    public double ChargeEfficiency { get; init; }
    public bool Eclipse { get; init; }
    public UtilityExposure Exposure { get; init; } = new UtilityExposure.None();
    public double HorizonDays { get; init; } = ColonyModelPolicy.DefaultHorizonDays;
}

/// <summary>An alternative's price over the horizon: capability-days, or unpriced naming the fact or constraint that leaves it so.</summary>
public abstract record UtilityPrice
{
    private protected UtilityPrice() { }

    public sealed record Days(double Value) : UtilityPrice;

    public sealed record Unpriced(string Fact) : UtilityPrice;
}

/// <summary>An alternative with its price over the horizon.</summary>
public sealed record PricedUtility(UtilityAlternative Alternative, UtilityPrice Price);

/// <summary>
/// The comparison's pick for a network: every alternative with its price, the chosen one, and the facts of consumers whose
/// interruption is unassessed: their loss is left out of every price, so the priced loss is a lower bound.
/// </summary>
public sealed record UtilityDecision(EquatableArray<PricedUtility> Alternatives, PricedUtility Chosen, EquatableArray<UnassessedConsumer> Unassessed)
{
    /// <summary>The chosen alternative's kind.</summary>
    public UtilityChoiceKind Kind => Chosen.Alternative.Kind;

    /// <summary>
    /// Why the pick won: the chosen subject; for a deferral the fact that left it unpriced, else the first unassessed consumer fact
    /// when nothing was priced above zero, else that no alternative costs less.
    /// </summary>
    public string Reason => Kind != UtilityChoiceKind.Defer ? Chosen.Alternative.Subject
        : Chosen.Price is UtilityPrice.Unpriced unpriced ? unpriced.Fact
        : Unassessed.Count > 0 && Chosen.Price is UtilityPrice.Days { Value: 0 } ? Unassessed[0].Fact : "interruption below every alternative";
}

/// <summary>
/// The switch orders a network's configuration needs: the buildings to switch off and on, each one whose switch does not yet
/// want that state (PowerSwitch under Auto, task 6.5). Re-derived each review from the observed switches; recovery stays the
/// observed PowerOn of the consumers left on.
/// </summary>
public sealed record PowerConfiguration(EquatableArray<string> SwitchOff, EquatableArray<string> SwitchOn);

public static class UtilityChoiceRules
{
    private const double Tolerance = 1e-9;

    /// <summary>
    /// The energy a network fails to deliver over a 24 h day: the day's draw its producers (and a routed supply) do not cover, and
    /// the night's draw beyond night generation that the bank, filled from the day's surplus at the charge efficiency, does not
    /// cover. The same day shape as GenerationShortfallW; an eclipse zeroes solar producers.
    /// </summary>
    public static double UnservedWD(IReadOnlyList<PowerProducer> producers, double supplyW, double storageWD, double demandW, double efficiency, bool eclipse)
    {
        const double night = PowerRules.SolarNightFraction;
        const double day = 1 - PowerRules.SolarNightFraction;
        double nightW = supplyW, dayW = supplyW;
        foreach (var p in producers)
        {
            if (p.Profile.Solar && eclipse) continue;
            nightW += p.NominalW * p.Profile.Night;
            dayW += p.NominalW * (p.Profile.Daily - (p.Profile.Night * night)) / day;
        }
        var dayShort = Math.Max(0, demandW - dayW) * day;
        var surplus = Math.Max(0, dayW - demandW) * day;
        var nightShort = Math.Max(0, demandW - nightW) * night;
        return dayShort + Math.Max(0, nightShort - Math.Min(Math.Max(0, storageWD), surplus * efficiency));
    }

    /// <summary>
    /// The share of a day's demand left unserved: PowerNet.PowerNetTick sheds and restores a random consumer at a time while the
    /// net is short, so each consumer is off for that share of the day in expectation.
    /// </summary>
    public static double UnservedShare(IReadOnlyList<PowerProducer> producers, double supplyW, double storageWD, double demandW, double efficiency, bool eclipse) =>
        demandW <= 0 ? 0 : Math.Min(1, UnservedWD(producers, supplyW, storageWD, demandW, efficiency, eclipse) / demandW);

    /// <summary>
    /// Prices every alternative over the network's horizon and picks the cheapest priced one; deferring is the baseline and keeps
    /// ties. Deferring costs the unserved share of the consumers' assessed loss for the whole horizon. An alternative costs its
    /// price, the same interruption until it is ready, then the share it still leaves unserved (the consumers it switches off losing
    /// their whole day) while its producers run and the base share after their runway. The network's expected common-cause
    /// outages charge every alternative that does not clear them, deferring included; an unassessed exposure leaves those
    /// unpriced, and an unpriced deferral is chosen, naming the fact, because nothing can be compared against it. A consumer's
    /// pending work (InterruptionLoss.PerDay.OnceDays) is charged once, at the share unserved when it is done.
    /// </summary>
    public static UtilityDecision Choose(UtilityNetwork n, IReadOnlyList<UtilityAlternative> alternatives)
    {
        if (!double.IsFinite(n.HorizonDays) || n.HorizonDays <= 0) throw new DomainException("utility horizon must be positive");
        if (n.Exposure is UtilityExposure.Expected { EventsPerDay: var r, OutageDays: var o } && !(double.IsFinite(r) && r >= 0 && double.IsFinite(o) && o >= 0))
            throw new DomainException("utility exposure has no finite rate or outage");
        var unassessed = new List<UnassessedConsumer>();
        var loss = new Dictionary<string, double>(StringComparer.Ordinal);
        var once = new Dictionary<string, double>(StringComparer.Ordinal);
        double demand = 0;
        foreach (var c in n.Consumers)
        {
            if (!double.IsFinite(c.DrawW) || c.DrawW < 0) throw new DomainException("consumer " + c.Building + " has no finite draw");
            demand += c.DrawW;
            if (c.Loss is InterruptionLoss.PerDay { CapabilityDays: var days, OnceDays: var single })
            {
                if (!double.IsFinite(days) || days < 0 || !double.IsFinite(single) || single < 0)
                    throw new DomainException("consumer " + c.Building + " has no finite interruption loss");
                loss[c.Building] = loss.GetValueOrDefault(c.Building) + days;
                once[c.Building] = once.GetValueOrDefault(c.Building) + single;
            }
            else if (c.Loss is InterruptionLoss.Unassessed { Fact: var fact } && !unassessed.Any(u => u.Fact == fact))
            {
                unassessed.Add(new(c.Service, fact));
            }
        }
        var total = loss.Values.Sum();
        var baseShare = UnservedShare(n.Producers, 0, n.StorageWD, demand, n.ChargeEfficiency, n.Eclipse);
        var defer = Priced(n, new UtilityAlternative(UtilityChoiceKind.Defer, "", new UtilityStanding.Assessed(new UtilityEffect())), loss, once, demand, baseShare, total);
        var priced = new List<PricedUtility> { defer };
        var best = defer;
        foreach (var a in alternatives)
        {
            if (a.Kind == UtilityChoiceKind.Defer) throw new DomainException("deferring is the comparison's own baseline");
            var row = Priced(n, a, loss, once, demand, baseShare, total);
            priced.Add(row);
            if (defer.Price is UtilityPrice.Days { Value: var floor } && row.Price is UtilityPrice.Days { Value: var p } && best.Price is UtilityPrice.Days { Value: var current }
                && p < Math.Min(floor, current) - Tolerance) best = row;
        }
        return new UtilityDecision([.. priced], best, [.. unassessed]);
    }

    private static PricedUtility Priced(UtilityNetwork n, UtilityAlternative a, IReadOnlyDictionary<string, double> loss, IReadOnlyDictionary<string, double> once, double demand, double baseShare, double total)
    {
        if (a.Standing is UtilityStanding.Excluded { Reason: var reason }) return new(a, new UtilityPrice.Unpriced(reason));
        if (a.Standing is UtilityStanding.Unassessed { Fact: var fact }) return new(a, new UtilityPrice.Unpriced(fact));
        var e = ((UtilityStanding.Assessed)a.Standing).Effect;
        if (!double.IsFinite(e.CostDays) || e.CostDays < 0 || !double.IsFinite(e.LeadDays) || e.LeadDays < 0 || double.IsNaN(e.RunwayDays) || e.RunwayDays < 0
            || !double.IsFinite(e.StorageWD) || e.StorageWD < 0 || !double.IsFinite(e.SupplyW) || e.SupplyW < 0)
            throw new DomainException("utility alternative has no finite cost, lead, runway, storage or supply");
        if (!e.ClearsExposure && n.Exposure is UtilityExposure.Unassessed { Fact: var exposed }) return new(a, new UtilityPrice.Unpriced(exposed));
        var horizon = n.HorizonDays;
        var lead = Math.Min(e.LeadDays, horizon);
        var after = horizon - lead;
        var runway = Math.Min(after, e.RunwayDays);
        double offLoss = 0, offDraw = 0, replacedLoss = 0;
        foreach (var c in n.Consumers.Where(c => e.Replaced.Contains(c.Building)))
        {
            offDraw += c.DrawW;
            replacedLoss += loss.GetValueOrDefault(c.Building);
        }
        double offOnce = 0;
        foreach (var c in n.Consumers.Where(c => e.SwitchedOff.Contains(c.Building)))
        {
            offDraw += c.DrawW;
            offLoss += loss.GetValueOrDefault(c.Building);
            offOnce += once.GetValueOrDefault(c.Building);
        }
        var remaining = total - offLoss - replacedLoss;
        var producers = n.Producers.Concat(e.Producers).ToArray();
        var storage = n.StorageWD + e.StorageWD;
        var running = UnservedShare(producers, e.SupplyW, storage, demand - offDraw, n.ChargeEfficiency, n.Eclipse);
        var spent = UnservedShare(n.Producers, e.SupplyW, storage, demand - offDraw, n.ChargeEfficiency, n.Eclipse);
        // An outage blacks the whole network out: while it is being built the full loss, after it the consumers still on.
        var outage = n.Exposure is UtilityExposure.Expected { EventsPerDay: var rate, OutageDays: var span } ? rate * span : 0;
        var exposure = e.ClearsExposure ? outage * total * lead : outage * ((total * lead) + (remaining * after));
        // Pending work is done now, at the start of the window: before a lead it meets the network as it stands, a consumer
        // switched off loses it whole, and the rest meet the alternative's own unserved share.
        var single = once.Values.Sum();
        var pending = offOnce + ((lead > 0 ? baseShare : running) * (single - offOnce));
        return new(a, new UtilityPrice.Days(e.CostDays + (baseShare * total * lead) + (offLoss * after) + (running * remaining * runway) + (spent * remaining * (after - runway))
            + exposure + pending));
    }

    /// <summary>
    /// The configuration alternative: switch off the switchable consumers whose assessed loss per watt is least (ties by id)
    /// until the switched draw covers the network's unserved watts, never one whose loss is unassessed. Free and immediate;
    /// excluded when the assessed switchable consumers cannot cover the shortfall or there is none.
    /// </summary>
    public static UtilityAlternative Configuration(UtilityNetwork n)
    {
        var demand = n.Consumers.Sum(c => c.DrawW);
        var unserved = UnservedWD(n.Producers, 0, n.StorageWD, demand, n.ChargeEfficiency, n.Eclipse);
        if (unserved <= Tolerance) return new(UtilityChoiceKind.Configure, "", new UtilityStanding.Excluded("no_shortfall"));
        var off = new List<string>();
        double removed = 0;
        // Ranked by what switching off loses over the horizon: every day's loss and the pending work's once.
        foreach (var c in n.Consumers.Where(c => c.Switchable && c.Loss is InterruptionLoss.PerDay && c.DrawW > 0)
            .OrderBy(c => c.Loss is InterruptionLoss.PerDay { CapabilityDays: var days, OnceDays: var single } ? ((days * n.HorizonDays) + single) / c.DrawW : 0)
            .ThenBy(c => c.Building, StringComparer.Ordinal))
        {
            if (UnservedWD(n.Producers, 0, n.StorageWD, demand - removed, n.ChargeEfficiency, n.Eclipse) <= Tolerance) break;
            off.Add(c.Building);
            removed += c.DrawW;
        }
        if (UnservedWD(n.Producers, 0, n.StorageWD, demand - removed, n.ChargeEfficiency, n.Eclipse) > Tolerance)
            return new(UtilityChoiceKind.Configure, "", new UtilityStanding.Excluded("assessed_consumers_short"));
        return new(UtilityChoiceKind.Configure, string.Join(",", off), new UtilityStanding.Assessed(new UtilityEffect { SwitchedOff = off.ToHashSet(StringComparer.Ordinal) }));
    }

    /// <summary>A passive alternative's subject: the burner that would stand in for the control.</summary>
    public static string PassiveSubject(PassiveProvision provision) => provision.Definition + "@" + provision.Control;

    /// <summary>The consumers a chosen Configure alternative switches off, or null for any other choice.</summary>
    public static IReadOnlySet<string>? SwitchedOffBy(UtilityDecision d) =>
        d.Chosen.Alternative is { Kind: UtilityChoiceKind.Configure, Standing: UtilityStanding.Assessed { Effect: var e } } ? e.SwitchedOff : null;

    /// <summary>
    /// A passive thermal provision for a powered temperature control: the room's steady temperature with the unpowered provision
    /// (read from the thermal model, or unread naming it) inside the band frees the control's draw at the provision's cost and lead;
    /// outside the band it is excluded.
    /// </summary>
    public static UtilityAlternative Passive(PassiveProvision provision)
    {
        var (control, steady, bandMin, bandMax, costDays, leadDays) = provision;
        var subject = PassiveSubject(provision);
        if (steady is PassiveSteady.Unread { Fact: var fact }) return new(UtilityChoiceKind.Passive, subject, new UtilityStanding.Unassessed(fact));
        var t = ((PassiveSteady.Read)steady).TemperatureC;
        if (!double.IsFinite(t)) throw new DomainException("passive steady temperature is not finite: " + control);
        return t < bandMin || t > bandMax ? new(UtilityChoiceKind.Passive, subject, new UtilityStanding.Excluded("passive_out_of_band"))
            : new(UtilityChoiceKind.Passive, subject, new UtilityStanding.Assessed(new UtilityEffect
            {
                Replaced = new HashSet<string>(StringComparer.Ordinal) { control }, CostDays = costDays, LeadDays = leadDays,
            }));
    }
}

/// <summary>The map's rain rate a rain short circuit's chance scales with: read (WeatherManager.RainRate), or unread naming the fact.</summary>
public abstract record PowerRain
{
    private protected PowerRain() { }

    public sealed record Read(double Rate) : PowerRain;

    public sealed record Unread(string Fact) : PowerRain;
}

/// <summary>
/// The rain short circuit an unroofed rain-vulnerable member of a network is exposed to (ilspycmd, RimWorld 1.6): every 97 ticks
/// SteadyEnvironmentEffects rolls 2%, then RollForRainFire picks one of the colony's N rain-vulnerable buildings at random with a
/// 0.2 · N · RainRate chance; an unroofed pick short-circuits (ShortCircuitUtility.TryShortCircuitInRain: a powered trader, or a
/// battery holding over 100 Wd) in a radius-1.9 flame explosion. A damaged building keeps working, so the event's interruption is
/// the stored energy it burns: flame damage lights a battery's wick 5% of the time while it holds over 500 Wd
/// (Building_Battery.PostApplyDamage), and the wick's explosion draws 400 Wd.
/// </summary>
public static class RainShortCircuitRules
{
    private const double RollIntervalTicks = 97, RollChance = 0.02, PickChancePerBuilding = 0.2;
    private const double TraderOrBatteryMinWD = 100, WickMinWD = 500, WickChance = 0.05, WickDrawWD = 400;

    /// <summary>
    /// The network's expected rain short circuits a day and each one's outage, as the share of a day's demand the energy it burns
    /// would have served. None without an exposed member; unassessed while the rain rate is unread.
    /// </summary>
    /// <param name="exposed">The network's unroofed rain-vulnerable members: each one's stored energy (a battery) or none (a trader), and whether it can short now.</param>
    /// <param name="vulnerable">The colony's rain-vulnerable buildings, roofed or not: the pool RollForRainFire picks from.</param>
    public static UtilityExposure Exposure(IReadOnlyList<(bool Battery, double StoredWD, bool On)> exposed, int vulnerable, PowerRain rain, double demandW)
    {
        var live = exposed.Where(e => e.Battery ? e.StoredWD > TraderOrBatteryMinWD : e.On).ToArray();
        if (live.Length == 0) return new UtilityExposure.None();
        if (rain is PowerRain.Unread { Fact: var fact }) return new UtilityExposure.Unassessed(fact);
        var rate = ((PowerRain.Read)rain).Rate;
        if (!double.IsFinite(rate) || rate < 0 || vulnerable < exposed.Count || !double.IsFinite(demandW) || demandW < 0)
            throw new DomainException("rain short circuit has no finite rate, pool or demand");
        var perBuilding = GameTime.TicksPerDay / RollIntervalTicks * RollChance * Math.Min(1, PickChancePerBuilding * vulnerable * rate) / vulnerable;
        var events = perBuilding * live.Length;
        if (events <= 0) return new UtilityExposure.Expected(0, 0);
        var burned = live.Where(e => e.Battery && e.StoredWD > WickMinWD).Sum(e => WickChance * Math.Min(WickDrawWD, e.StoredWD)) / live.Length;
        return new UtilityExposure.Expected(events, demandW > 0 ? burned / demandW : 0);
    }
}

/// <summary>A room's steady temperature under an unpowered provision: read, or unread naming the fact.</summary>
public abstract record PassiveSteady
{
    private protected PassiveSteady() { }

    public sealed record Read(double TemperatureC) : PassiveSteady;

    public sealed record Unread(string Fact) : PassiveSteady;
}

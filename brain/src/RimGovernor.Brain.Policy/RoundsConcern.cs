namespace RimGovernor.Brain.Policy;

/// <summary>
/// An inspection's observed deficit and method availability. It does not reserve a worker; RimWorld
/// schedules pawn work. Mutable because the inspections adjust a raised concern in place.
/// </summary>
public sealed record RoundsConcern
{
    public ConcernId Id { get; set; }

    public int Priority { get; set; }

    public Fact<double> Deficit { get; set; }

    public bool MethodUnavailable { get; set; }
}

/// <summary>
/// The review-time native research state EnsureResearch's need is measured against. Projects lists every
/// native project the census exposes so a configured target absent from the game stays unknown rather
/// than an unbounded deficit.
/// </summary>
public sealed record ResearchFacts
{
    public ResearchProjectId Current { get; init; }

    public EquatableArray<ResearchProjectId> Finished { get; init; }

    public EquatableArray<ResearchProjectId> Projects { get; init; }

    /// <summary>The current project's only native lock is the research bench nobody has built, so it does not progress and the goal stays in deficit for the bench.</summary>
    public bool CurrentBenchMissing { get; init; }

    /// <summary>The knowledge project an empty Anomaly knowledge slot should fund now (KnowledgePick), empty when none is owed.</summary>
    public ResearchProjectId KnowledgePick { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Counts observed available workers, preserving incomplete facts.</summary>
    public static Fact<int> RoundsWorkers(IReadOnlyList<WorkPawn> pawns)
    {
        var count = 0;
        foreach (var p in pawns)
        {
            var known = p.Available.TryGetValue(out var available);
            var appliesKnown = p.Applies.TryGetValue(out var applies);
            if ((known && !available) || (appliesKnown && !applies))
            {
                continue;
            }
            if (!known || !appliesKnown)
            {
                return Unknown<int>();
            }
            count++;
        }
        return Known(count);
    }

    public static Fact<double> RoundsDeficit(ConcernId id, RoundsFacts f, RoundsPolicy p)
    {
        long stock = 0, target = 0;
        var known = false;
        if (id == MaintainHousing)
        {
            // The expansion phase: one indoor place beyond the population.
            var countKnown = f.Colonists.TryGetValue(out target);
            if (!countKnown || target <= 0 || target >= long.MaxValue)
            {
                return Unknown<double>();
            }
            target++;
            known = f.IndoorCapacity.TryGetValue(out stock);
        }
        else if (id == EnsureBasicDefense)
        {
            var countKnown = f.Colonists.TryGetValue(out target);
            target = Math.Min(target, 2);
            known = f.Armed.TryGetValue(out stock);
            known = known && countKnown;
            if (known && f.Unarmed.TryGetValue(out var unarmed) && unarmed > 0)
            {
                target = Math.Max(target, stock + unarmed);
            }
        }
        else if (id == EnsureDefensiveLayout)
        {
            // A configured layout is owed until every tier is observed standing.
            if (!p.DefensiveLayout)
            {
                return Unknown<double>();
            }
            if (Positive(f.DefensiveLayoutStanding))
            {
                return Known(0.0);
            }
            return Known(1.0);
        }
        else if (id == MaintainFoodStorage)
        {
            // Pending reserve access is observed as a full deficit.
            if (!f.FoodReserve.TryGetValue(out var reserve))
            {
                return Unknown<double>();
            }
            if (reserve.Hold.Count > 0 || reserve.Release.Count > 0)
            {
                return Known(1.0);
            }
            if (reserve.TargetNutrition <= 0)
            {
                return Known(0.0);
            }
            return Known(Math.Min(1.0, Math.Max(0.0, reserve.DeficitNutrition / reserve.TargetNutrition)));
        }
        else if (id == MaintainMedicalReserves)
        {
            // The reserve review supplies the measured stock and target.
            MedicalReserveReview review;
            try
            {
                review = ReviewMedicalReserve(f.MedicalReserve, f.IsIssued(MaintainMedicalReserves), p.MedicalReserve);
            }
            catch (DomainException)
            {
                return Unknown<double>();
            }
            known = review.Stock.TryGetValue(out stock);
            var targetKnown = review.Target.TryGetValue(out target);
            known = known && targetKnown;
        }
        else if (id == MaintainBurial)
        {
            // Census-driven: a tomb, grave or morgue step due is a full deficit.
            return OwedDeficit(f.BurialOwed);
        }
        else if (id == MaintainIncineration)
        {
            return OwedDeficit(f.IncinerationOwed);
        }
        else if (id == RemoveBlight)
        {
            // Census-driven like waste: any standing blighted plant is a full deficit until the census is empty.
            return OwedDeficit(BlightDeficit(f.Blight));
        }
        else if (id == EnsureMechCharger)
        {
            // A charger owed is a full deficit, none owed none.
            return OwedDeficit(f.MechChargerOwed);
        }
        else if (id == MaintainWorkLedger)
        {
            // A non-empty diff is a full deficit, an empty one none.
            return OwedDeficit(f.LedgerOwed);
        }
        else if (id == MaintainGeneBank)
        {
            // A bank owed is a full deficit, none owed none.
            return OwedDeficit(f.GeneBankOwed);
        }
        else if (id == ManagePollution)
        {
            // Census-driven like blight: any exposed or forbidden wastepack, or polluted cell outside the clear area, is a full deficit.
            return OwedDeficit(PollutionDeficit(f.Pollution));
        }
        else if (id == MaintainStockpiles)
        {
            if (!f.Stockpiles.TryGetValue(out var review) || !review.Known)
            {
                return Unknown<double>();
            }
            if (!review.Active)
            {
                return Known(0.0);
            }
            return Known(StockpileDeficit);
        }
        else
        {
            return Unknown<double>();
        }
        if (!known || target <= 0 || stock < 0)
        {
            return Unknown<double>();
        }
        return Known(Math.Min(1.0, Math.Max(0.0, ((double)target - stock) / target)));
    }

    /// <summary>A binary owed fact as a full or empty deficit; unknown stays unknown.</summary>
    private static Fact<double> OwedDeficit(Fact<bool> owed)
    {
        if (!owed.TryGetValue(out var v))
        {
            return Unknown<double>();
        }
        return Known(v ? 1.0 : 0.0);
    }

    /// <summary>
    /// The project EnsureResearch pursues: the configured target when one is set, else the first derived
    /// need the native research census lists and has not finished. Unknown facts keep the first derived
    /// need so the deficit stays unknown rather than recovered; no need at all is no target.
    /// </summary>
    public static string ResearchConcernTarget(string configured, IReadOnlyList<string> derived, Fact<ResearchFacts> facts)
    {
        if (configured != "" || derived.Count == 0)
        {
            return configured;
        }
        if (!facts.TryGetValue(out var f))
        {
            return derived[0];
        }
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Projects)
        {
            listed.Add(name.Value);
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Finished)
        {
            finished.Add(name.Value);
        }
        foreach (var name in derived)
        {
            if (listed.Contains(name) && !finished.Contains(name))
            {
                return name;
            }
        }
        return "";
    }

    /// <summary>
    /// The roadmap EnsureResearch walks when neither an operator target nor a workshop need names a project:
    /// the early research direction a new colony needs on its own (stone blocks, then power with its
    /// storage, geothermal and solar rungs, the medieval crafts, then Machining simple firearms, then the
    /// ChargedShot and BeamWeapons gates of the training tiers; BeamWeapons is Odyssey's, so the census
    /// omits and the walk skips it without), with Microelectronics and the multi-analyzer after the crafts:
    /// the hi-tech bench and analyzer the lab is planned for. Rungs the native census does not list
    /// (another mod set) are skipped.
    /// </summary>
    public static string[] DefaultResearchLadder() =>
        ["Stonecutting", "Electricity", "Batteries", "GeothermalPower", "SolarPanels", "Smithing", "CarpetMaking", "ComplexClothing", "MicroelectronicsBasics", "MultiAnalyzer", "Machining", "Gunsmithing", "ChargedShot", "BeamWeapons"];

    /// <summary>
    /// Resolves the project EnsureResearch pursues and whether it is a workshop need (derived): the first
    /// unfinished project the workshop ladder recorded, then the first unfinished rung of
    /// RoundsPolicy.ResearchLadder. A ladder rung is only walked under a known research census: the ladder
    /// is a default, not a declared need, and without the census there is nothing to measure it against.
    /// </summary>
    public static (string Target, bool Derived) ResearchConcern(RoundsPolicy p, IReadOnlyList<string> needs, Fact<ResearchFacts> facts)
    {
        var target = ResearchConcernTarget("", needs, facts);
        if (target != "")
        {
            return (target, true);
        }
        if (!facts.TryGetValue(out _))
        {
            return ("", false);
        }
        return (ResearchConcernTarget("", p.ResearchLadder, facts), false);
    }

    /// <summary>
    /// Names the first project of required that the native census has not finished: the research a goal's
    /// only method waits on. Unknown facts keep the first requirement; none unfinished is no gate.
    /// </summary>
    public static string ResearchGate(IReadOnlyList<string> required, Fact<ResearchFacts> facts)
    {
        if (required.Count == 0)
        {
            return "";
        }
        if (!facts.TryGetValue(out var f))
        {
            return required[0];
        }
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in f.Finished)
        {
            finished.Add(name.Value);
        }
        foreach (var name in required)
        {
            if (!finished.Contains(name))
            {
                return name;
            }
        }
        return "";
    }

    /// <summary>
    /// Measures EnsureResearch: no target is certain recovery; a finished target is recovered; an idle
    /// research tab with the target unfinished is a full deficit. A current native project also recovers a
    /// configured target (player-chosen research is respected, never replaced), but not a derived one:
    /// another goal's ladder waits on that project, so the deficit stands, and the research planner asks the
    /// clock for ticks while any project is current, until it is finished. Missing facts stay unknown.
    /// </summary>
    public static (Fact<bool> Recovered, Fact<double> Deficit) ResearchTargetNeed(string target, bool derived, Fact<ResearchFacts> facts)
    {
        if (target == "")
        {
            return (Known(true), Known(0.0));
        }
        if (!facts.TryGetValue(out var f))
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        var listed = false;
        foreach (var name in f.Projects)
        {
            listed = listed || name.Value == target;
        }
        if (!listed)
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        foreach (var name in f.Finished)
        {
            if (name.Value == target)
            {
                return (Known(true), Known(0.0));
            }
        }
        if (f.Current.Value is { Length: > 0 } && !derived && !f.CurrentBenchMissing)
        {
            return (Known(true), Known(0.0));
        }
        return (Known(false), Known(1.0));
    }

    /// <summary>
    /// Measures MaintainResource against the generic resource census: the deficit is the worst-covered
    /// configured target's shortfall fraction, the same proportion SelectResourceTarget dispatches on. No
    /// configured target is certain recovery; unknown stock stays unknown.
    /// </summary>
    public static (Fact<bool> Recovered, Fact<double> Deficit) ResourceTargetNeed(IReadOnlyDictionary<Resource, long>? targets, Fact<EquatableArray<Amount>> stock)
    {
        if (targets is null || targets.Count == 0)
        {
            return (Known(true), Known(0.0));
        }
        if (!stock.TryGetValue(out var rows))
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        Resource resource;
        long target;
        bool ok;
        try
        {
            (resource, target, ok) = SelectResourceTarget(targets, stock);
        }
        catch (DomainException)
        {
            return (Unknown<bool>(), Unknown<double>());
        }
        if (!ok)
        {
            return (Known(true), Known(0.0));
        }
        long have = 0;
        foreach (var q in rows)
        {
            if (q.Resource == resource)
            {
                have = q.Count;
            }
        }
        return (Known(false), Known(Math.Min(1.0, Math.Max(0.0, (double)(target - have) / target))));
    }
}

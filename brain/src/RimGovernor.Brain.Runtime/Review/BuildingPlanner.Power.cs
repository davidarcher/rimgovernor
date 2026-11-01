using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    public static BuildingPlanner NewReviewPowerPlanner(ColonyReview reviewer, IReviewBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewReviewPowerPlanner: reviewer or native is missing");
        }

        if (native is not IReviewSource)
        {
            throw new ControlException("NewReviewPowerPlanner: native is not a rounds source");
        }

        return new BuildingPlanner { Reviewer = reviewer, Native = native, Concern = PowerFamily.Basic.FilingKey, Need = PowerFamily.Basic };
    }

    /// <summary>The planner resolved to the power method the policy picked, or the verdict that says why none applies.</summary>
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectPower(ColonyReading facts, IReadOnlyList<string> pendingConsumers, PowerValuation valuation)
    {
        var planning = new PowerPlanning
        {
            Restorable = PowerRestorable, Valuation = valuation,
            Protected = facts.Facts.Preferences.ProtectedThings.ToHashSet(StringComparer.Ordinal),
        };
        var crops = FieldPlanner.CropChoices(facts);
        var biome = facts.Facts.CropClimate.Biome;
        var skill = TreePlantationRules.GrowerSkill(facts.Facts.ForecastObserved.WorkPawns);
        var generators = ColonyPlanningRules.GeneratorOptions(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), fuel => TreePlantationRules.ResourceGrowable(crops, fuel, biome, skill));
        planning = planning with
        {
            Generators = generators.Select(option => option with { Available = option.Available && ComfortBuilderAvailable(facts, option.Definition) }).ToList(),
            DaylightHours = facts.Facts.Gear.Climate.DaylightHoursIn(facts.Facts.Gear.Climate.CurrentTwelfth),
            Sources = facts.Facts.PowerSources,
            Battery = facts.Facts.PowerBattery,
            Light = facts.Facts.LightTerrains,
            BatteryAvailable = ColonyPlanningRules.DefinitionAvailable(facts.Facts, PowerRules.BatteryDefinition),
            PendingDemandW = BuildingPlanner.PendingDemand(facts, pendingConsumers),
            Expected = ProspectiveCapacityRules.ChainPower(facts.Facts.LayoutPlan, facts.Facts.BuiltBuildings.Where(b => b.Count > 0).Select(b => b.Resource.Value).ToHashSet(StringComparer.Ordinal),
                pendingConsumers, def => BuildingPlanner.PendingDemand(facts, [def]), facts.Facts.WorkBenchSpeeds, facts.Facts.ProductionRecipes, facts.Facts.Tick),
            Now = facts.Facts.Tick,
            Rain = facts.Facts.ForecastObserved.Needs is { } needs ? new PowerRain.Read(needs.RainRate) : new PowerRain.Unread("rain rate"),
        };
        if (facts.Facts.LayoutPlan is { } plan)
        {
            planning = planning with { Net = PowerRules.PlannedNetOf(plan) };
        }

        var proposal = PowerRules.SelectPowerMethod(facts.Facts.EssentialInputs.Power, facts.Facts.MapBounds, facts.PlanningWindow.Cells, [], planning);
        switch (proposal.Method)
        {
            case PowerMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case PowerMethod.RouteBlocked:
                return (null, NoSpace("power_route"));
            case PowerMethod.NoGenerator:
            {
                var gate = ResearchRules.ResearchGate(BuildingPlanner.GeneratorResearch(facts), facts.Facts.Research);
                if (gate != "")
                {
                    return (null, ResearchWait(gate));
                }

                return (null, AwaitingMethod(proposal.Method));
            }

            case PowerMethod.WaitOutput:
            case PowerMethod.Restore:
            case PowerMethod.WaitBlackout:
            case PowerMethod.WaitCharge:
                return (null, AwaitingMethod(proposal.Method));
            // An ordered switch waits for its flick; a configuration that holds keeps its consumers off. Neither orders anything.
            case PowerMethod.WaitSwitch:
                return (null, WaitFor(Cause.ExistingWork, Wire.Name(proposal.Method)));
            case PowerMethod.Configured:
                return (null, WaitFor(Cause.MethodUsed, Wire.Name(proposal.Method)));
            // A deferral waits on its named fact: its pick and the consumers whose unassessed interruption the prices leave out.
            case PowerMethod.Defer:
                return (null, AwaitingPlan(Wire.Name(proposal.Method), UtilityDetail(proposal)));
        }

        var resolved = Copy();
        resolved.Power = proposal;
        resolved.Definition = Wire.Name(proposal.Method);
        resolved.Environment = PlacementEnvironment.Anywhere;
        switch (proposal.Method)
        {
            case PowerMethod.Generate:
                resolved.Definition = proposal.Definition;
                resolved.Stuff = facts.Facts.GeneratorRows[proposal.Definition].Stuff;
                break;
            case PowerMethod.Store:
                resolved.Definition = proposal.Definition;
                resolved.Environment = PlacementEnvironment.Indoors;
                break;
            case PowerMethod.Passive:
                // The unpowered burner stands beside the control it replaces, in its room.
                resolved.Definition = proposal.Definition;
                resolved.Environment = PlacementEnvironment.Indoors;
                break;
        }

        if (proposal.Method == PowerMethod.Shelter)
        {
            resolved.Definition = "Wall";
        }

        return (resolved, default);
    }

    // The comparison a deferral reports: the pick's reason, then each unassessed consumer's service and fact.
    private static string UtilityDetail(PowerProposal proposal)
    {
        var decision = proposal.Decision ?? throw new DomainException("power method " + proposal.Method + " carries no utility decision");
        return string.Join(";", [decision.Reason, .. decision.Unassessed.Select(u => Wire.Name(u.Service) + ":" + u.Fact)]);
    }

    /// <summary>The MethodId prefix of a power switch configuration (PowerRules.SelectPowerMethod's configure key).</summary>
    internal const string PowerSwitchMethod = "configure-";

    /// <summary>
    /// Commits a configure proposal's switch orders as one owner Method (<see cref="PowerSwitchPlan"/>) unless
    /// <see cref="PowerSwitchHold"/> holds it.
    /// </summary>
    internal BuildingResult CommitPowerSwitch(NeedOwner goal, GenerationSnapshot world, Action check, CancellationToken call)
    {
        var p = Reviewer.Player;
        var proposal = Power ?? throw new ControlException("commitPowerSwitch: no power proposal");
        var configuration = proposal.Configuration ?? throw new ControlException("commitPowerSwitch: not a configure proposal");
        if (proposal.Method != PowerMethod.Configure || configuration.SwitchOff.Count + configuration.SwitchOn.Count == 0)
        {
            throw new ControlException("commitPowerSwitch: a configure proposal orders at least one switch");
        }
        if (PowerSwitchHold(p.Journal, goal, proposal.Key, world, call) is { } held)
        {
            return new BuildingResult { Verdict = held };
        }
        var spec = PowerSwitchPlan(PlanIds.MintPlanId(), configuration);
        check();
        return new BuildingResult { Verdict = OwnerCommitVerdict(p.Journal.CommitOwnerWork(goal, proposal.Key, "", spec, call)) };
    }

    /// <summary>
    /// Why the switch orders under <paramref name="key"/> are not committed now, or null to commit them: an open attempt continues
    /// (power_switch_method); a refused one is admitted again only while the refusal budget allows it; an accepted one whose
    /// switches still do not want the ordered state, with no other configuration bound since, is Unconfirmed
    /// (power_switch_unconfirmed): the facts are re-derived next review and the same orders are never re-issued blindly.
    /// </summary>
    internal static Verdict? PowerSwitchHold(StoreJournal journal, NeedOwner goal, MethodId key, GenerationSnapshot world, CancellationToken call)
    {
        if (journal.OwnerMethodPlan(goal, key, call) is not { } latest)
        {
            return null;
        }
        var plan = journal.LoadPlan(latest, call);
        if (!plan.Retired && WorkProgress.Open(plan.Progress))
        {
            return WaitFor(Cause.MethodUsed, "power_switch_method");
        }
        if (plan.Progress.Any(x => x.View.Stage == Stage.Unsuccessful))
        {
            var (verdict, admitted) = ReviewRefusalBudget.AdmitLatestAttempt(journal, goal, key, world, call);
            return admitted ? null : verdict;
        }
        var newest = journal.OwnerMethodHistory(goal, call).LastOrDefault(m => m.Method.Value.StartsWith(PowerSwitchMethod, StringComparison.Ordinal));
        return newest is not null && newest.Method == key ? WaitFor(Cause.MethodUsed, "power_switch_unconfirmed") : null;
    }

    /// <summary>One PowerSwitch per ordered building: the switches off first, then on, each in id order.</summary>
    internal static PlanSpec PowerSwitchPlan(PlanId id, PowerConfiguration configuration)
    {
        var orders = configuration.SwitchOff.Select(t => new PowerSwitch(t, false)).Concat(configuration.SwitchOn.Select(t => new PowerSwitch(t, true)));
        return new PlanSpec(id, new PlanRevision(1), [.. orders.Select((o, i) => DomainAction.NewPowerSwitchAction(new ActionId($"{id}-{i}"), o))]);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerShelterAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var wall = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, "Wall");
        var door = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, "Door");
        if (wall is null || door is null)
        {
            return ([], stock, FieldUnavailable("wall_door_definitions"));
        }

        var stuff = AnimalContainment.ShellSharedStuff(facts, wall, door);
        if (stuff is null)
        {
            return ([], stock, FieldUnavailable("wall_door_stuff"));
        }

        var room = Power!.Room;
        var entry = new Cell(room.X + (room.Width / 2), room.Z);
        var perimeter = new List<Cell> { entry };
        for (var x = room.X; x < room.X + room.Width; x++)
        {
            for (var z = room.Z; z < room.Z + room.Height; z++)
            {
                var c = new Cell(x, z);
                if (c != entry && (x == room.X || x == room.X + room.Width - 1 || z == room.Z || z == room.Z + room.Height - 1))
                {
                    perimeter.Add(c);
                }
            }
        }

        var blocked = protectedCells.ToHashSet();
        var selected = new List<Preview>();
        for (var i = 0; i < perimeter.Count; i++)
        {
            var c = perimeter[i];
            if (blocked.Contains(c))
            {
                return ([], stock, BuildingReasonExistingWork);
            }

            check();
            var name = i == 0 ? "Door" : "Wall";
            var b = new Building(name, c, Rotation.North, stuff);
            var a = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), b, ConstructionTierRules.PlannerTier(Concern, Phase));
            var p = await Native.PreviewBuildingAsync(a, snapshot, ct).ConfigureAwait(false);
            var v = p.Preview;
            if (v.Footprint.Count != 1 || v.Footprint[0] != c || !v.CanPlace || !v.SafeToPlace || v.MadeFromStuff != (stuff != ""))
            {
                return ([], stock, NoSpace("power_shelter_cell"));
            }

            stock = MergeReviewStock(stock, p.Stock);
            selected.Add(v);
        }

        return (selected, stock, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerSiteAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Power is null || !Power.FixedSite())
        {
            throw new ControlException("previewPowerSite: no fixed power site");
        }

        check();
        var building = new Building(Definition, Power.Center, Rotation.North, Stuff);
        var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
        var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
        var p = preview.Preview;
        var blocked = protectedCells.ToHashSet();
        foreach (var c in p.Footprint)
        {
            if (blocked.Contains(c))
            {
                return ([], stock, BuildingReasonExistingWork);
            }
        }

        if (p.MadeFromStuff != (Stuff != "") || p.Footprint.Count == 0 || !p.CanPlace || !p.SafeToPlace)
        {
            return ([], stock, NoSpace("geyser_site"));
        }

        stock = MergeReviewStock(stock, preview.Stock);
        return ([p], stock, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerOrSearchAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, long missing, Action check, CancellationToken call)
    {
        if (Power is not null && Power.Method is PowerMethod.Generate)
        {
            var (selected, stock, ok) = await PreviewPlannedPowerAsync(snapshot, facts, protectedCells, missing, check, call).ConfigureAwait(false);
            if (ok)
            {
                return (selected, stock, default);
            }
        }

        var (found, searched, verdict) = await PreviewSearchAsync(snapshot, facts, protectedCells, missing, check, call).ConfigureAwait(false);
        return (found ?? [], searched, verdict);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, bool Ok)> PreviewPlannedPowerAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, long missing, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var plan = facts.Facts.LayoutPlan;
        var planned = plan is not null;
        var tier = TechTierRules.SelectTechTier(facts.Tables.FinishedResearch.Select(project => new ResearchProjectId(project)).ToArray(), facts.Snapshot.PlayerTechLevel);
        if (!planned || tier < TechTier.Masonry || missing < 1)
        {
            return ([], stock, false);
        }

        var sites = LayoutPlan.PlannedPowerSites(plan!, Definition);
        if (sites.Count == 0)
        {
            return ([], stock, false);
        }

        var blocked = protectedCells.ToHashSet();
        var built = new HashSet<Cell>();
        var census = facts.Facts.CurrentConstruction;
        foreach (var b in census.Buildings)
        {
            foreach (var c in b.Cells)
            {
                built.Add(c);
            }
        }

        var n = 0;
        var merged = 0;
async 
        Task<(Preview Preview, bool Ok)> PreviewOneAsync(Cell c, Rotation rot, Rectangle area)
        {
            check();
            var b = new Building(Definition, c, rot, Stuff);
            var a = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{n}"), b, ConstructionTierRules.PlannerTier(Concern, Phase));
            n++;
            var p = await Native.PreviewBuildingAsync(a, snapshot, ct).ConfigureAwait(false);
            var v = p.Preview;
            if (!v.CanPlace || !v.SafeToPlace || v.MadeFromStuff != (Stuff != "") || !BuildingPlanner.SameCells(v.Footprint, LayoutUtilityRules.RectangleCells(area)))
            {
                return (default!, false);
            }

            if (v.Wind is RimGovernor.Brain.GameFacts.Observations.BuildingWind.WindTurbine && !PreviewRules.ClearWindTurbine(v))
            {
                return (default!, false);
            }

            stock = MergeReviewStock(stock, p.Stock);
            merged++;
            return (v, true);
        }

        var selected = new List<Preview>();
        long placed = 0;
        foreach (var s in sites)
        {
            if (placed == missing)
            {
                break;
            }

            var cells = LayoutUtilityRules.RectangleCells(s.Area);
            var free = true;
            foreach (var c in cells)
            {
                free = free && !blocked.Contains(c) && !built.Contains(c);
            }

            if (!free)
            {
                continue;
            }

            var (v, ok) = await PreviewOneAsync(s.Cell, s.Rotation, s.Area).ConfigureAwait(false);
            if (!ok)
            {
                continue;
            }

            selected.Add(v);
            placed++;
        }

        if (placed < missing)
        {
            return ([], stock, false);
        }

        return (selected, stock, true);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerRouteAsync(GenerationSnapshot snapshot, ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Power is null || Power.Method != PowerMethod.Connect || Power.Cells.Count < 1)
        {
            throw new ControlException("previewPowerRoute: no connect route");
        }

        var blocked = protectedCells.ToHashSet();
        var selected = new List<Preview>();
        for (var i = 0; i < Power.Cells.Count; i++)
        {
            var cell = Power.Cells[i];
            if (blocked.Contains(cell))
            {
                return ([], stock, BuildingReasonExistingWork);
            }

            check();
            var building = new Building(Wire.Name(PowerMethod.Connect), cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
            var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var p = preview.Preview;
            if (p.MadeFromStuff || p.Footprint.Count != 1 || p.Footprint[0] != cell || !p.CanPlace || !p.SafeToPlace)
            {
                return ([], stock, NoSpace("power_route_cell"));
            }

            selected.Add(p);
            stock = MergeReviewStock(stock, preview.Stock);
        }

        return (selected, stock, default);
    }
}

partial class BuildingPlanner
{
    internal static IReadOnlyList<string> PendingBuildingDefinitions(IReadOnlyList<Reservation> held)
    {
        var output = new List<string>();
        foreach (var h in held)
        {
            if (h.Progress.Action.Building is not { } b || !PendingWork.IsPending(h.Progress))
            {
                continue;
            }

            if (!output.Contains(b.Definition, StringComparer.Ordinal))
                output.Add(b.Definition);
        }

        return output;
    }

    internal static double PendingDemand(ColonyReading facts, IReadOnlyList<string> pending)
    {
        var draw = new Dictionary<string, double>();
        foreach (var d in facts.Facts.Definitions)
        {
            if (d.Power is not { } power) continue;
            var w = power.PowerW;
            if (w > 0)
            {
                draw[d.Name] = w;
            }
        }

        var total = 0.0;
        foreach (var name in pending)
        {
            total += draw.GetValueOrDefault(name);
        }

        return total;
    }

    internal static IReadOnlyList<string> GeneratorResearch(ColonyReading facts)
    {
        var definitions = new Dictionary<string, PlanningDefinition>();
        foreach (var d in facts.Facts.Definitions)
        {
            definitions[d.Name] = d;
        }

        var seen = new HashSet<string>();
        var required = new List<string>();
        foreach (var name in facts.Facts.GeneratorRows.Keys.Order(StringComparer.Ordinal))
        {
            if (!definitions.TryGetValue(name, out var d))
            {
                continue;
            }

            var available = d.Available;

            if (available)
            {
                continue;
            }

            foreach (var project in d.Research)
            {
                if (seen.Add(project))
                {
                    required.Add(project);
                }
            }
        }

        return required;
    }

    internal static bool SameCells(IReadOnlyList<Cell> a, IReadOnlyList<Cell> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var set = a.ToHashSet();
        return b.All(set.Contains);
    }
}

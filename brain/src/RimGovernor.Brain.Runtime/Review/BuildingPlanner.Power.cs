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
    internal (BuildingPlanner? Planner, Verdict Verdict) SelectPower(ColonyReading facts, IReadOnlyList<string> pendingConsumers)
    {
        var planning = PowerRules.DefaultPowerPlanning(facts.Facts.ComponentSteel ?? throw new RequiredFactException("component steel cost"));
        var crops = FieldPlanner.CropChoices(facts);
        var biome = facts.Facts.CropClimate.Biome;
        var skill = TreePlantationRules.GrowerSkill(facts.Facts.ForecastObserved.WorkPawns);
        var generators = ColonyPlanningRules.GeneratorOptions(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), fuel => TreePlantationRules.ResourceGrowable(crops, fuel, biome, skill));
        planning = planning with
        {
            Generators = generators,
            DaylightHours = facts.Facts.Gear.Climate.DaylightHoursIn(facts.Facts.Gear.Climate.CurrentTwelfth),
            Sources = facts.Facts.PowerSources,
            Battery = facts.Facts.PowerBattery,
            Light = facts.Facts.LightTerrains,
            BatteryAvailable = ColonyPlanningRules.DefinitionAvailable(facts.Facts, PowerRules.BatteryDefinition),
            GeothermalAvailable = ColonyPlanningRules.DefinitionAvailable(facts.Facts, PowerRules.GeothermalDefinition),
            PendingDemandW = BuildingPlanner.PendingDemand(facts, pendingConsumers),
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
            case PowerMethod.WaitFuel:
            case PowerMethod.WaitRepair:
            case PowerMethod.WaitBlackout:
            case PowerMethod.WaitPlayer:
            case PowerMethod.WaitCharge:
                return (null, AwaitingMethod(proposal.Method));
        }

        var resolved = Copy();
        resolved.Power = proposal;
        resolved.Definition = Wire.Name(proposal.Method);
        resolved.Environment = PlacementEnvironment.Anywhere;
        switch (proposal.Method)
        {
            case PowerMethod.Generate:
                resolved.Definition = proposal.Definition;
                break;
            case PowerMethod.Store:
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
        var building = new Building(Definition, Power.Center, Rotation.North, "");
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

        if (p.MadeFromStuff || p.Footprint.Count == 0 || !p.CanPlace || !p.SafeToPlace)
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

            if (Definition == PowerRules.WindTurbineDefinition && !PreviewRules.ClearWindTurbine(v))
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
            if (h.Progress.Action.Building is not { } b || !PendingWork.IsPending(h.Progress) || PowerRules.PowerFamilyDefinitions().Contains(b.Definition))
            {
                continue;
            }

            if (output.Count == 64)
            {
                break;
            }

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
        foreach (var name in PowerRules.GeneratorDefinitions)
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

    internal static bool PowerDefinition(string name) =>
        name is "PowerConduit" or "HiddenConduit" or "WaterproofConduit" || PowerRules.PowerFamilyDefinitions().Contains(name);

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

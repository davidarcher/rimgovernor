namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    public static RoundsBuildingPlanner NewRoundsPowerPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsPowerPlanner: reviewer or native is missing");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsPowerPlanner: native is not a rounds source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyFunctions.EnsureBasicPower };
    }

    /// <summary>The planner resolved to the power method the policy picked, or the verdict that says why none applies.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectPower(ColonyProjection facts, IReadOnlyList<string> pendingConsumers)
    {
        var planning = PolicyFunctions.DefaultPowerPlanning();
        var crops = BuildingRuntimePackage.CropChoices(facts);
        var biome = facts.CropClimate.Biome;
        var skill = PolicyFunctions.GrowerSkill(facts.WorkPawns);
        var generators = facts.GeneratorOptions(fuel => PolicyFunctions.ResourceGrowable(crops, fuel, biome, skill));
        planning = planning with
        {
            Generators = generators,
            ComponentSteel = facts.ComponentSteel,
            Sources = facts.PowerSources,
            Battery = facts.PowerBattery,
            Light = facts.LightTerrains,
            BatteryAvailable = facts.DefinitionAvailable(PolicyConstants.BatteryDefinition),
            GeothermalAvailable = facts.DefinitionAvailable(PolicyConstants.GeothermalDefinition),
            PendingDemandW = BuildingRuntimePackage.PendingDemand(facts, pendingConsumers),
        };
        if (facts.LayoutPlan.TryGetValue(out var plan))
        {
            planning = planning with { Net = PolicyFunctions.PlannedNetOf(plan) };
        }

        var proposal = PolicyFunctions.SelectPowerMethod(facts.PowerPlanning, facts.Bounds, facts.Cells, [], planning);
        switch (proposal.Method)
        {
            case PowerMethod.Unknown:
                return (null, FieldUnavailable("power"));
            case PowerMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case PowerMethod.RouteBlocked:
                return (null, NoSpace("power_route"));
            case PowerMethod.NoGenerator:
            {
                var gate = PolicyFunctions.ResearchGate(BuildingRuntimePackage.GeneratorResearch(facts), facts.Facts.Research);
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

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerShelterAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var wall = BuildingRuntimePackage.AnimalContainmentDefinition(facts.Definitions, "Wall");
        var door = BuildingRuntimePackage.AnimalContainmentDefinition(facts.Definitions, "Door");
        if (wall is null || door is null)
        {
            return ([], stock, FieldUnavailable("wall_door_definitions"));
        }

        var stuff = BuildingRuntimePackage.ShellSharedStuff(facts, wall, door);
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
            var a = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), b, PolicyFunctions.PlannerTier(Concern, Phase));
            var (p, _) = await Native.PreviewBuildingAsync(a, snapshot, ct).ConfigureAwait(false);
            var v = p.Preview;
            var fk = v.Footprint.TryGetValue(out var footprint);
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            var mk = v.MadeFromStuff.TryGetValue(out var made);
            if (!fk || !ck || !sk || !mk)
            {
                return ([], stock, FieldUnavailable("power_preview"));
            }

            if (footprint!.Count != 1 || footprint[0] != c || !can || !safe || made != (stuff != ""))
            {
                return ([], stock, NoSpace("power_shelter_cell"));
            }

            stock = MergeRoundsStock(stock, p.Stock, i == 0);
            selected.Add(v);
        }

        return (selected, stock, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerSiteAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Power is null || !Power.FixedSite())
        {
            throw new ControlException("previewPowerSite: no fixed power site");
        }

        check();
        var building = new Building(Definition, Power.Center, Rotation.North, "");
        var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, PolicyFunctions.PlannerTier(Concern, Phase));
        var (preview, _) = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
        var p = preview.Preview;
        var fk = p.Footprint.TryGetValue(out var footprint);
        var mk = p.MadeFromStuff.TryGetValue(out var made);
        var lk = p.CanPlace.TryGetValue(out var legal);
        var sk = p.SafeToPlace.TryGetValue(out var safe);
        if (!fk || !mk || !lk || !sk)
        {
            return ([], stock, FieldUnavailable("power_preview"));
        }

        var blocked = protectedCells.ToHashSet();
        foreach (var c in footprint!)
        {
            if (blocked.Contains(c))
            {
                return ([], stock, BuildingReasonExistingWork);
            }
        }

        if (made || footprint.Count == 0 || !legal || !safe)
        {
            return ([], stock, NoSpace("geyser_site"));
        }

        stock = MergeRoundsStock(stock, preview.Stock, true);
        return ([p], stock, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerOrSearchAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, long missing, Action check, CancellationToken call)
    {
        if (Power is not null && Power.Method is PowerMethod.Generate or PowerMethod.Store)
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

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, bool Ok)> PreviewPlannedPowerAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, long missing, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var planned = facts.LayoutPlan.TryGetValue(out var plan);
        var tk = facts.TechTier.TryGetValue(out var tier);
        if (!planned || !tk || tier < TechTier.Masonry || missing < 1)
        {
            return ([], stock, false);
        }

        var sites = PolicyFunctions.PlannedPowerSites(plan!, Definition);
        if (sites.Count == 0)
        {
            return ([], stock, false);
        }

        var blocked = protectedCells.ToHashSet();
        var built = new HashSet<Cell>();
        if (facts.Facts.CurrentConstruction.TryGetValue(out var census) && census.Colony)
        {
            foreach (var b in census.Buildings)
            {
                foreach (var c in b.Cells)
                {
                    built.Add(c);
                }
            }
        }

        var roofed = new HashSet<Cell>();
        foreach (var c in facts.Cells)
        {
            if (c.Roofed.TryGetValue(out var v) && v)
            {
                roofed.Add(c.Cell);
            }
        }

        foreach (var c in BuildingRuntimePackage.PorchCells(facts))
        {
            roofed.Add(c);
        }

        var wallStuff = "";
        var style = BuildingRuntimePackage.ShellStyleOf(facts).WallStuff;
        if (style != "")
        {
            wallStuff = style;
        }

        var n = 0;
        var merged = 0;
async 
        Task<(Preview Preview, bool Ok)> PreviewOneAsync(string name, Cell c, Rotation rot, string stuff, Rectangle area)
        {
            check();
            var b = new Building(name, c, rot, stuff);
            var a = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{n}"), b, PolicyFunctions.PlannerTier(Concern, Phase));
            n++;
            var (p, _) = await Native.PreviewBuildingAsync(a, snapshot, ct).ConfigureAwait(false);
            var v = p.Preview;
            var fk = v.Footprint.TryGetValue(out var footprint);
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            var mk = v.MadeFromStuff.TryGetValue(out var made);
            if (!fk || !ck || !sk || !mk || !can || !safe || made != (stuff != "") || !BuildingRuntimePackage.SameCells(footprint!, PolicyFunctions.RectangleCells(area)))
            {
                return (default!, false);
            }

            if (name == PolicyConstants.WindTurbineDefinition && (!v.WindBlockedCells.TryGetValue(out var w) || w > 0))
            {
                return (default!, false);
            }

            stock = MergeRoundsStock(stock, p.Stock, merged == 0);
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

            var cells = PolicyFunctions.RectangleCells(s.Area);
            var free = true;
            foreach (var c in cells)
            {
                free = free && !blocked.Contains(c) && !built.Contains(c) && (Definition != PolicyConstants.BatteryDefinition || roofed.Contains(c));
            }

            if (!free)
            {
                continue;
            }

            var (v, ok) = await PreviewOneAsync(Definition, s.Cell, s.Rotation, Stuff, s.Area).ConfigureAwait(false);
            if (!ok)
            {
                continue;
            }

            selected.Add(v);
            placed++;
            foreach (var c in PolicyFunctions.RectangleCells(s.Block))
            {
                if (built.Contains(c) || blocked.Contains(c))
                {
                    continue;
                }

                var (w, wok) = await PreviewOneAsync("Wall", c, Rotation.North, wallStuff, new Rectangle(c.X, c.Z, 1, 1)).ConfigureAwait(false);
                if (wok)
                {
                    selected.Add(w);
                }
            }
        }

        if (placed < missing)
        {
            return ([], stock, false);
        }

        return (selected, stock, true);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPowerRouteAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
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
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), building, PolicyFunctions.PlannerTier(Concern, Phase));
            var (preview, _) = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var p = preview.Preview;
            var fk = p.Footprint.TryGetValue(out var footprint);
            var mk = p.MadeFromStuff.TryGetValue(out var made);
            var lk = p.CanPlace.TryGetValue(out var legal);
            var sk = p.SafeToPlace.TryGetValue(out var safe);
            if (!fk || !mk || !lk || !sk)
            {
                return ([], stock, FieldUnavailable("power_preview"));
            }

            if (made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
            {
                return ([], stock, NoSpace("power_route_cell"));
            }

            selected.Add(p);
            stock = MergeRoundsStock(stock, preview.Stock, i == 0);
        }

        return (selected, stock, default);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static IReadOnlyList<string> PendingBuildingDefinitions(IReadOnlyList<Reservation> held)
    {
        var output = new List<string>();
        foreach (var h in held)
        {
            if (h.Progress.Action.Building is not { } b || !PendingWork(h.Progress) || PolicyFunctions.PowerFamilyDefinitions().Contains(b.Definition))
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

    internal static double PendingDemand(ColonyProjection facts, IReadOnlyList<string> pending)
    {
        var draw = new Dictionary<string, double>();
        foreach (var d in facts.Definitions)
        {
            if (d.PowerW.TryGetValue(out var w) && w > 0)
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

    internal static IReadOnlyList<string> GeneratorResearch(ColonyProjection facts)
    {
        var definitions = new Dictionary<string, PlanningDefinition>();
        foreach (var d in facts.Definitions)
        {
            definitions[d.Name] = d;
        }

        var seen = new HashSet<string>();
        var required = new List<string>();
        foreach (var name in PolicyConstants.GeneratorDefinitions)
        {
            if (!definitions.TryGetValue(name, out var d))
            {
                continue;
            }

            if (!d.Available.TryGetValue(out var available) || available)
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

    internal static uint PowerOutputAllowance(StoreJournal journal, StandardState goal, GenerationSnapshot current, Tick tick, CancellationToken ct)
    {
        var methods = journal.LoadOwnerMethods(goal, ct);
        uint allowance = 0;
        foreach (var m in methods)
        {
            var plan = journal.LoadPlan(m.Plan, ct);
            allowance = Math.Max(allowance, PowerNativeWorkTicks(plan, current, tick));
            var actions = plan.Spec.Actions;
            if (actions.Count > 0)
            {
                var walls = true;
                foreach (var a in actions)
                {
                    walls = walls && a.Building is { } b && (b.Definition == "Wall" || b.Definition == "Door");
                }

                if (walls)
                {
                    allowance = Math.Max(allowance, ShelterNativeWorkTicks(plan, current, tick));
                }
            }
        }

        return allowance;
    }

    internal static bool PowerDefinition(string name) =>
        name is "PowerConduit" or "HiddenConduit" or "WaterproofConduit" || PolicyFunctions.PowerFamilyDefinitions().Contains(name);

    internal static uint PowerNativeWorkTicks(PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count is < 1 or > 8)
        {
            return 0;
        }

        current = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
        long completed = 0;
        foreach (var p in plan.Progress)
        {
            if (p.Action.Building is not { } b || !PowerDefinition(b.Definition))
            {
                return 0;
            }

            var v = p.View;
            if (v.Stage != Stage.Completed || v.Unresolved || !v.Effect.TryGetValue(out var effect) || effect != Effect.Completed || !v.Snapshot.Matches(current) || tick.Value < v.Tick.Value)
            {
                return 0;
            }

            completed = Math.Max(completed, v.Tick.Value);
        }

        if (tick.Value - completed >= 10000)
        {
            return 0;
        }

        return (uint)Math.Min(120L, 10000L - (tick.Value - completed));
    }

    internal static IReadOnlySet<Cell> PorchCells(ColonyProjection facts)
    {
        var output = new HashSet<Cell>();
        if (facts.LayoutPlan.TryGetValue(out var plan))
        {
            foreach (var c in PolicyFunctions.PorchRoofCells(plan))
            {
                output.Add(c);
            }
        }

        return output;
    }

    internal static (DomainAction Action, bool Ok) PorchRoofAction(RoundsAdmission a, int ordinal)
    {
        if (!a.Facts.LayoutPlan.TryGetValue(out var plan))
        {
            return (default!, false);
        }

        var porch = PorchCells(a.Facts);
        var onPorch = false;
        foreach (var v in a.Selected)
        {
            if (v.Action.Building is { } b && b.Definition == PolicyConstants.BatteryDefinition && porch.Contains(b.Cell))
            {
                onPorch = true;
            }
        }

        if (!onPorch)
        {
            return (default!, false);
        }

        var roof = new BuildRoof(PolicyFunctions.PorchRoofCells(plan));
        var action = DomainAction.NewBuildRoofAction(new ActionId($"{a.Snapshot.Plan}-{ordinal}"), roof);
        return (action, true);
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

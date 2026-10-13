namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    public static RoundsBuildingPlanner NewRoundsLightingPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(PolicyFunctions.MaintainLighting))
        {
            throw new ControlException("NewRoundsLightingPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsLightingPlanner: native is not a rounds source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = PolicyFunctions.MaintainLighting, Definition = "TorchLamp" };
    }

    internal static Fact<EquatableArray<int>> PlanLitRooms(ColonyProjection facts) =>
        PolicyFunctions.PlanLitRooms(facts.LayoutPlan, facts.Rooms, facts.Facts.Upkeep.Lighting, PoweredSource(facts));

    internal IReadOnlyList<string> LightingDefinitions()
    {
        var output = Reviewer.Policy.Lighting.Lamps.Select(lamp => lamp.Name).ToList();
        var defs = PolicyFunctions.LightDefs();
        foreach (var name in new[] { defs.LampDef, defs.WallLampDef })
        {
            if (!output.Contains(name))
            {
                output.Add(name);
            }
        }

        return output;
    }

    /// <summary>The planner resolved to the lighting method the policy picked, or the verdict that says why none applies.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectLighting(ColonyProjection facts, RoundsLatches latches)
    {
        var p = Reviewer.Policy.Lighting;
        var review = PolicyFunctions.ReviewLighting(facts.Facts.Upkeep.Lighting, latches.Lighting, p, PolicyFunctions.SkyDarkHold(facts.Facts.DisasterConditions, facts.Facts.OutdoorsDark));
        review = review.WithPlanned(PlanLitRooms(facts));
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        if (!review.Known)
        {
            return (null, FieldUnavailable("lighting"));
        }

        var lighting = new LightingFacts
        {
            Cells = facts.Cells,
            Available = facts.Definitions.ToDictionary(d => d.Name, d => d.Available),
            PoweredSource = PoweredSource(facts),
            Styled = LampStyle(facts),
        };
        if (facts.Rooms.TryGetValue(out var rooms))
        {
            lighting = lighting with { Rooms = rooms.Rooms };
        }

        if (facts.LayoutPlan.TryGetValue(out var plan))
        {
            lighting = lighting with { Plan = plan };
        }

        var proposal = PolicyFunctions.SelectLightingMethod(review, facts.Facts.Upkeep.Lighting, lighting, p);
        switch (proposal.Method)
        {
            case LightingMethod.Build:
                var resolved = Copy();
                resolved.Lighting = proposal;
                resolved.Definition = proposal.Definition;
                return (resolved, default);
            case LightingMethod.Unknown:
                return (null, FieldUnavailable("lighting"));
            case LightingMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            default:
                return (null, AwaitingMethod(proposal.Method));
        }
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewLightingAsync(GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Lighting is null || Lighting.Method != LightingMethod.Build)
        {
            throw new ControlException("previewLighting: no lighting build proposal");
        }

        var guarded = protectedCells.ToHashSet();
        foreach (var lamp in Lighting.Planned)
        {
            var rotation = lamp.Facing;
            var (previews, planned, reason) = await PreviewWallBuildingAsync(snapshot, facts, protectedCells, check, lamp.Def, lamp.Cell, rotation, false, ct).ConfigureAwait(false);
            if (reason.IsZero)
            {
                return (previews, planned, default);
            }
        }

        var unknown = false;
        foreach (var cell in Lighting.Cells)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }

            var building = new Building(Lighting.Definition, cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, PolicyFunctions.PlannerTier(Concern, Phase));
            var (preview, _) = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var p = preview.Preview;
            var fk = p.Footprint.TryGetValue(out var footprint);
            var mk = p.MadeFromStuff.TryGetValue(out var made);
            var lk = p.CanPlace.TryGetValue(out var legal);
            var sk = p.SafeToPlace.TryGetValue(out var safe);
            if (!fk || !mk || !lk || !sk)
            {
                unknown = true;
                continue;
            }

            if (made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
            {
                continue;
            }

            stock = MergeRoundsStock(stock, preview.Stock, true);
            return ([p], stock, default);
        }

        if (unknown)
        {
            return (null!, stock, FieldUnavailable("lighting_preview"));
        }

        return (null!, stock, NoSpace("light_cell"));
    }
}

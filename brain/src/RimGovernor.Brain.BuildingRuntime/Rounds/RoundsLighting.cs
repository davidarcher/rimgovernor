using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    public static RoundsBuildingPlanner NewRoundsLightingPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsLightingPlanner: reviewer or native is missing");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsLightingPlanner: native is not a rounds source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = RoomsFamily.Lighting.FilingKey, Need = RoomsFamily.Lighting, Definition = "TorchLamp" };
    }

    internal static Fact<EquatableArray<int>> PlanLitRooms(ColonyProjection facts) =>
        LightingRules.PlanLitRooms(facts.LayoutPlan, facts.Rooms, facts.Facts.Upkeep.Lighting, RoundsTierStyle.PoweredSource(facts));

    internal IReadOnlyList<string> LightingDefinitions()
    {
        var output = Reviewer.Policy.Lighting.Lamps.Select(lamp => lamp.Name).ToList();
        var defs = LayoutLightRules.LightDefs();
        foreach (var name in new[] { defs.LampDef, defs.WallLampDef })
        {
            if (!output.Contains(name))
            {
                output.Add(name);
            }
        }

        return output;
    }

    /// <summary>The lighting review of <paramref name="facts"/> with the plan's unlit rooms folded in, measured fresh: the rooms/lighting Need's evidence.</summary>
    internal static LightingReview LightingReviewOf(ColonyProjection facts, LightingPolicy p) =>
        LightingRules.ReviewLighting(facts.Facts.Upkeep.Lighting, p, DisasterRules.SkyDarkHold(facts.Facts.DisasterConditions, facts.Facts.OutdoorsDark))
            .WithPlanned(PlanLitRooms(facts));

    /// <summary>The planner resolved to the lighting method the policy picked, or the verdict that says why none applies.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectLighting(ColonyProjection facts)
    {
        var p = Reviewer.Policy.Lighting;
        var review = LightingReviewOf(facts, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        if (!review.Known)
        {
            return (null, FieldUnavailable("lighting"));
        }

        if (!facts.Rooms.TryGetValue(out var rooms))
        {
            return (null, FieldUnavailable("rooms"));
        }

        var lighting = new LightingFacts
        {
            Rooms = rooms,
            Cells = facts.Cells,
            Available = facts.Definitions.ToDictionary(d => d.Name, d => d.Available),
            PoweredSource = RoundsTierStyle.PoweredSource(facts),
            Styled = RoundsTierStyle.LampStyle(facts),
        };

        if (facts.LayoutPlan.TryGetValue(out var plan))
        {
            lighting = lighting with { Plan = plan };
        }

        var proposal = LightingRules.SelectLightingMethod(review, facts.Facts.Upkeep.Lighting, lighting, p);
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

        foreach (var cell in Lighting.Cells)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }

            var building = new Building(Lighting.Definition, cell, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
            var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var p = preview.Preview;
            if (p.MadeFromStuff || p.Footprint.Count != 1 || p.Footprint[0] != cell || !p.CanPlace || !p.SafeToPlace)
            {
                continue;
            }

            stock = MergeRoundsStock(stock, preview.Stock);
            return ([p], stock, default);
        }

        return (null!, stock, NoSpace("light_cell"));
    }
}

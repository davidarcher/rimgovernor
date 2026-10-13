namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Cells a field must not cover: held reservations, pending monuments, housing shells and the firebreak ring.</summary>
    internal IReadOnlyList<Cell> FieldProtected(ControlState state, Rounds review, ColonyProjection projection, Fact<EquatableArray<ConstructionClaim>> claims, CancellationToken call)
    {
        var p = Player;
        var held = p.Journal.BuildingReservations(state.Snapshot, call);
        var protectedCells = new List<Cell>();
        var plans = p.Journal.LoadPlans(call);
        protectedCells.AddRange(BuildingRuntimePackage.PendingMonumentCells(plans, projection.Facts.QuestOffers));
        foreach (var h in held)
        {
            protectedCells.AddRange(h.Footprint);
        }

        var shells = new List<PlanState>();
        foreach (var binding in review.Standards)
        {
            if (binding.Concern != PolicyFunctions.MaintainHousing)
            {
                continue;
            }

            var shelter = p.Journal.LoadStandard(binding.Standard, call);
            foreach (var method in shelter.Methods)
            {
                shells.Add(p.Journal.LoadPlan(method.Plan, call));
            }
        }

        claims.TryGetValue(out var claimed);
        protectedCells.AddRange(BuildingRuntimePackage.ShellInteriors(shells, claimed));
        protectedCells.AddRange(BuildingRuntimePackage.FirebreakRing(projection));
        return protectedCells;
    }

    internal async Task<IReadOnlyList<string>> SowableCropsAsync(GenerationSnapshot snapshot, CancellationToken call)
    {
        if (Native is not IDefinitionSource source)
        {
            return [];
        }

        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), call).ConfigureAwait(false);
        return catalog is null ? [] : catalog.SowableCrops();
    }

    internal ResourceFieldPlanner NewResourceFieldPlanner(ControlState state, Rounds review, Identity expected, ColonyProjection projection, CancellationToken call)
    {
        var (_, choices) = BuildingRuntimePackage.FieldRequestOf(projection, 0);
        return new ResourceFieldPlanner(
            choices,
            projection.CropClimate,
            PolicyFunctions.GrowerSkill(projection.WorkPawns),
            projection.Farms,
            () =>
            {
                if (LayoutProtected.FieldAnchor(projection) is not { } anchor)
                {
                    return (new FarmSiteRequest(), false);
                }

                var claims = Player.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
                var protectedCells = FieldProtected(state, review, projection, claims, call);
                var site = new FarmSiteRequest { Bounds = projection.Bounds, Anchor = anchor, Cells = projection.Cells, Protected = protectedCells };
                var fields = LayoutFieldPlanning.LayoutFieldCells(projection);
                if (fields is not null)
                {
                    site = site with { Fields = fields };
                }

                return (site, true);
            });
    }

    internal double ChopMinGrowth(ColonyProjection projection)
    {
        var (fraction, ok) = PolicyFunctions.TreeFellFraction(BuildingRuntimePackage.CropChoices(projection), new Resource("WoodLog"), projection.CropClimate.Biome, PolicyFunctions.GrowerSkill(projection.WorkPawns));
        return ok ? fraction : Policy.ChopMinGrowth;
    }
}

/// <summary>Plans crop fields toward a resource deficit, reading the farm site lazily and once.</summary>
internal sealed class ResourceFieldPlanner
{
    private readonly Func<(FarmSiteRequest Site, bool Ok)> siteRead;
    private FarmSiteRequest site = new();
    private bool read;
    private bool sited;

    public ResourceFieldPlanner(IReadOnlyList<CropChoice> choices, CropClimate climate, Fact<int> skill, IReadOnlyList<FarmZoneFact> farms, Func<(FarmSiteRequest Site, bool Ok)> siteRead)
    {
        Choices = choices;
        Climate = climate;
        Skill = skill;
        Farms = farms;
        this.siteRead = siteRead;
    }

    public IReadOnlyList<CropChoice> Choices { get; }

    public CropClimate Climate { get; }

    public Fact<int> Skill { get; }

    public IReadOnlyList<FarmZoneFact> Farms { get; }

    public bool Serves(Resource resource)
    {
        foreach (var crop in Choices)
        {
            if (crop.Harvests.TryGetValue(out var harvests) && harvests == resource)
            {
                return true;
            }
        }

        return false;
    }

    public (FarmSiteRequest Site, bool Sited) SiteOf()
    {
        if (!read)
        {
            var (fetched, ok) = siteRead();
            site = fetched;
            sited = ok;
            read = true;
        }

        return (site, sited);
    }

    public long Standing(Resource resource)
    {
        var byName = Choices.ToDictionary(c => c.Name);
        var units = 0.0;
        foreach (var farm in Farms)
        {
            if (byName.TryGetValue(farm.Crop, out var crop))
            {
                units += PolicyFunctions.StandingFieldYield(crop, resource, farm.UsableCells);
            }
        }

        return (long)Math.Round(units, MidpointRounding.AwayFromZero);
    }

    public (SupplyCandidate Candidate, FieldPlan Plan, bool Ok) Candidate(Resource resource, long need)
    {
        var (siteRequest, isSited) = SiteOf();
        if (!isSited)
        {
            return (new SupplyCandidate(), new FieldPlan(), false);
        }

        var plan = PolicyFunctions.PlanFieldByResource(new ResourceFieldRequest
        {
            Resource = resource,
            Deficit = Known((double)need),
            Choices = Choices,
            Climate = Climate,
            Site = siteRequest,
            GrowerSkill = Skill,
        });
        if (plan is null)
        {
            return (new SupplyCandidate(), new FieldPlan(), false);
        }

        var candidate = PolicyFunctions.ResourceFieldCandidate(resource, plan);
        return candidate is null ? (new SupplyCandidate(), plan, false) : (candidate, plan, true);
    }
}

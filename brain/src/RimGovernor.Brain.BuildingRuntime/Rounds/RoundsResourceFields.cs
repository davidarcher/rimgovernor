namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Cells a field must not cover: held reservations, pending monuments, housing shells and the firebreak ring.</summary>
    internal IReadOnlyList<Cell> FieldProtected(CancellationToken call, ControlState state, Rounds review, ColonyProjection projection, Fact<IReadOnlyList<ConstructionClaim>> claims)
    {
        var p = Player;
        var held = p.Journal.BuildingReservations(call, state.Snapshot);
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
            if (binding.Concern != ConcernId.MaintainHousing)
            {
                continue;
            }

            var shelter = p.Journal.LoadStandard(call, binding.Standard);
            foreach (var method in shelter.Methods)
            {
                shells.Add(p.Journal.LoadPlan(call, method.Plan));
            }
        }

        claims.TryGetValue(out var claimed);
        protectedCells.AddRange(BuildingRuntimePackage.ShellInteriors(shells, claimed ?? []));
        protectedCells.AddRange(BuildingRuntimePackage.FirebreakRing(projection));
        return protectedCells;
    }

    internal IReadOnlyList<string> SowableCrops(CancellationToken call, GenerationSnapshot snapshot)
    {
        if (Native is not IDefinitionSource source)
        {
            return [];
        }

        return source.DefinitionCatalog(call, Boundary.Identity(snapshot)).SowableCrops();
    }

    internal ResourceFieldPlanner NewResourceFieldPlanner(CancellationToken call, ControlState state, Rounds review, Identity expected, ColonyProjection projection)
    {
        var (_, choices) = BuildingRuntimePackage.FieldRequest(projection, 0);
        return new ResourceFieldPlanner(
            choices,
            projection.CropClimate,
            PolicyFunctions.GrowerSkill(projection.WorkPawns),
            projection.Farms,
            () =>
            {
                var (anchor, planned) = BuildingRuntimePackage.FieldAnchor(projection);
                if (!planned)
                {
                    return (default, false);
                }

                var claims = Player.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
                var protectedCells = FieldProtected(call, state, review, projection, Known(claims));
                var site = new FarmSiteRequest { Bounds = projection.Bounds, Anchor = anchor, Cells = projection.Cells, Protected = protectedCells };
                var (fields, ok) = BuildingRuntimePackage.LayoutFieldCells(projection);
                if (ok)
                {
                    site = site with { Fields = fields };
                }

                return (site, true);
            });
    }

    internal double ChopMinGrowth(ColonyProjection projection)
    {
        var (fraction, ok) = PolicyFunctions.TreeFellFraction(BuildingRuntimePackage.CropChoices(projection), "WoodLog", projection.CropClimate.Biome, PolicyFunctions.GrowerSkill(projection.WorkPawns));
        return ok ? fraction : Policy.ChopMinGrowth;
    }
}

/// <summary>Plans crop fields toward a resource deficit, reading the farm site lazily and once.</summary>
internal sealed class ResourceFieldPlanner
{
    private readonly Func<(FarmSiteRequest Site, bool Ok)> siteRead;
    private FarmSiteRequest? site;
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
        if (site is null)
        {
            var (read, ok) = siteRead();
            site = read;
            sited = ok;
        }

        return (site.Value, sited);
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
            return (default, default, false);
        }

        var (plan, ok) = PolicyFunctions.PlanFieldByResource(new ResourceFieldRequest
        {
            Resource = resource,
            Deficit = Known((double)need),
            Choices = Choices,
            Climate = Climate,
            Site = siteRequest,
            GrowerSkill = Skill,
        });
        if (!ok)
        {
            return (default, default, false);
        }

        var (candidate, candidateOk) = PolicyFunctions.ResourceFieldCandidate(resource, plan);
        return (candidate, plan, candidateOk);
    }
}

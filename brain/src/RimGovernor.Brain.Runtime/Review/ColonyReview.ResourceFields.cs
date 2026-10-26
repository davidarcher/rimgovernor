using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Cells a field must not cover: held reservations, pending monuments, housing shells and the firebreak ring.</summary>
    internal IReadOnlyList<Cell> FieldProtected(ControlState state, ReviewState review, ColonyReading projection, EquatableArray<ConstructionClaim> claims, CancellationToken call)
    {
        var p = Player;
        var held = p.Journal.BuildingReservations(state.Snapshot, call);
        var protectedCells = new List<Cell>();
        var plans = p.Journal.LoadPlans(call);
        protectedCells.AddRange(QuestMonumentClaims.PendingMonumentCells(plans, projection.Facts.QuestOffers));
        foreach (var h in held)
        {
            protectedCells.AddRange(h.Footprint);
        }

        var shells = new List<PlanState>();
        if (Needs.Contains(ShelterFamily.Sleeping))
        {
            foreach (var method in p.Journal.LoadNeed(ShelterFamily.Sleeping, review.Snapshot, call).Methods)
            {
                shells.Add(p.Journal.LoadPlan(method.Plan, call));
            }
        }

        protectedCells.AddRange(ShellInteriors.Cells(shells, claims));
        protectedCells.AddRange(FieldPlanner.FirebreakRing(projection));
        return protectedCells;
    }

    internal async Task<IReadOnlyList<string>> SowableCropsAsync(GenerationSnapshot snapshot, CancellationToken call)
    {
        if (Native is not IDefinitionSource source)
        {
            return [];
        }

        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), call).ConfigureAwait(false);
        return catalog.SowableCrops();
    }

    internal ResourceFieldPlanner NewResourceFieldPlanner(ControlState state, ReviewState review, Identity expected, ColonyReading projection, CancellationToken call)
    {
        var choices = FieldPlanner.CropChoices(projection);
        return new ResourceFieldPlanner(
            choices,
            projection.Facts.CropClimate,
            TreePlantationRules.GrowerSkill(projection.Facts.ForecastObserved.WorkPawns),
            (projection.Tables.Zones ?? throw new RequiredFactException("zones")).Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>().ToArray(),
            () =>
            {
                if (LayoutProtected.FieldAnchor(projection) is not { } anchor)
                {
                    return (new FarmSiteRequest(), false);
                }

                var claims = Player.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
                var protectedCells = FieldProtected(state, review, projection, claims, call);
                var site = new FarmSiteRequest { Anchor = anchor, Cells = projection.PlanningWindow.Cells, Protected = protectedCells };
                var fields = LayoutFieldPlanning.LayoutFieldCells(projection.Facts);
                if (fields is not null)
                {
                    site = site with { Fields = fields };
                }

                return (site, true);
            });
    }

    internal double ChopMinGrowth(ColonyReading projection)
    {
        var (fraction, ok) = TreePlantationRules.TreeFellFraction(FieldPlanner.CropChoices(projection), new Resource("WoodLog"), projection.Facts.CropClimate.Biome, TreePlantationRules.GrowerSkill(projection.Facts.ForecastObserved.WorkPawns));
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

    public ResourceFieldPlanner(IReadOnlyList<CropChoice> choices, CropClimate climate, int skill, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing> farms, Func<(FarmSiteRequest Site, bool Ok)> siteRead)
    {
        Choices = choices;
        Climate = climate;
        Skill = skill;
        Farms = farms;
        this.siteRead = siteRead;
    }

    public IReadOnlyList<CropChoice> Choices { get; }

    public CropClimate Climate { get; }

    public int Skill { get; }

    public IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing> Farms { get; }

    public bool Serves(Resource resource)
    {
        foreach (var crop in Choices)
        {
            var harvests = crop.Harvests;
            if (harvests == resource)
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
                units += FieldResourceRules.StandingFieldYield(crop, resource, farm.UsableCells);
            }
        }

        return (long)Math.Round(units, MidpointRounding.AwayFromZero);
    }

    /// <summary>The field candidate that grows the resource, with its plan; a null candidate when the site, plan or candidate is missing.</summary>
    public (SupplyCandidate? Candidate, FieldPlan Plan) Candidate(Resource resource, long need)
    {
        var (siteRequest, isSited) = SiteOf();
        if (!isSited)
        {
            return (null, new FieldPlan());
        }

        var plan = FieldResourceRules.PlanFieldByResource(new ResourceFieldRequest
        {
            Resource = resource,
            Deficit = (double)need,
            Choices = Choices,
            Climate = Climate,
            Site = siteRequest,
            GrowerSkill = Skill,
        });
        if (plan is null)
        {
            return (null, new FieldPlan());
        }

        var candidate = FieldResourceRules.ResourceFieldCandidate(resource, plan);
        return (candidate, plan);
    }
}

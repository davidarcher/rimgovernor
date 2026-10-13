namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names the autonomous action that owns a building. The journal supplies Plan, Action, Concern and Building
/// for every applied building intent; OwnedConstructions fills Identity (the building's current id) and Cells
/// from the census row standing where the intent built. A census row no intent built carries Identity,
/// Building and Cells only.
/// </summary>
public sealed record ConstructionClaim
{
    public PlanId Plan { get; init; }

    public ActionId Action { get; init; }

    public ConcernId Concern { get; init; }

    public ConstructionIdentity Identity { get; init; }

    public Building? Building { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

public sealed record CurrentBuilding
{
    public string Id { get; init; } = "";

    public Building? Building { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

/// <summary>The colony's built, player-owned buildings. Colony marks a complete census; Requested scopes an exact-ID read.</summary>
public sealed record CurrentConstruction
{
    public bool Colony { get; init; }

    public EquatableArray<string> Requested { get; init; }

    public EquatableArray<CurrentBuilding> Buildings { get; init; }

    /// <summary>The player's blueprints and frames still standing.</summary>
    public EquatableArray<ConstructionSite> Sites { get; init; }

    /// <summary>The id of the built row standing as <paramref name="building"/>, or null.</summary>
    public string? Built(Building building)
    {
        foreach (var row in Buildings)
        {
            if (row.Building == building)
            {
                return row.Id;
            }
        }
        return null;
    }
}

/// <summary>One blueprint or frame on the map: the building it will become and its stage.</summary>
public sealed record ConstructionSite
{
    public Building? Building { get; init; }

    public string Stage { get; init; } = "";

    public string Id { get; init; } = "";

    public Fact<bool> ResourcesComplete { get; init; }

    public Fact<bool> QualitySensitive { get; init; }

    public Fact<int> MinimumFinishingSkill { get; init; }

    public Fact<int> NativeFinishingSkill { get; init; }

    /// <summary>The native per-target tier; unknown means ungated.</summary>
    public Fact<ConstructionTier> Tier { get; init; }

    public Fact<int> EligibleFinishers { get; init; }

    public string FinishingBlocker { get; init; } = "";
}

/// <summary>Where an applied building intent stands.</summary>
public enum BuildingWork
{
    /// <summary>Its blueprint or frame stands, or the census is unknown.</summary>
    BuildingOpen = 0,

    /// <summary>A built row stands where it built.</summary>
    BuildingDone,

    /// <summary>Neither exists; the work closed without a building.</summary>
    BuildingGone,
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Classifies an applied building intent against the census by geometry: whatever stands with its
    /// definition, stuff, anchor and rotation is its work, whoever placed it. A match placed by someone else
    /// is the same end state, so no lineage is kept.
    /// </summary>
    public static BuildingWork WorkOpen(Building building, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return BuildingWork.BuildingOpen;
        }
        if (census.Built(building) is not null)
        {
            return BuildingWork.BuildingDone;
        }
        foreach (var site in census.Sites)
        {
            if (site.Building == building)
            {
                return BuildingWork.BuildingOpen;
            }
        }
        return BuildingWork.BuildingGone;
    }

    /// <summary>
    /// The common current geometry/ownership view for facility planning and colony extent: every census
    /// building, annotated with the journal claim whose building stands there. History never excludes
    /// player-built facilities. Throws <see cref="DomainException"/> on a malformed census.
    /// </summary>
    public static Fact<EquatableArray<ConstructionClaim>> OwnedConstructions(Fact<EquatableArray<ConstructionClaim>> claims, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return Unknown<EquatableArray<ConstructionClaim>>();
        }
        DomainException.Require(census.Requested.Count == 0, "invalid colony construction census");
        var byBuilding = new Dictionary<Building, ConstructionClaim>();
        if (claims.TryGetValue(out var history))
        {
            foreach (var claim in history)
            {
                if (FoodId(claim.Plan.Value) && FoodId(claim.Action.Value) && FoodId(claim.Concern.Value) && claim.Building is not null)
                {
                    byBuilding[claim.Building] = claim;
                }
            }
        }
        var result = new List<ConstructionClaim>(census.Buildings.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in census.Buildings)
        {
            DomainException.Require(FoodId(row.Id) && seen.Add(row.Id), "invalid colony building identity");
            var b = row.Building ?? throw new DomainException("invalid colony building identity");
            DomainException.Require(FacilityCells(row.Cells), "invalid colony building footprint");
            DomainException.Require(row.Cells.Contains(b.Cell), "building anchor outside footprint");
            var owned = byBuilding.TryGetValue(b, out var claim) ? claim : new ConstructionClaim { Building = b };
            result.Add(owned with { Identity = new ConstructionIdentity("", row.Id), Cells = row.Cells });
        }
        return Known(EquatableArray.From(result.OrderBy(c => c.Identity.Current, StringComparer.Ordinal)));
    }
}

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

/// <summary>The colony's construction census: its built, player-owned buildings, its standing sites and what they are owed.</summary>
public sealed record CurrentConstruction
{
    public required EquatableArray<CurrentBuilding> Buildings { get; init; }

    /// <summary>The player's blueprints and frames still standing.</summary>
    public required EquatableArray<ConstructionSite> Sites { get; init; }

    /// <summary>The material standing blueprints and frames are still owed, per resource.</summary>
    public required IReadOnlyDictionary<Resource, long> Deficit { get; init; }

    /// <summary>A census with nothing built, standing or owed.</summary>
    public static CurrentConstruction Empty { get; } = new() { Buildings = [], Sites = [], Deficit = new Dictionary<Resource, long>() };

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
    /// <summary>The <see cref="Stage"/> of a blueprint: nothing delivered yet.</summary>
    public const string BlueprintStage = "blueprint";

    public Building? Building { get; init; }

    public string Stage { get; init; } = "";

    public string Id { get; init; } = "";

    /// <summary>Every material the build needs is delivered.</summary>
    public bool ResourcesComplete { get; init; }

    /// <summary>The built thing has quality, so the finisher's skill sets it.</summary>
    public bool QualitySensitive { get; init; }

    /// <summary>The def's own constructionSkillPrerequisite.</summary>
    public int NativeFinishingSkill { get; init; }

    /// <summary>The planner-set minimum finishing skill and how many free colonists meet it; null when none is set.</summary>
    public FinishingMinimum? Minimum { get; init; }

    /// <summary>The native per-target tier: ungated, or gated at a tier.</summary>
    public ConstructionGate Gate { get; init; } = new ConstructionGate.Ungated();

    public string FinishingBlocker { get; init; } = "";
}

/// <summary>Where an applied building intent stands.</summary>
public enum BuildingWork
{
    /// <summary>Its blueprint or frame stands.</summary>
    BuildingOpen = 0,

    /// <summary>A built row stands where it built.</summary>
    BuildingDone,

    /// <summary>Neither exists; the work closed without a building.</summary>
    BuildingGone,
}

public static class ConstructionOwnershipRules
{
    /// <summary>
    /// Classifies an applied building intent against the census by geometry: whatever stands with its
    /// definition, stuff, anchor and rotation is its work, whoever placed it. A match placed by someone else
    /// is the same end state, so no lineage is kept.
    /// </summary>
    public static BuildingWork WorkOpen(Building building, CurrentConstruction? census)
    {
        if (census is null)
        {
            throw new RequiredFactException("construction census");
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
    public static EquatableArray<ConstructionClaim> OwnedConstructions(EquatableArray<ConstructionClaim> history, CurrentConstruction? census)
    {
        if (census is null)
        {
            throw new RequiredFactException("current_construction");
        }
        var byBuilding = new Dictionary<Building, ConstructionClaim>();
        foreach (var claim in history)
        {
            if (FoodForecastRules.FoodId(claim.Plan.Value) && FoodForecastRules.FoodId(claim.Action.Value) && FoodForecastRules.FoodId(claim.Concern.Value) && claim.Building is not null)
            {
                byBuilding[claim.Building] = claim;
            }
        }
        var result = new List<ConstructionClaim>(census.Buildings.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in census.Buildings)
        {
            DomainException.Require(FoodForecastRules.FoodId(row.Id) && seen.Add(row.Id), "invalid colony building identity");
            var b = row.Building ?? throw new DomainException("invalid colony building identity");
            DomainException.Require(FacilityUpkeepRules.FacilityCells(row.Cells), "invalid colony building footprint");
            DomainException.Require(row.Cells.Contains(b.Cell), "building anchor outside footprint");
            var owned = byBuilding.TryGetValue(b, out var claim) ? claim : new ConstructionClaim { Building = b };
            result.Add(owned with { Identity = new ConstructionIdentity("", row.Id), Cells = row.Cells });
        }
        return EquatableArray.From(result.OrderBy(c => c.Identity.Current, StringComparer.Ordinal));
    }
}

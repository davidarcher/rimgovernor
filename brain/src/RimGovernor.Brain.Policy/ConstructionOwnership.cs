
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

    public FilingKey Concern { get; init; }

    public ConstructionIdentity Identity { get; init; }

    public Building? Building { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

public sealed record CurrentBuilding
{
    public required RimGovernor.Brain.GameFacts.Observations.BuildingState Observed { get; init; }
    public string Id => Observed.Building.Id;
    public Building Building => ConstructionOwnershipRules.BuildingOf(Observed, Observed.Building.DefName);
    public EquatableArray<Cell> Cells => EquatableArray.From(CellsRules.RectCells(Observed.Occupied));
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

    public required RimGovernor.Brain.GameFacts.Observations.BuildingState Observed { get; init; }
    public RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build Construction => Observed.Construction as RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build
        ?? throw new DomainException("construction site carries no build state");
    public Building Building => ConstructionOwnershipRules.BuildingOf(Observed, Construction.Definition);
    public string Stage => Observed.Status switch
    {
        RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint => BlueprintStage,
        RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Frame => "frame",
        RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built => throw new DomainException("construction site is already built"),
        var invalid => throw new DomainException($"undefined construction status: {invalid}"),
    };
    public string Id => Observed.Building.Id;

    /// <summary>Every material the build needs is delivered.</summary>
    public bool ResourcesComplete => Construction.Resources.All(r => r.StillNeeded == 0);

    /// <summary>The built thing has quality, so the finisher's skill sets it.</summary>
    public bool QualitySensitive => Construction.QualitySensitive;

    /// <summary>The def's own constructionSkillPrerequisite.</summary>
    public int NativeFinishingSkill => Construction.NativeFinishingSkill;

    /// <summary>The planner-set minimum finishing skill and how many free colonists meet it; null when none is set.</summary>
    public FinishingMinimum? Minimum => Construction.Minimum;

    /// <summary>The native per-target tier: ungated, or gated at a tier.</summary>
    public ConstructionGate Gate => Construction.Gate;

    public string FinishingBlocker => Minimum is { } minimum
        ? (minimum.EligibleFinishers == 0 ? "no_qualified_builder" : "")
        : (QualitySensitive ? "minimum_not_set" : "");
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
    /// <summary>The value geometry an observed row is compared with proposed building intents.</summary>
    public static Building BuildingOf(RimGovernor.Brain.GameFacts.Observations.BuildingState row, string definition) => new(definition,
        row.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap at ? at.Cell : throw new DomainException("required construction row is unplaced"),
        row.Rotation switch
        {
            RimGovernor.Brain.GameFacts.Placement.Rotation.North => Rotation.North,
            RimGovernor.Brain.GameFacts.Placement.Rotation.East => Rotation.East,
            RimGovernor.Brain.GameFacts.Placement.Rotation.South => Rotation.South,
            RimGovernor.Brain.GameFacts.Placement.Rotation.West => Rotation.West,
            var invalid => throw new DomainException($"undefined building rotation {(int)invalid}"),
        }, row.Stuff ?? "");

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

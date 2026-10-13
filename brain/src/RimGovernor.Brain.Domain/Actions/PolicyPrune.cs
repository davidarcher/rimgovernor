namespace RimGovernor.Brain.Domain;

/// <summary>
/// The native database a prune deletes from (Go <c>PolicyDatabase</c>). Members are the Go constants
/// <c>OutfitPolicies</c>, <c>DrugPolicies</c>, <c>FoodPolicies</c>, <c>ReadingPolicies</c>, <c>AllowedAreas</c>
/// without the plural, so the wire names stay the Go strings.
/// </summary>
public enum PolicyDatabase
{
    Outfit,
    Drug,
    Food,
    Reading,
    AllowedArea,
}

/// <summary>
/// Deletes outfit, drug, food or reading policies, or allowed areas, that no bot planner assigned: a
/// PolicyPruneIntent on Actions/Apply. Native first moves every player pawn still holding one onto its own
/// per-pawn policy (or unrestricted), then deletes it; an id already gone applies again. An immutable value: the
/// database and the canonical (sorted, deduplicated) load ids to delete, at least one.
/// </summary>
public sealed record PolicyPrune : IActionPayload
{
    /// <summary>Go <c>NewPolicyPrune</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public PolicyPrune(PolicyDatabase database, IEnumerable<string> ids)
    {
        DomainException.Require(Wire.IsDefined(database), "invalid policy database");
        var rows = ids.OrderBy(i => i, StringComparer.Ordinal).ToArray();
        DomainException.Require(rows.Length != 0, "policy prune needs ids");
        for (var i = 0; i < rows.Length; i++)
        {
            DomainException.Require(Identity.IsValidText(rows[i]) && !(i > 0 && rows[i - 1] == rows[i]), "invalid policy id");
        }
        Database = database;
        Ids = EquatableArray.From(rows);
    }

    public PolicyDatabase Database { get; }

    public EquatableArray<string> Ids { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewPolicyPruneAction(ActionId id, PolicyPrune prune) => Create(id, ActionKind.PolicyPrune, prune);

    public PolicyPrune? PolicyPrune => PayloadOf<PolicyPrune>(ActionKind.PolicyPrune);
}

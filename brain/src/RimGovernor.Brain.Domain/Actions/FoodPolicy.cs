using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// Writes one food policy's contents: a FoodPolicyIntent on Actions/Apply. The policy labelled Name (a pawn's
/// short name) allows exactly the food definitions; native makes it when missing. PawnSettingsIntent.food_policy
/// assigns it. An immutable value: the label and the canonical (sorted, deduplicated) allowed food definitions,
/// possibly none.
/// </summary>
public sealed record FoodPolicy : IActionPayload
{
    /// <summary>Go <c>NewFoodPolicy</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public FoodPolicy(string name, IEnumerable<string> definitions)
    {
        DomainException.Require(Identity.IsValidText(name) && Encoding.UTF8.GetByteCount(name) <= 80, "a food policy needs a name");
        var rows = definitions.OrderBy(d => d, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            DomainException.Require(Identity.IsValidText(rows[i]) && !(i > 0 && rows[i - 1] == rows[i]), "invalid food policy definition");
        }
        Name = name;
        Definitions = EquatableArray.From(rows);
    }

    public string Name { get; }

    public EquatableArray<string> Definitions { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewFoodPolicyAction(ActionId id, FoodPolicy policy) => Create(id, ActionKind.FoodPolicy, policy);

    public FoodPolicy? FoodPolicy => PayloadOf<FoodPolicy>(ActionKind.FoodPolicy);
}

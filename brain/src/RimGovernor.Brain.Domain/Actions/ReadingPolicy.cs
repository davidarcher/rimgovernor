using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// Writes one reading policy's contents: a ReadingPolicyIntent on Actions/Apply. The policy labelled Name (a
/// pawn's short name) allows exactly the book definitions; native makes it when missing.
/// PawnSettingsIntent.reading_policy assigns it. An immutable value: the label and the canonical (sorted,
/// deduplicated) allowed book definitions, possibly none.
/// </summary>
public sealed record ReadingPolicy : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public ReadingPolicy(string name, IEnumerable<string> definitions)
    {
        DomainException.Require(IdText.IsValidText(name) && Encoding.UTF8.GetByteCount(name) <= 80, "a reading policy needs a name");
        var rows = definitions.OrderBy(d => d, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            DomainException.Require(IdText.IsValidText(rows[i]) && !(i > 0 && rows[i - 1] == rows[i]), "invalid reading policy definition");
        }
        Name = name;
        Definitions = EquatableArray.From(rows);
    }

    public string Name { get; }

    public EquatableArray<string> Definitions { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewReadingPolicyAction(ActionId id, ReadingPolicy policy) => Create(id, ActionKind.ReadingPolicy, policy);

    public ReadingPolicy? ReadingPolicy => PayloadOf<ReadingPolicy>(ActionKind.ReadingPolicy);
}

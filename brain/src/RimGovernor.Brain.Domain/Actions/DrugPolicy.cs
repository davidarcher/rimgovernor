using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// One drug's row of a drug policy (vanilla DrugPolicyEntry). An entry allows something: joy, addiction,
/// scheduled use or a carried count; a drug with none of them is off and is left out. DaysFrequency,
/// OnlyIfMoodBelow and OnlyIfJoyBelow shape scheduled use.
/// </summary>
public sealed record DrugPolicyEntry(
    string Drug,
    bool Joy,
    bool Addiction,
    bool Scheduled,
    double DaysFrequency,
    double OnlyIfMoodBelow,
    double OnlyIfJoyBelow,
    int TakeToInventory
)
{
    /// <summary>Whether the entry allows nothing.</summary>
    public bool Off => !Joy && !Addiction && !Scheduled && TakeToInventory == 0;

    internal bool IsValid
    {
        get
        {
            static bool Unit(double v) => !double.IsNaN(v) && v >= 0 && v <= 1;
            return Identity.IsValidText(Drug)
                && !Off
                && !double.IsNaN(DaysFrequency)
                && DaysFrequency > 0
                && DaysFrequency <= 1000
                && Unit(OnlyIfMoodBelow)
                && Unit(OnlyIfJoyBelow)
                && TakeToInventory >= 0
                && TakeToInventory <= DomainConstants.MaxDrugCarry;
        }
    }
}

public static partial class DomainConstants
{
    /// <summary>Bounds <see cref="DrugPolicyEntry.TakeToInventory"/>.</summary>
    public const int MaxDrugCarry = 10;
}

/// <summary>
/// Writes one drug policy's full contents: a DrugPolicyIntent on Actions/Apply. The policy labelled Name (a
/// pawn's short name) carries exactly the entries and every other drug is off; native makes it when missing.
/// PawnSettingsIntent.drug_policy assigns it. An immutable value: the label and the canonical (sorted by drug,
/// one per drug) entries, possibly none.
/// </summary>
public sealed record DrugPolicy : IActionPayload
{
    /// <summary>Go <c>NewDrugPolicy</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public DrugPolicy(string name, IEnumerable<DrugPolicyEntry> entries)
    {
        DomainException.Require(Identity.IsValidText(name) && Encoding.UTF8.GetByteCount(name) <= 80, "a drug policy needs a name");
        var rows = entries.OrderBy(e => e.Drug, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < rows.Length; i++)
        {
            DomainException.Require(rows[i].IsValid && !(i > 0 && rows[i - 1].Drug == rows[i].Drug), "invalid drug policy entry");
        }
        Name = name;
        Entries = EquatableArray.From(rows);
    }

    public string Name { get; }

    public EquatableArray<DrugPolicyEntry> Entries { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewDrugPolicyAction(ActionId id, DrugPolicy policy) => Create(id, ActionKind.DrugPolicy, policy);

    public DrugPolicy? DrugPolicy => PayloadOf<DrugPolicy>(ActionKind.DrugPolicy);
}

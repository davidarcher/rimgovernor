namespace RimGovernor.Brain.Policy;

/// <summary>What stands between the next conversion and its order.</summary>
public enum ConversionBlock
{
    /// <summary>The moral guide holds the role and the Convert ability is charged.</summary>
    None,

    /// <summary>The ideoligion has no role that grants the Convert ability.</summary>
    NoGuideRole,

    /// <summary>The role is not held: nobody is assigned, or it is inactive. The roles Need owes the assignment.</summary>
    Unassigned,

    /// <summary>The guide's Convert ability is on cooldown or already ordered.</summary>
    Charging,
}

/// <summary>
/// The conversion a believer goal owes (#3501): the colonist to convert next and the guide who converts. Target is
/// empty when none is owed; Guide is empty while <see cref="Block"/> names why there is none.
/// </summary>
public sealed record ConversionOwed(PawnId Target, PawnId Guide, ConversionBlock Block)
{
    public static readonly ConversionOwed None = new(default, default, ConversionBlock.None);

    public bool IsOwed => !Target.IsEmpty;
}

public static class ConversionRules
{
    /// <summary>Babies cannot be converted (the ability's own rule); colonists younger than this are not targets.</summary>
    public const double MinTargetAge = 3;

    /// <summary>
    /// How many believers the Believers goals still lack: the largest shortfall among them, with no goal 0. A goal's
    /// count is the believers it wants, 0 for every free colonist; believers and colonists are counted over the free
    /// colonists whose rows show an ideoligion. Unknown while the ideoligion or a pawn row is unread.
    /// </summary>
    public static Fact<int> Shortfall(IEnumerable<GoalRecord> goals, Fact<Ideoligion> ideology, Fact<EquatableArray<WorkPawn>> pawns)
    {
        var wanted = goals.Where(g => g.Kind == GoalKind.Believers).Select(g => g.Count).ToList();
        if (wanted.Count == 0)
        {
            return Known(0);
        }
        if (!Colonists(ideology, pawns, out var believers, out var colonists))
        {
            return Unknown<int>();
        }
        return Known(wanted.Max(w => Math.Max(0, (w == 0 ? colonists : w) - believers)));
    }

    /// <summary>
    /// The conversion the goals owe now: none while no believer is lacking; otherwise the free colonist of another
    /// ideoligion with the lowest certainty (unknown last, then pawn id) who is able to be talked to, and the moral
    /// guide who converts. With nobody convertible the target is empty. Unknown while the ideoligion or a pawn row is
    /// unread.
    /// </summary>
    public static Fact<ConversionOwed> Owed(IEnumerable<GoalRecord> goals, Fact<Ideoligion> ideology, Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (!Shortfall(goals, ideology, pawns).TryGetValue(out var lacking))
        {
            return Unknown<ConversionOwed>();
        }
        if (lacking == 0)
        {
            return Known(ConversionOwed.None);
        }
        ideology.TryGetValue(out var ideo);
        pawns.TryGetValue(out var rows);
        var target = default(PawnId);
        var certainty = double.PositiveInfinity;
        foreach (var row in rows)
        {
            if (!row.PolicyInputs.TryGetValue(out var inputs) || inputs.Guest is GuestState.Hosted || inputs.Ideo is PawnIdeo.Member { IdeoId: var believed } && believed == ideo!.Facts.IdeoId)
            {
                continue;
            }
            if (!row.Available.TryGetValue(out var available) || !available || !row.Age.TryGetValue(out var age) || age < MinTargetAge)
            {
                continue;
            }
            var c = inputs.Ideo is PawnIdeo.Member member ? member.Certainty : double.PositiveInfinity;
            if (target.IsEmpty || c < certainty || (c == certainty && row.Id < target))
            {
                target = row.Id;
                certainty = c;
            }
        }
        if (target.IsEmpty)
        {
            return Known(ConversionOwed.None);
        }
        var guides = ideo!.Facts.Roles.Where(r => r.Converts).ToList();
        if (guides.Count == 0)
        {
            return Known(new ConversionOwed(target, default, ConversionBlock.NoGuideRole));
        }
        var held = guides.FirstOrDefault(r => r.Active && r.Pawns.Count > 0);
        if (held is null)
        {
            return Known(new ConversionOwed(target, default, ConversionBlock.Unassigned));
        }
        return Known(new ConversionOwed(target, held.Pawns[0], held.ConvertReady ? ConversionBlock.None : ConversionBlock.Charging));
    }

    /// <summary>The Need that converts <paramref name="target"/>: <c>ideology/convert/&lt;pawn&gt;</c>.</summary>
    public static NeedId NeedOf(PawnId target) => NeedId.Of(IdeologyFamily.FamilyId, IdeologyFamily.ConvertKind, target.Value);

    private static bool Colonists(Fact<Ideoligion> ideology, Fact<EquatableArray<WorkPawn>> pawns, out int believers, out int colonists)
    {
        believers = 0;
        colonists = 0;
        if (!ideology.TryGetValue(out var ideo) || !pawns.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var row in rows)
        {
            if (!row.PolicyInputs.TryGetValue(out var inputs))
            {
                return false;
            }
            if (inputs.Guest is GuestState.Hosted)
            {
                continue;
            }
            colonists++;
            if (inputs.Ideo is PawnIdeo.Member member && member.IdeoId == ideo.Facts.IdeoId)
            {
                believers++;
            }
        }
        return true;
    }
}

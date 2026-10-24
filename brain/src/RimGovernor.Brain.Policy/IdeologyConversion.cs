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
    /// colonists whose rows show an ideoligion. With a Believers goal, an unread ideoligion or pawn census raises
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    public static int Shortfall(IEnumerable<GoalRecord> goals, IdeologyMode ideology, Fact<EquatableArray<WorkPawn>> pawns)
    {
        var wanted = goals.Where(g => g.Kind == GoalKind.Believers).Select(g => g.Count).ToList();
        if (wanted.Count == 0 || ideology is not IdeologyMode.Installed)
        {
            return 0;
        }
        var (ideo, rows) = Inputs(ideology, pawns);
        return Lacking(wanted, ideo, rows);
    }

    /// <summary>
    /// The conversion the goals owe now: none while no believer is lacking; otherwise the free colonist of another
    /// ideoligion with the lowest certainty (a colonist of no ideoligion last, then pawn id) who is able to be talked
    /// to, and the moral guide who converts. With nobody convertible the target is empty. With a Believers goal, an
    /// unread ideoligion or pawn census raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static ConversionOwed Owed(IEnumerable<GoalRecord> goals, IdeologyMode ideology, Fact<EquatableArray<WorkPawn>> pawns)
    {
        var wanted = goals.Where(g => g.Kind == GoalKind.Believers).Select(g => g.Count).ToList();
        if (wanted.Count == 0 || ideology is not IdeologyMode.Installed)
        {
            return ConversionOwed.None;
        }
        var (ideo, rows) = Inputs(ideology, pawns);
        if (Lacking(wanted, ideo, rows) == 0)
        {
            return ConversionOwed.None;
        }
        var target = default(PawnId);
        var certainty = double.PositiveInfinity;
        foreach (var row in rows)
        {
            var inputs = row.PolicyInputs;
            if (inputs.Guest is GuestState.Hosted || inputs.Ideo is PawnIdeo.Member { IdeoId: var believed } && believed == ideo.Facts.IdeoId)
            {
                continue;
            }
            if (!row.Available || row.Age < MinTargetAge)
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
            return ConversionOwed.None;
        }
        var guides = ideo.Facts.Roles.Where(r => r.Converts).ToList();
        if (guides.Count == 0)
        {
            return new ConversionOwed(target, default, ConversionBlock.NoGuideRole);
        }
        var held = guides.FirstOrDefault(r => r.Active && r.Pawns.Count > 0);
        if (held is null)
        {
            return new ConversionOwed(target, default, ConversionBlock.Unassigned);
        }
        return new ConversionOwed(target, held.Pawns[0], held.ConvertReady ? ConversionBlock.None : ConversionBlock.Charging);
    }

    /// <summary>The Need that converts <paramref name="target"/>: <c>ideology/convert/&lt;pawn&gt;</c>.</summary>
    public static NeedId NeedOf(PawnId target) => NeedId.Of(IdeologyFamily.FamilyId, IdeologyFamily.ConvertKind, target.Value);

    private static (Ideoligion Ideology, EquatableArray<WorkPawn> Pawns) Inputs(IdeologyMode ideology, Fact<EquatableArray<WorkPawn>> pawns) =>
        (ideology is IdeologyMode.Installed { Primary: var ideo } ? ideo : throw new RequiredFactException("ideoligion"),
         pawns.TryGetValue(out var rows) ? rows : throw new RequiredFactException("pawn rows"));

    private static int Lacking(IReadOnlyList<int> wanted, Ideoligion ideo, EquatableArray<WorkPawn> rows)
    {
        var believers = 0;
        var colonists = 0;
        foreach (var row in rows)
        {
            var inputs = row.PolicyInputs;
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
        return wanted.Max(w => Math.Max(0, (w == 0 ? colonists : w) - believers));
    }
}

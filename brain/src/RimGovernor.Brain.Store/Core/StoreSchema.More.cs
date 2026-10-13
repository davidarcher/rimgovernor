namespace RimGovernor.Brain.Store;

internal static partial class StoreSchema
{
    /// <summary>Every concern's schema beside the plan core, in creation order.</summary>
    private static SchemaPart[] MorePartsOf() =>
    [
        ControlPart,
        SubmissionPart,
        StandardsPart,
        Store.ClockPart,
        TimelinePart,
        SingletonPart,
        CombatFightsPart,
    ];
}

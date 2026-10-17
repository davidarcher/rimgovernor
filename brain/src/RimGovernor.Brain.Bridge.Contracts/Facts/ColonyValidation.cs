namespace RimGovernor.Brain.Bridge.Contracts;

public static class ColonyValidation
{
    /// <summary>Go's <c>optionalID</c>: an absent id or a valid one.</summary>
    internal static bool OptionalId(bool present, string value) => !present || WireValidation.IsValidId(value);
}

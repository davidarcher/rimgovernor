namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>Go's <c>optionalID</c>: an absent id or a valid one.</summary>
    private static bool OptionalId(bool present, string value) => !present || IsValidId(value);
}

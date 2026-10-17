namespace RimGovernor.Brain.Bridge.Contracts;

public static class ColonyValidation
{
    /// <summary>An absent id or a valid one.</summary>
    internal static bool OptionalId(bool present, string value) => !present || WireValidation.IsValidId(value);
}

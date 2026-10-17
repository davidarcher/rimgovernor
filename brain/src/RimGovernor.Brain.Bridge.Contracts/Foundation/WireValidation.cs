using RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's package-level functions of <c>internal/bridge</c> with no owner type. Imported everywhere in this project
/// with a project-wide <c>using static</c>. Each ported Go file adds a part for what that file declared.
/// </summary>
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>contract(format, ...)</c>: the error every validator returns, thrown here.</summary>
    internal static BridgeContractException Contract(string message) => new(message);

    /// <summary>Go's <c>validID</c>: a native identifier is non-blank text.</summary>
    internal static void ValidId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Contract("invalid native identifier");
        }
    }

    /// <summary>Whether <see cref="ValidId"/> accepts <paramref name="value"/>.</summary>
    public static bool IsValidId(string value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>Go's <c>ValidateIdentity</c>.</summary>
    public static void ValidateIdentity(Identity? value)
    {
        if (value is null || value.MapId < 0)
        {
            throw Contract("identity presence or map");
        }
        ValidId(value.ColonyId);
        ValidId(value.LoadToken);
    }

    /// <summary>Go's <c>sameIdentity</c>: the colony, load token and map match; absent fields read as their defaults.</summary>
    internal static bool SameIdentity(Identity? a, Identity? b) =>
        string.Equals(a?.ColonyId ?? "", b?.ColonyId ?? "", StringComparison.Ordinal)
        && string.Equals(a?.LoadToken ?? "", b?.LoadToken ?? "", StringComparison.Ordinal)
        && (a?.MapId ?? 0) == (b?.MapId ?? 0);

    /// <summary>Go's <c>authorityIdentity</c>.</summary>
    internal static void AuthorityIdentity(Identity? value)
    {
        ValidateIdentity(value);
    }
}

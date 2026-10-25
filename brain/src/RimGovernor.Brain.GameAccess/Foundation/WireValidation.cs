using RimGovernor.Brain.GameFacts.Common;

namespace RimGovernor.Brain.GameAccess;

/// <summary>
/// Package-level functions with no owner type. Imported everywhere in this project
/// with a project-wide <c>using static</c>.
/// </summary>
public static class WireValidation
{

    /// <summary>A native identifier is non-blank text.</summary>
    internal static void ValidId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BridgeContractException.Contract("invalid native identifier");
        }
    }

    /// <summary>Whether <see cref="ValidId"/> accepts <paramref name="value"/>.</summary>
    public static bool IsValidId(string value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    public static void ValidateIdentity(Identity? value)
    {
        if (value is null || value.MapId < 0)
        {
            throw BridgeContractException.Contract("identity presence or map");
        }
        ValidId(value.ColonyId);
        ValidId(value.LoadToken);
    }

    /// <summary>The colony, load token and map match; absent fields read as their defaults.</summary>
    internal static bool SameIdentity(Identity? a, Identity? b) =>
        string.Equals(a?.ColonyId ?? "", b?.ColonyId ?? "", StringComparison.Ordinal)
        && string.Equals(a?.LoadToken ?? "", b?.LoadToken ?? "", StringComparison.Ordinal)
        && (a?.MapId ?? 0) == (b?.MapId ?? 0);

    internal static void AuthorityIdentity(Identity? value)
    {
        ValidateIdentity(value);
    }
}

partial class BridgeContractException
{
    /// <summary>The error every validator returns, thrown here.</summary>
    internal static BridgeContractException Contract(string message) => new(message);
}

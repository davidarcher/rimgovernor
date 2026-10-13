using System.Text;
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

    /// <summary>Go's <c>validID</c>: a native identifier is non-blank text of at most 256 bytes with no NUL.</summary>
    internal static void ValidId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 256 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw Contract("invalid native identifier");
        }
    }

    /// <summary>Whether <see cref="ValidId"/> accepts <paramref name="value"/>.</summary>
    public static bool IsValidId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && Encoding.UTF8.GetByteCount(value) <= 256 && !value.Contains('\0', StringComparison.Ordinal);
    }

    /// <summary>Go's <c>diagnostic</c>: an absent diagnostic string, or one of at most 4096 runes.</summary>
    internal static bool Diagnostic(bool present, string value)
    {
        if (!present)
        {
            return true;
        }
        var runes = 0;
        foreach (var unused in value.EnumerateRunes())
        {
            _ = unused;
            if (++runes > 4096)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Go's <c>ValidateIdentity</c>.</summary>
    public static void ValidateIdentity(Identity? value)
    {
        if (value is null || value.ColonyId is null || value.LoadToken is null || value.MapId is null || value.MapId.GetValueOrDefault() < 0)
        {
            throw Contract("identity presence or map");
        }
        ValidId((value.ColonyId ?? ""));
        ValidId((value.LoadToken ?? ""));
    }

    /// <summary>Go's <c>ValidateContext</c>.</summary>
    public static void ValidateContext(ObservationContext? value)
    {
        if (value is null || value.Tick is null || value.Tick.GetValueOrDefault() < 0 || (value.NativeGeneration is not null && value.NativeGeneration.GetValueOrDefault() == 0))
        {
            throw Contract("context presence/tick/generation");
        }
        ValidateIdentity(value.Identity);
    }

    /// <summary>Go's <c>sameIdentity</c>: the colony, load token and map match; absent fields read as their defaults.</summary>
    internal static bool SameIdentity(Identity? a, Identity? b) =>
        string.Equals(a?.ColonyId ?? "", b?.ColonyId ?? "", StringComparison.Ordinal)
        && string.Equals(a?.LoadToken ?? "", b?.LoadToken ?? "", StringComparison.Ordinal)
        && (a?.MapId ?? 0) == (b?.MapId ?? 0);

    /// <summary>Go's <c>validateUnavailable</c>: an explicit, specified, known reason and a bounded diagnostic.</summary>
    internal static void ValidateUnavailable(Unavailable? value)
    {
        if (value is null
            || value.Reason is null
            || value.Reason.GetValueOrDefault() == UnavailableReason.Unspecified
            || !Enum.IsDefined(value.Reason.GetValueOrDefault())
            || !Diagnostic(value.Detail is not null, (value.Detail ?? "")))
        {
            throw Contract("invalid unavailable");
        }
    }

    /// <summary>
    /// Go's <c>failure(v, raw)</c> for a typed refusal that a caller accepts as evidence: throws when the failure is
    /// malformed, returns when it is a valid <see cref="Failure"/>.
    /// </summary>
    internal static void ValidateFailure(Failure? value)
    {
        if (value is null || value.Code is null || (int)value.Code.GetValueOrDefault() < 1 || (int)value.Code.GetValueOrDefault() > 14 || !Diagnostic(value.Detail is not null, (value.Detail ?? "")))
        {
            throw Contract("invalid failure");
        }
        if (value.ObservedContext is not null)
        {
            ValidateContext(value.ObservedContext);
        }
    }

    /// <summary>Go's <c>authorityIdentity</c>.</summary>
    internal static void AuthorityIdentity(Identity? value)
    {
        ValidateIdentity(value);
    }

    /// <summary>
    /// Go's <c>authorityDuration</c>: a requested lease duration in milliseconds, 1000 through 30000. The clock
    /// domain's own lease-duration requests use the same bound.
    /// </summary>
    internal static void AuthorityDuration(uint? value)
    {
        if (value is null || value < 1000 || value > 30000)
        {
            throw Contract("authority duration outside1000..30000ms");
        }
    }

    /// <summary>Go's <c>buildingAttempt</c>: a complete attempt key with no unknown fields.</summary>
    internal static void BuildingAttempt(AttemptKey? attempt)
    {
        if (attempt is null
            || attempt.ControllerSessionId is null
            || attempt.ActionId is null
            || attempt.AttemptId is null
            || attempt.AttemptId.GetValueOrDefault() == 0
            || !IsValidId((attempt.ControllerSessionId ?? ""))
            || !IsValidId((attempt.ActionId ?? "")))
        {
            throw Contract("building attempt presence");
        }
    }
}

using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using RimGovernor.Protocol.Common;

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
    internal static bool IsValidId(string value)
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
        if (value is null || !value.HasColonyId || !value.HasLoadToken || !value.HasMapId || value.MapId < 0)
        {
            throw Contract("identity presence or map");
        }
        if (UnknownFields.Present(value))
        {
            throw Contract("unknown identity fields");
        }
        ValidId(value.ColonyId);
        ValidId(value.LoadToken);
    }

    /// <summary>Go's <c>ValidateContext</c>.</summary>
    public static void ValidateContext(ObservationContext? value)
    {
        if (value is null || !value.HasTick || value.Tick < 0 || (value.HasNativeGeneration && value.NativeGeneration == 0))
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
            || !value.HasReason
            || value.Reason == UnavailableReason.Unspecified
            || !Enum.IsDefined(value.Reason)
            || !Diagnostic(value.HasDetail, value.Detail))
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
        if (value is null || !value.HasCode || (int)value.Code < 1 || (int)value.Code > 14 || !Diagnostic(value.HasDetail, value.Detail))
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
        if (value is not null && UnknownFields.Present(value))
        {
            throw Contract("unknown authority fields");
        }
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
            || !attempt.HasControllerSessionId
            || !attempt.HasActionId
            || !attempt.HasAttemptId
            || attempt.AttemptId == 0
            || !IsValidId(attempt.ControllerSessionId)
            || !IsValidId(attempt.ActionId))
        {
            throw Contract("building attempt presence");
        }
        UnknownFields.Reject(attempt, "building");
    }
}

/// <summary>
/// Finds fields the generated type does not know (Go's <c>GetUnknown()</c> walk). The generated C# keeps them in a
/// private set, so the check re-reads the message's own encoding against its descriptor.
/// </summary>
internal static class UnknownFields
{
    private const int MaxDepth = 64;

    /// <summary>Whether <paramref name="message"/> or any message inside it carries a field its descriptor lacks.</summary>
    public static bool Present(IMessage message) => Present(message.Descriptor, message.ToByteArray(), 0);

    /// <summary>Throws a contract error naming <paramref name="domain"/> when unknown fields are present.</summary>
    public static void Reject(IMessage message, string domain)
    {
        if (Present(message))
        {
            throw Contract("unknown fields in " + message.Descriptor.FullName + " (" + domain + ")");
        }
    }

    private static bool Present(MessageDescriptor descriptor, byte[] data, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Contract("excessive depth in " + descriptor.FullName);
        }
        var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var field = descriptor.FindFieldByNumber(WireFormat.GetTagFieldNumber(tag));
            if (field is null)
            {
                return true;
            }
            switch (WireFormat.GetTagWireType(tag))
            {
                case WireFormat.WireType.Varint:
                    _ = input.ReadUInt64();
                    break;
                case WireFormat.WireType.Fixed32:
                    _ = input.ReadFixed32();
                    break;
                case WireFormat.WireType.Fixed64:
                    _ = input.ReadFixed64();
                    break;
                case WireFormat.WireType.LengthDelimited:
                    var bytes = input.ReadBytes();
                    if (field.FieldType == FieldType.Message && Present(field.MessageType, bytes.ToByteArray(), depth + 1))
                    {
                        return true;
                    }
                    break;
                default:
                    return true;
            }
        }
        return false;
    }
}

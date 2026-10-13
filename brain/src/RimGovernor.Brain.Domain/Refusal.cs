namespace RimGovernor.Brain.Domain;

/// <summary>
/// How RimWorld answers the same request again, as native reported it on a refusal. A refusal that names
/// no class is <see cref="Unknown"/>, which is therefore the default value.
/// </summary>
public enum RefusalClass
{
    /// <summary>Native cannot say. Never silently banned.</summary>
    Unknown,

    /// <summary>The game refuses the same request until the world changes materially, or never.</summary>
    Permanent,

    /// <summary>The request may succeed once the world changes.</summary>
    Transient,
}

/// <summary>
/// The game's own account of a refused write: the failure code name, the reason text native sent, and the
/// class it assigned. It is journaled beside <see cref="Receipt.Refused"/>.
/// </summary>
public sealed record NativeRefusal(string Code, string Reason, RefusalClass Class);

public static class RefusalClassExtensions
{
    /// <summary>Whether <paramref name="c"/> is one of the three classes.</summary>
    public static bool IsValid(this RefusalClass c) => Wire.IsDefined(c);
}

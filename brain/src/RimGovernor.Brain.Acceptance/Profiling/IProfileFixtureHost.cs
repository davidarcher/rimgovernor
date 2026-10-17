using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What a fixture op call needs (Go <c>fixtureOptions</c> as profile-capture fills it): the disposable worker root,
/// the evidence directory, the configured game, the profile, the safety-net timeout, the save to load, and the op with
/// its arguments.
/// </summary>
public sealed record ProfileFixtureCall
{
    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "";

    public bool Headless { get; init; } = true;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The profile save to load, "" for none.</summary>
    public string Save { get; init; } = "";

    /// <summary>A CI bundle may record mods since retired from the build; the call ignores the save's mod list.</summary>
    public bool IgnoreMods { get; init; }

    /// <summary>Refuse a stale or fixture-less installed mod instead of rebuilding it.</summary>
    public bool NoHeal { get; init; }

    public string Op { get; init; } = "";

    public JsonObject Args { get; init; } = new();
}

/// <summary>What a fixture op call produced (Go <c>fixtureResult</c>): the host fills Success, Response and Error.</summary>
public sealed class ProfileFixtureResult
{
    public string Op { get; init; } = "";

    public JsonObject Args { get; init; } = new();

    /// <summary>The call's evidence directory.</summary>
    public string Output { get; set; } = "";

    public bool Success { get; set; }

    /// <summary>The op's decoded reply; null when the call failed.</summary>
    public JsonObject? Response { get; set; }

    public string Error { get; set; } = "";
}

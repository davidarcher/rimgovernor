using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Host;

/// <summary>
/// What <see cref="BrainComposition"/> needs beyond the facade: the parts of Go's <c>serveConfig</c> the composition
/// reads. Every default is the Go flag's default, so <c>new BrainCompositionOptions { Profile = ... }</c> composes what
/// an autonomous <c>rimgovernor serve</c> composed.
/// </summary>
public sealed record BrainCompositionOptions
{
    /// <summary>The largest <see cref="ClockBlindTicks"/> (Go <c>maxClockBlindTicks</c>, the wire bound of <c>StartRequest.blind_tick_budget</c>).</summary>
    public const uint MaxClockBlindTicks = 1800000;

    /// <summary>The shared game profile directory whose native journal the clock window driver polls (Go <c>--profile</c>); must exist.</summary>
    public required string Profile { get; init; }

    /// <summary>The routine planner families (Go <c>RIMGOVERNOR_ROUTINE_FAMILIES</c>, every family on by default).</summary>
    public BrainFamilies Families { get; init; } = BrainFamilies.All;

    /// <summary>Acceptance only: asks native for its dev tick boost and runs every window at Ultrafast (Go <c>--clock-test-acceleration</c>, default off).</summary>
    public bool ClockTestAcceleration { get; init; }

    /// <summary>Runs each window at the speed the player last chose instead of always Ultrafast (Go <c>--follow-player-speed</c>, default off; ignored with <see cref="ClockTestAcceleration"/>).</summary>
    public bool FollowPlayerSpeed { get; init; }

    /// <summary>Arms the native blind-tick regulator past this many ticks, 1 to <see cref="MaxClockBlindTicks"/>; 0 leaves windows unregulated (Go <c>--clock-blind-ticks</c>, default 0).</summary>
    public uint ClockBlindTicks { get; init; }

    /// <summary>Draws the colony layout plan as a native overlay (Go <c>--layout-overlay</c>, default on).</summary>
    public bool LayoutOverlay { get; init; } = true;

    /// <summary>The native call bound (Go <c>--timeout</c>, default 15 s, 1 s to 1 min); the clock poll runs under <c>min(min(this, 10 s), 7 s)</c>.</summary>
    public TimeSpan NativeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the C# definition catalog of a load from its validated wire catalog and the game's Defs, which are not on the
    /// wire (see <c>FacadeNativeClient.Create</c>). Null composes a client whose catalog reads refuse.
    /// </summary>
    public Func<RimGovernor.Contracts.Wire.Observations.DefinitionCatalog, RimGovernor.Contracts.Wire.Common.Identity, DefinitionCatalog>? CatalogFactory { get; init; }

    /// <summary>
    /// Resolves the ideoligion the governor chooses for a new colony's scenario (Go <c>CreationCatalog</c> then
    /// <c>StartingIdeoligion</c>), from the creation catalog over the game's Defs. Null refuses a new colony that asks for
    /// <c>governor_ideoligion</c> as unavailable; the mod host installs it with <see cref="CatalogFactory"/> (#3116).
    /// </summary>
    public Func<string, CancellationToken, Task<RimGovernor.Contracts.Wire.Common.IdeoligionDesign>>? StartingIdeoligion { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Profile) || !Path.IsPathRooted(Profile))
        {
            throw new ArgumentException("the profile must be an absolute directory", nameof(Profile));
        }
        ArgumentNullException.ThrowIfNull(Families);
        if (ClockBlindTicks > MaxClockBlindTicks)
        {
            throw new ArgumentException($"the clock blind tick budget must be 0 through {MaxClockBlindTicks}", nameof(ClockBlindTicks));
        }
        if (NativeTimeout < TimeSpan.FromSeconds(1) || NativeTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("the native call timeout is 1 s to 1 min", nameof(NativeTimeout));
        }
    }
}

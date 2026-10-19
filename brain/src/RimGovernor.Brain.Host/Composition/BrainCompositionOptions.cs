using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Host;

/// <summary>
/// What <see cref="BrainComposition"/> needs beyond the facade: the serve settings the composition
/// reads. Every default is the serve flag's default, so <c>new BrainCompositionOptions()</c> composes what
/// an autonomous <c>rimgovernor serve</c> composed.
/// </summary>
public sealed record BrainCompositionOptions
{
    /// <summary>The routine planner families.</summary>
    public BrainFamilies Families { get; init; } = BrainFamilies.All;

    /// <summary>
    /// Acceptance only: runs every run at Hyperfast, native's dev tick boost under its 30x cap. The mod sets it for a
    /// <c>-rimgovernor-test-acceleration</c> launch unless the case unpins it (<see cref="InProcInputs.PinsHyperfast"/>).
    /// </summary>
    public bool ClockTestAcceleration { get; init; }

    /// <summary>
    /// The speed the brain requests while it owns the clock, Normal through Hyperfast (default Ultrafast, the game's own 15x).
    /// Only the first run's speed: <c>POST /api/player/clock-speed</c> changes it live from the next run. Ignored with <see cref="ClockTestAcceleration"/>, which pins Hyperfast.
    /// </summary>
    public PbClock.Speed TargetSpeed { get; init; } = PbClock.Speed.Ultrafast;

    /// <summary>Draws the colony layout plan as a native overlay.</summary>
    public bool LayoutOverlay { get; init; } = true;

    /// <summary>The native call bound; the clock poll runs under <c>min(min(this, 10 s), 7 s)</c>.</summary>
    public TimeSpan NativeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Builds the C# definition catalog of a load from its validated wire catalog and the game's Defs, which are not on the
    /// wire (see <c>FacadeNativeClient.Create</c>). Null composes a client whose catalog reads refuse.
    /// </summary>
    public Func<RimGovernor.Contracts.Wire.Common.Identity, DefinitionCatalog>? CatalogFactory { get; init; }

    /// <summary>
    /// Resolves the ideoligion the governor chooses for a new colony's scenario (<c>CreationCatalog</c> then
    /// <c>StartingIdeoligion</c>), from the creation catalog over the game's Defs. Null refuses a new colony that asks for
    /// <c>governor_ideoligion</c> as unavailable; the mod host installs it with <see cref="CatalogFactory"/> (NativeCatalogFactory, #3116; #3115 wires both).
    /// </summary>
    public Func<string, string?, CancellationToken, Task<RimGovernor.Contracts.Wire.Common.IdeoligionDesign>>? StartingIdeoligion { get; init; }

    /// <summary>
    /// The prediction-backed hunt roster estimates the food plan compares (#3130). Null composes a brain whose hunt rosters have
    /// no estimate, so a formation hunt is refused as unknown rather than priced as safe.
    /// </summary>
    public RimGovernor.Brain.Policy.IHuntEstimates? HuntEstimates { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Families);
        if (TargetSpeed == PbClock.Speed.Unspecified || !Enum.IsDefined(TargetSpeed))
        {
            throw new ArgumentException("the clock target speed is Normal, Fast, Superfast, Ultrafast or Hyperfast", nameof(TargetSpeed));
        }
        if (NativeTimeout < TimeSpan.FromSeconds(1) || NativeTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("the native call timeout is 1 s to 1 min", nameof(NativeTimeout));
        }
    }
}

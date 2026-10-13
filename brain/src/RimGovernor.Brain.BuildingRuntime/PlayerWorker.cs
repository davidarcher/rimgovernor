namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The player state a <see cref="Worker"/> reads and flips under the player's lock (Go reached these fields directly, one package).</summary>
public sealed partial class Player
{
    internal object Mu => _mu;

    internal PlayerConfig Config => _config;

    internal IPlayerSession Session => _session;

    internal IWorldSource Worlds => _worlds;

    internal CancellationToken Lifetime => _lifetime;

    internal bool Closing
    {
        get => _closing;
        set => _closing = value;
    }

    internal bool Closed => _closed;

    /// <summary>A worker owns the step loop; set once under <see cref="Mu"/> and never cleared.</summary>
    internal bool WorkerAttached { get; set; }

    /// <summary>Ends the current epoch so queued and running player calls stop; callers hold <see cref="Mu"/>.</summary>
    internal void CancelEpoch() => _epochSource.Cancel();
}

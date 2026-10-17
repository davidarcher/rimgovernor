using System.Globalization;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Contracts.Wire;
using Wl = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A finished <c>lifecycle_new_colony</c> start (Go <c>NewColonyRun</c>).</summary>
public sealed record NewColonyRun
{
    /// <summary>The phases seen in wire order, each once.</summary>
    public IReadOnlyList<Wl.NewColonyPhase> Phases { get; init; } = [];

    /// <summary>The completed reply: saveName, seed, paused, byteLength, context.</summary>
    public Wl.NewColonyCompleted Completed { get; init; } = new();

    /// <summary>The last rerollCount a pending reply carried (completed replies carry none), 0 when the first roll was accepted.</summary>
    public int Rerolls { get; init; }
}

/// <summary>The new-colony op's driver (Go <c>newcolony.go</c>).</summary>
public static class NewColony
{
    /// <summary>The timeoutMs a harness start asks for when the caller names none: the team policy's reroll budget can take minutes.</summary>
    public static readonly TimeSpan NewColonyTimeout = TimeSpan.FromMinutes(10);

    private const int PollBudget = 6000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The order a start's phases must follow.</summary>
    private static readonly Wl.NewColonyPhase[] PhaseOrder =
    [
        Wl.NewColonyPhase.GeneratingWorld,
        Wl.NewColonyPhase.ChoosingTile,
        Wl.NewColonyPhase.RollingColonists,
        Wl.NewColonyPhase.GeneratingMap,
        Wl.NewColonyPhase.Finishing,
        Wl.NewColonyPhase.Saving,
    ];

    /// <summary>Wraps a spec in a <c>lifecycle_new_colony</c> request; a zero timeout is <see cref="NewColonyTimeout"/>.</summary>
    public static Wl.NewColonyRequest NewColonyRequest(string id, Wl.NewColonySpec spec, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = NewColonyTimeout;
        }

        return new Wl.NewColonyRequest { RequestId = id, Spec = spec, TimeoutMs = (uint)timeout.TotalMilliseconds };
    }

    /// <summary>
    /// Starts a colony (<c>lifecycle_new_colony</c>) and polls until it completes. The game must be at the main menu;
    /// it ends paused with a live map, the naming dialog confirmed and the save in the profile's Saves. A terminal
    /// failure is an error (a failed start can leave game state behind: restart the game before retrying); phases
    /// must follow <see cref="PhaseOrder"/> without going back.
    /// </summary>
    public static async Task<NewColonyRun> RunNewColonyAsync(Harness harness, Wl.NewColonyRequest request, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(request);
        var phases = new List<Wl.NewColonyPhase>();
        var rerolls = 0;
        var reply = await harness.CallAsync(label + "-start", NativeOps.StartNewColony, request, cancellationToken).ConfigureAwait(false);
        var last = -1;
        using var pace = Wait.Pace(PollInterval);
        for (var attempt = 0; attempt < PollBudget; attempt++)
        {
            if (reply.Outcome?.Failure is { } failure)
            {
                throw new AcceptanceException($"{label}: new colony failed: {failure.Code}: {failure.Detail}");
            }

            if (reply.Outcome?.Completed is { } completed)
            {
                if (!phases.Contains(Wl.NewColonyPhase.Saving))
                {
                    throw new AcceptanceException($"{label}: completed without reporting Saving (phases [{string.Join(' ', phases)}])");
                }

                return new NewColonyRun { Phases = phases, Completed = completed, Rerolls = rerolls };
            }

            if (reply.Outcome?.Pending is not { } pending)
            {
                throw new AcceptanceException($"{label}: unexpected reply: {WireJson.Serialize(reply)}");
            }

            var phase = pending.Phase ?? Wl.NewColonyPhase.Unspecified;
            var index = Array.IndexOf(PhaseOrder, phase);
            if (index < 0)
            {
                throw new AcceptanceException($"{label}: unknown phase {phase}");
            }

            if (index < last)
            {
                throw new AcceptanceException($"{label}: phase went backwards to {phase} after {phases[^1]}");
            }

            if (index != last)
            {
                phases.Add(phase);
                last = index;
            }

            rerolls = (int)(pending.RerollCount ?? 0);
            await pace.NextAsync(cancellationToken).ConfigureAwait(false);
            reply = await harness.CallAsync(
                string.Create(CultureInfo.InvariantCulture, $"{label}-poll-{attempt}"),
                NativeOps.PollNewColony,
                new Wl.RequestStatus { RequestId = request.RequestId },
                cancellationToken).ConfigureAwait(false);
        }

        throw new AcceptanceException($"{label}: new colony did not complete within the polling budget (phases [{string.Join(' ', phases)}])");
    }
}

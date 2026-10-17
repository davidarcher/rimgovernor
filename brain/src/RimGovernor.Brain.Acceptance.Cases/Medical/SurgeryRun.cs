using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>A surgery bill the brain queued: the patient, the body part index (-1 without one) and the recipe.</summary>
internal readonly record struct SurgeryIntent(string Pawn, int Part, string Recipe);

/// <summary>
/// Hosts the brain with the medical family and runs the clock (Go <c>surgeryRun</c>). The Go run read the durable
/// journal for the surgery intents, the rounds tick and the goal state. The brain is in-process here and its journal
/// has no reader in this project, so the same three facts come from what the player API and the game show:
/// <list type="bullet">
/// <item>the intent history is the set of surgery bills ever seen on a pawn's health read, kept across polls (a queued
/// bill is the applied intent; a plan that retired before the poll still left its bill, or its completed operation);</item>
/// <item>the tick is the live game tick the player API reports;</item>
/// <item>recovered reads the goal's sampled finding when the host wired a journal reader, else the routines review's
/// no-op rows (a satisfied concern).</item>
/// </list>
/// </summary>
internal sealed class SurgeryRun
{
    /// <summary>How far the served clock must carry the game past a queued bill for native doctors to finish the operation.</summary>
    internal const ulong ServedTicks = 15000;

    private readonly ISession _session;
    private readonly BrainRun _run;
    private readonly List<SurgeryIntent> _history = [];
    private readonly HashSet<SurgeryIntent> _seen = [];

    private SurgeryRun(ISession session, BrainRun run)
    {
        _session = session;
        _run = run;
    }

    /// <summary>Launches the medical and work families and keeps authority granted under <paramref name="prefix"/>.</summary>
    internal static async Task<SurgeryRun> StartAsync(ISession s, string prefix, CancellationToken cancellationToken) =>
        new(s, await BrainRun.StartAsync(s, ["medical", "work"], prefix, keepPrefix: prefix, cancellationToken).ConfigureAwait(false));

    /// <summary>The live game tick (zero before the first snapshot).</summary>
    internal async Task<ulong> TickAsync(CancellationToken cancellationToken) =>
        await _run.TickAsync(cancellationToken).ConfigureAwait(false) ?? 0;

    /// <summary>Ends the keep-alive and the handle; the report keeps the authority counters (idempotent).</summary>
    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        var keep = await _run.StopAsync(cancellationToken).ConfigureAwait(false);
        if (keep is not null)
        {
            _session.Report["authority_reacquisitions"] = keep;
        }
    }

    /// <summary>Reads every humanlike pawn's queued surgery bills into the history and returns it in first-seen order.</summary>
    internal async Task<IReadOnlyList<SurgeryIntent>> IntentsAsync(CancellationToken cancellationToken)
    {
        var observed = await MedicalKit.ListPawnsAsync(_session.Harness, "surgery-bills", _session.Identity, MedicalKit.HumanFilter(), MedicalKit.Details(health: true), cancellationToken).ConfigureAwait(false);
        foreach (var row in observed["pawns"].Rows())
        {
            var pawn = row["pawn"].RefId();
            foreach (var bill in MedicalKit.Surgery(row["health"].AsMap() ?? [])["surgeryBills"].Rows())
            {
                var intent = new SurgeryIntent(pawn, (int)bill["partIndex"].AsNumber(), bill["recipe"].AsString());
                if (intent.Recipe.Length > 0 && _seen.Add(intent))
                {
                    _history.Add(intent);
                }
            }
        }

        return [.. _history];
    }

    /// <summary>Whether the review binds <paramref name="concern"/> to a goal whose finding is met.</summary>
    /// <summary>Polls the intent history and <paramref name="probe"/> until it reports done, within <paramref name="limit"/>.</summary>
    internal Task UntilAsync(TimeSpan limit, Func<IReadOnlyList<SurgeryIntent>, CancellationToken, Task<(string Signature, bool Done)>> probe, CancellationToken cancellationToken) =>
        NativeReads.WithinAsync(
            limit,
            "surgery wait",
            async ct =>
            {
                var wait = new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = _run.Service.Exited };
                await wait.WaitProgressAsync(
                    async pc =>
                    {
                        var intents = await IntentsAsync(pc).ConfigureAwait(false);
                        return await probe(intents, pc).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>The first intent on <paramref name="pawn"/> (and <paramref name="part"/> when it is not negative) whose recipe matches (any recipe when empty).</summary>
    internal static SurgeryIntent? Find(IEnumerable<SurgeryIntent> surgeries, string pawn, int part, string recipe)
    {
        foreach (var s in surgeries)
        {
            if (s.Pawn == pawn && (part < 0 || s.Part == part) && (recipe.Length == 0 || s.Recipe == recipe))
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the service running until the tick has moved <see cref="ServedTicks"/> past the first read (a queued bill
    /// is clock work, so a clock parked on it stalls the wait), stops the service, then reads the patient's health once
    /// for the operation.
    /// </summary>
    internal async Task ServedAsync(string label, string patient, Func<JsonObject, bool> done, CancellationToken cancellationToken)
    {
        ulong from = 0, at = 0;
        try
        {
            await NativeReads.WithinAsync(
                TimeSpan.FromMinutes(4),
                label + " served clock",
                async ct =>
                {
                    var wait = new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = _run.Service.Exited };
                    await wait.WaitProgressAsync(
                        async pc =>
                        {
                            var tick = await TickAsync(pc).ConfigureAwait(false);
                            if (from == 0)
                            {
                                from = tick;
                            }

                            at = tick;
                            return (Wait.Signature(tick), tick >= from + ServedTicks);
                        },
                        ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{label}: served clock stalled at tick {at} (from {from}): {e.Message}"), e);
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);
        _ = _session.Reattach();
        var health = await MedicalKit.PawnHealthAsync(_session, label + "-after", patient, cancellationToken).ConfigureAwait(false);
        if (!done(health))
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: operation not completed after {at - from} served ticks (missing={MedicalKit.Surgery(health)["missingParts"].ToJson()} bills={MedicalKit.Surgery(health)["surgeryBills"].ToJson()})"));
        }
    }
}

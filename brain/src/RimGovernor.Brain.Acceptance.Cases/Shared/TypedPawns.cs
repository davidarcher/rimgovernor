using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The typed pawn reads and the one-action apply a <see cref="TypedCase"/> takes on a pawn.</summary>
internal static class TypedPawns
{
    /// <summary>The observations_list_pawns rows <paramref name="filter"/> selects, with <paramref name="details"/>.</summary>
    internal static async Task<IReadOnlyList<Observations.PawnState>> ListAsync(
        TypedSession s, string label, Observations.PawnFilter? filter, Observations.PawnDetails? details, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest { Scope = s.Scope, Filter = filter, Details = details }, cancellationToken).ConfigureAwait(false);
        return [.. reply.Pawns];
    }

    /// <summary>The one row of pawn <paramref name="id"/>; a missing row fails <paramref name="label"/>.</summary>
    internal static async Task<Observations.PawnState> OneAsync(TypedSession s, string label, string id, Observations.PawnDetails? details, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(s, label, new Observations.PawnFilter { Ids = [id] }, details, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(p => p.Pawn.Id == id) ?? throw new AcceptanceException($"{label}: pawn {id} not read");
    }

    /// <summary>The first free, standing colonist by id.</summary>
    internal static async Task<Observations.PawnState> FirstColonistAsync(TypedSession s, string label, Observations.PawnDetails? details, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(s, label, new Observations.PawnFilter { Colonist = true, Downed = false }, details, cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(p => p.Pawn.Id, StringComparer.Ordinal).FirstOrDefault() ?? throw new AcceptanceException($"{label}: no standing colonist");
    }

    /// <summary>Applies the one action <paramref name="intent"/> under <paramref name="key"/> and returns its result.</summary>
    internal static async Task<Operations.ActionResult> ApplyAsync(TypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = key, Intent = intent }],
        }, cancellationToken).ConfigureAwait(false);
        return reply is { BatchFailure: null, Results: [{ } result] }
            ? result
            : throw new AcceptanceException($"{key}: expected one action result, got {WireJson.Serialize(reply)}");
    }

    /// <summary>Applies <paramref name="intent"/> and returns its receipt's effect; a refusal or failure fails <paramref name="key"/>.</summary>
    internal static async Task<Receipts.EffectEvidenceEffect?> AppliedAsync(TypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var result = await ApplyAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        var receipt = result.Outcome?.Applied ?? throw new AcceptanceException($"{key}: not applied: {result.Outcome}");
        return (receipt.Outcome?.Applied?.Observed ?? (receipt.Outcome as Receipts.ReceiptOutcome.NoChangeCase)?.Value.Observed)?.Effect;
    }
}

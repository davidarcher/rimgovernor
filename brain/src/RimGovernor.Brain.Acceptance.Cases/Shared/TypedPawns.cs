using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The typed pawn reads and the one-action apply a <see cref="TypedCase"/> takes on a pawn.</summary>
internal static class TypedPawns
{
    /// <summary>The observations_list_pawns rows <paramref name="filter"/> selects, each whole.</summary>
    internal static async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.PawnState>> ListAsync(
        TypedSession s, string label, RimGovernor.Brain.GameFacts.Observations.PawnFilter? filter, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest { Scope = s.Scope, Filter = filter }, cancellationToken).ConfigureAwait(false);
        return [.. reply.Pawns];
    }

    /// <summary>The one row of pawn <paramref name="id"/>; a missing row fails <paramref name="label"/>.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Observations.PawnState> OneAsync(TypedSession s, string label, string id, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(s, label, new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Ids = [id] }, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(p => p.Pawn.Id == id) ?? throw new AcceptanceException($"{label}: pawn {id} not read");
    }

    /// <summary>The first free, standing colonist by id.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Observations.PawnState> FirstColonistAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var rows = await ListAsync(s, label, new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Only, Downed = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Excluded }, cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(p => p.Pawn.Id, StringComparer.Ordinal).FirstOrDefault() ?? throw new AcceptanceException($"{label}: no standing colonist");
    }

    /// <summary>Applies the one action <paramref name="intent"/> under <paramref name="key"/> and returns its result.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Operations.ActionResult> ApplyAsync(TypedSession s, string key, RimGovernor.Brain.GameOrders.Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new RimGovernor.Brain.GameOrders.Operations.Action { Key = key, Intent = intent }], DeferSnapshot = false }, cancellationToken).ConfigureAwait(false);
        return reply is { BatchFailure: null, Results: [{ } result] }
            ? result
            : throw new AcceptanceException($"{key}: expected one action result, got {OrdersJson.Serialize(reply)}");
    }

    /// <summary>Applies <paramref name="intent"/> and returns its receipt's effect; a refusal or failure fails <paramref name="key"/>.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect?> AppliedAsync(TypedSession s, string key, RimGovernor.Brain.GameOrders.Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var result = await ApplyAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        var receipt = result.Outcome?.Applied ?? throw new AcceptanceException($"{key}: not applied: {result.Outcome}");
        return (receipt.Outcome?.Applied?.Observed ?? (receipt.Outcome as RimGovernor.Brain.GameFacts.Receipts.ReceiptOutcome.NoChangeCase)?.Value.Observed)?.Effect;
    }
}

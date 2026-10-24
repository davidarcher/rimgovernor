using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Typed operations_apply and pawn reads a <see cref="TypedCase"/> body shares.</summary>
internal static class TypedApply
{
    /// <summary>Applies one action under <paramref name="key"/> and returns its outcome; a batch failure or a missing result fails the case.</summary>
    internal static async Task<Operations.ActionResultOutcome> OneAsync(TypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = key, Intent = intent }],
        }, cancellationToken).ConfigureAwait(false);
        return reply.Results.ToList() is [{ Outcome: { } outcome }]
            ? outcome
            : throw new AcceptanceException($"{key}: want one result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
    }

    /// <summary>Applies one action that must apply (or match), returning its receipt.</summary>
    internal static async Task<Operations.ActionResultOutcome> AppliedAsync(TypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var outcome = await OneAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        return outcome.Applied is not null ? outcome : throw new AcceptanceException($"{key}: want applied, got {outcome}");
    }

    /// <summary>Applies one action that native must refuse, returning the refusal.</summary>
    internal static async Task<Operations.Refusal> RefusedAsync(TypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var outcome = await OneAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        return outcome.Refused ?? throw new AcceptanceException($"{key}: want refused, got {outcome}");
    }

    /// <summary>The observations_list_pawns row of pawn <paramref name="id"/>.</summary>
    internal static async Task<Observations.PawnState> PawnAsync(TypedSession s, string label, string id, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest
        {
            Scope = s.Scope,
            Filter = new Observations.PawnFilter { Ids = [id] },
        }, cancellationToken).ConfigureAwait(false);
        var pawns = reply.Pawns.ToList();
        return pawns is [var pawn] ? pawn : throw new AcceptanceException($"{label}: want pawn {id}, got {pawns.Count} rows");
    }

    /// <summary>The colony facts' upkeep section (observations_read_colony_facts, planning off); an unread section fails the case.</summary>
    internal static async Task<Observations.UpkeepFacts> UpkeepAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        return facts.Upkeep?.Outcome ?? throw new AcceptanceException($"{label}: upkeep facts unavailable: {facts.Upkeep?.Outcome}");
    }

    /// <summary>The observations_get_cells grid over the inclusive staged-site rectangle <paramref name="rect"/> (absolute cells).</summary>
    internal static async Task<MapCellGrid> CellsAsync(TypedSession s, string label, Fx.StageRect rect, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.GetCells, new Observations.GetCellsRequest
        {
            Scope = s.Scope,
            Rectangle = new Observations.Rectangle
            {
                Minimum = new Common.Cell { X = rect.X, Z = rect.Z },
                Maximum = new Common.Cell { X = rect.X + rect.Width - 1, Z = rect.Z + rect.Height - 1 },
            },
        }, cancellationToken).ConfigureAwait(false);
        return MapCellGrid.Of(TypedReads.Observed(label, NativeOps.GetCells, reply, reply.Outcome?.Observed).Grid);
    }
}

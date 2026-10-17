using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The typed pawn order and pawn read the mood and bed cases share: one Actions/Apply intent, one pawn row.</summary>
internal static class TypedPawnOps
{
    /// <summary>Applies <paramref name="intent"/> as the one action <paramref name="key"/>; anything but Applied fails the case.</summary>
    public static async Task<Operations.ActionResult> ApplyAsync(ITypedSession s, string key, Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = key, Intent = intent }],
        }, cancellationToken).ConfigureAwait(false);
        var results = reply.Results.ToList();
        if (results is not [{ Outcome.Applied: not null } result])
        {
            throw new AcceptanceException($"{key}: not applied: {string.Join("; ", results)}");
        }

        s.RecordWire(key, result);
        return result;
    }

    /// <summary>
    /// Applies <paramref name="intent"/> as the one action <paramref name="key"/> and requires a native refusal whose reason
    /// names <paramref name="reason"/>.
    /// </summary>
    public static async Task RefusedAsync(ITypedSession s, string key, Operations.ActionIntent intent, string reason, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = key, Intent = intent }],
        }, cancellationToken).ConfigureAwait(false);
        var results = reply.Results.ToList();
        if (results is not [{ Outcome.Refused: { } refusal } result] || refusal.Reason?.Contains(reason, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new AcceptanceException($"{key}: want a refusal naming \"{reason}\", got {string.Join("; ", results)}");
        }

        s.RecordWire(key, result);
    }

    /// <summary>The draft intent for <paramref name="pawnId"/>.</summary>
    public static Operations.ActionIntent Draft(string pawnId) => new Operations.DraftIntent { PawnId = pawnId, Drafted = true };

    /// <summary>The GiveJob intent ordering <paramref name="pawnId"/> to run <paramref name="job"/> on <paramref name="targets"/>.</summary>
    public static Operations.ActionIntent GiveJob(string pawnId, string job, params string[] targets) => new Operations.GiveJobIntent
    {
        Pawn = new Common.Ref { Id = pawnId },
        Job = job,
        Targets = [.. targets.Select(id => new Common.Ref { Id = id })],
    };

    /// <summary>The one <c>observations_list_pawns</c> row of <paramref name="pawnId"/>, dead included, with health; no row is null.</summary>
    public static async Task<Observations.PawnState?> PawnAsync(ITypedSession s, string label, string pawnId, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest
        {
            Scope = s.Scope,
            Filter = new Observations.PawnFilter { Ids = [pawnId], IncludeDead = true },
            Details = new Observations.PawnDetails { Health = true },
        }, cancellationToken).ConfigureAwait(false);
        var rows = TypedReads.Observed(label, NativeOps.ListPawns, reply, reply.Outcome?.Observed).Pawns.ToList();
        return rows switch
        {
            [] => null,
            [var row] when row.Pawn.Id == pawnId => row,
            _ => throw new AcceptanceException($"{label}: want one row for {pawnId}, got {string.Join("; ", rows)}"),
        };
    }
}

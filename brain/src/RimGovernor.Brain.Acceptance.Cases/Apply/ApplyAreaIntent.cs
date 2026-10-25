using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>apply/area-intent</c>: AreaIntent on Actions/Apply (#1321). A bot area is created with
/// a cell, gains and loses cells, a resent create applies again on the same area, it is deleted (twice), and the home
/// area gains and loses one cell; every receipt carries the area load id and cell count after the edit.
/// </summary>
internal static class ApplyAreaIntent
{
    internal static TypedCase Case() => new()
    {
        Name = "apply/area-intent",
        Scope = "AreaIntent on Actions/Apply (#1321): a bot area is created with a cell, gains and loses cells, a resent "
            + "create applies again on the same area, it is deleted (twice), and the home area gains and loses one cell; every "
            + "receipt carries the area load id and cell count after the edit.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Run = RunAsync,
    };

    private static Operations.AreaRect Cell(int x, int z) => new() { MinX = x, MinZ = z, MaxX = x, MaxZ = z };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Receipts.AreaEffect> ApplyAsync(string key, Operations.AreaIntent area)
        {
            var outcome = await TypedApply.OneAsync(s, key, area, cancellationToken).ConfigureAwait(false);
            var effect = ApplyCases.Effect(outcome)?.AreaEdit ?? throw new AcceptanceException($"{key}: expected an applied area edit, got {outcome}");
            s.RecordWire(key, effect);
            return effect;
        }

        async Task<Receipts.AreaEffect> StepAsync(string key, Operations.AreaOperation op, Operations.AreaRect[] cells, int wantCount, bool wantPresent)
        {
            var effect = await ApplyAsync(key, new Operations.AreaIntent { Operation = op, Key = "accept", Rects = [.. cells], Home = false, PollutionClear = false }).ConfigureAwait(false);
            if ((effect.Present == true) != wantPresent || effect.CellCount != wantCount || !string.IsNullOrEmpty(effect.AreaId) != wantPresent)
            {
                throw new AcceptanceException($"{key}: expected present={wantPresent} with {wantCount} cells, got {effect}");
            }

            return effect;
        }

        var created = await StepAsync("area-create", Operations.AreaOperation.Create, [Cell(10, 10)], 1, true).ConfigureAwait(false);
        _ = await StepAsync("area-set", Operations.AreaOperation.SetCells, [Cell(10, 11), Cell(11, 10)], 3, true).ConfigureAwait(false);
        _ = await StepAsync("area-clear", Operations.AreaOperation.ClearCells, [Cell(10, 10)], 2, true).ConfigureAwait(false);
        var again = await StepAsync("area-create-again", Operations.AreaOperation.Create, [], 2, true).ConfigureAwait(false);
        if (again.AreaId != created.AreaId)
        {
            throw new AcceptanceException($"re-create made a second area {again.AreaId} beside {created.AreaId}");
        }

        foreach (var key in new[] { "area-delete", "area-delete-again" })
        {
            _ = await StepAsync(key, Operations.AreaOperation.Delete, [], 0, false).ConfigureAwait(false);
        }

        async Task<int?> HomeAsync(string key, Operations.AreaOperation op)
        {
            var effect = await ApplyAsync(key, new Operations.AreaIntent { Operation = op, Home = true, Rects = [Cell(12, 12)], PollutionClear = false }).ConfigureAwait(false);
            return effect.Home == true && !string.IsNullOrEmpty(effect.AreaId)
                ? effect.CellCount
                : throw new AcceptanceException($"{key}: expected the home area, got {effect}");
        }

        var set = await HomeAsync("home-set", Operations.AreaOperation.SetCells).ConfigureAwait(false);
        var cleared = await HomeAsync("home-clear", Operations.AreaOperation.ClearCells).ConfigureAwait(false);
        if (cleared != set - 1)
        {
            throw new AcceptanceException($"home clear left {cleared} cells after set reached {set}");
        }
    }
}

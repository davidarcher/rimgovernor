using RimGovernor.Brain.NativeFacade;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The per-pawn Assign-tab settings a PawnSettingsIntent writes (#1299, #1305, #1307, #1310): each row applies one arm
/// through operations_apply on a lab colonist and reads the native setting back. Which pawn gets which value is policy
/// (policy/hostility_test.go, medicine_carry_test.go, pawn_names_test.go); these prove the native write and read.
/// </summary>
internal static class PawnSettings
{
    private static readonly Observations.PawnDetails SettingsDetails = new() { Settings = true, Work = true };

    private sealed record Row(string Name, string Scope, Func<ITypedSession, CancellationToken, Task> Run);

    private static readonly Row[] Rows =
    [
        new(
            "pawn/hostility",
            "The hostility_response arm sets a colonist's HostilityResponseMode to Flee, the pawn read's hostility_response reads Flee, "
                + "and the same setting again applies UNCHANGED.",
            HostilityAsync),
        new(
            "pawn/self-tend",
            "The self_tend arm turns a doctor-capable colonist's playerSettings.selfTend off and on, each reads back through the pawn "
                + "read, and the read carries the pawn's medical tend quality.",
            SelfTendAsync),
        new(
            "pawn/medicine-carry",
            "The medicine_carry arm sets a colonist's Medicine inventory-stock count, the pawn read's policy inputs read that count, "
                + "and the same count again applies UNCHANGED.",
            MedicineCarryAsync),
        new(
            "pawn/nickname",
            "The nickname arm naming a colonist's own short name draws a fresh, unnumbered name from its name bank that no other owned "
                + "pawn holds (the population read's owned names), and the same intent again applies UNCHANGED.",
            NicknameAsync),
    ];

    internal static IEnumerable<TypedCase> All() => Rows.Select(row => new TypedCase
    {
        Name = row.Name,
        Scope = row.Scope,
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            await row.Run(s, ct).ConfigureAwait(false);
        },
    });

    /// <summary>Applies one setting arm and returns its field outcomes.</summary>
    private static async Task<IReadOnlyList<Receipts.FieldOutcome?>> ApplyAsync(ITypedSession s, string key, string pawn, Operations.PawnSettingsIntentSetting setting, CancellationToken cancellationToken)
    {
        var effect = await TypedPawns.AppliedAsync(s, key, new Operations.PawnSettingsIntent { PawnId = pawn, Setting = setting }, cancellationToken).ConfigureAwait(false);
        var fields = effect?.Settings?.Fields.Select(f => f.Outcome).ToList();
        return fields is { Count: > 0 } ? fields : throw new AcceptanceException($"{key}: no settings field in {effect}");
    }

    private static async Task ExpectUnchangedAsync(ITypedSession s, string key, string pawn, Operations.PawnSettingsIntentSetting setting, CancellationToken cancellationToken)
    {
        var again = await ApplyAsync(s, key, pawn, setting, cancellationToken).ConfigureAwait(false);
        if (again.Any(o => o != Receipts.FieldOutcome.Unchanged))
        {
            throw new AcceptanceException($"{key}: want UNCHANGED, got [{string.Join(' ', again)}]");
        }
    }

    private static Task<Observations.PawnState> ReadAsync(ITypedSession s, string label, string pawn, CancellationToken cancellationToken) =>
        TypedPawns.OneAsync(s, label, pawn, SettingsDetails, cancellationToken);

    private static async Task HostilityAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var pawn = await TypedPawns.FirstColonistAsync(s, "colonist", SettingsDetails, cancellationToken).ConfigureAwait(false);
        var id = pawn.Pawn.Id;
        if (pawn.Settings?.HostilityResponse == Operations.HostilityResponse.Flee)
        {
            throw new AcceptanceException($"{id} already flees: the write would prove nothing");
        }

        var flee = new Operations.PawnSettingsIntentSetting.HostilityResponseCase(Operations.HostilityResponse.Flee);
        _ = await ApplyAsync(s, "flee", id, flee, cancellationToken).ConfigureAwait(false);
        var read = await ReadAsync(s, "flee-readback", id, cancellationToken).ConfigureAwait(false);
        s.RecordWire("hostility", read.Settings?.HostilityResponse);
        if (read.Settings?.HostilityResponse != Operations.HostilityResponse.Flee)
        {
            throw new AcceptanceException($"{id} reads hostility {read.Settings?.HostilityResponse}, want Flee");
        }

        await ExpectUnchangedAsync(s, "flee-again", id, flee, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SelfTendAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var colonists = await TypedPawns.ListAsync(s, "colonists", new Observations.PawnFilter { Colonist = true, Downed = false }, SettingsDetails, cancellationToken).ConfigureAwait(false);
        var doctor = colonists.OrderBy(p => p.Pawn.Id, StringComparer.Ordinal)
            .FirstOrDefault(p => p.Settings?.Work.Any(w => w.DefName == "Doctor" && !w.Disabled) == true)
            ?? throw new AcceptanceException("no doctor-capable colonist");
        var id = doctor.Pawn.Id;
        foreach (var on in new[] { false, true })
        {
            var key = on ? "self-tend-on" : "self-tend-off";
            _ = await ApplyAsync(s, key, id, new Operations.PawnSettingsIntentSetting.SelfTendCase(on), cancellationToken).ConfigureAwait(false);
            var read = await ReadAsync(s, key + "-readback", id, cancellationToken).ConfigureAwait(false);
            if (read.Settings?.SelfTend != on)
            {
                throw new AcceptanceException($"{key}: {id} reads self-tend {read.Settings?.SelfTend}");
            }

            if (on)
            {
                var quality = read.Settings?.PolicyInputs?.MedicalTendQuality;
                s.Record("tend_quality", quality);
                if (quality is null)
                {
                    throw new AcceptanceException($"doctor-capable {id} read no medical tend quality");
                }
            }
        }
    }

    private static async Task MedicineCarryAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        const int Count = 2;
        var pawn = await TypedPawns.FirstColonistAsync(s, "colonist", SettingsDetails, cancellationToken).ConfigureAwait(false);
        var id = pawn.Pawn.Id;
        var carry = new Operations.PawnSettingsIntentSetting.MedicineCarryCase(Count);
        _ = await ApplyAsync(s, "medicine-carry", id, carry, cancellationToken).ConfigureAwait(false);
        var read = await ReadAsync(s, "medicine-carry-readback", id, cancellationToken).ConfigureAwait(false);
        var stock = read.Settings?.PolicyInputs?.InventoryStock.FirstOrDefault(i => i.Group == "Medicine");
        s.RecordWire("medicine_stock", stock);
        if (stock?.Count != Count)
        {
            throw new AcceptanceException($"{id} reads Medicine stock {stock}, want {Count}");
        }

        await ExpectUnchangedAsync(s, "medicine-carry-again", id, carry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task NicknameAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        async Task<Dictionary<string, string>> NamesAsync(string label)
        {
            var reply = await s.CallAsync(label, NativeOps.ReadPopulation, new Observations.PopulationRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
            var population = TypedReads.Observed(label, NativeOps.ReadPopulation, reply, reply.Outcome?.Observed);
            return population.OwnedNames.Where(n => n.PawnId is not null).ToDictionary(n => n.PawnId!, n => n.ShortName ?? "", StringComparer.Ordinal);
        }

        var pawn = await TypedPawns.FirstColonistAsync(s, "colonist", null, cancellationToken).ConfigureAwait(false);
        var id = pawn.Pawn.Id;
        var before = await NamesAsync("names-before").ConfigureAwait(false);
        var old = before.GetValueOrDefault(id) ?? throw new AcceptanceException($"{id} has no owned name");
        var leave = new Operations.PawnSettingsIntentSetting.NicknameCase(old);
        _ = await ApplyAsync(s, "nickname", id, leave, cancellationToken).ConfigureAwait(false);
        var after = await NamesAsync("names-after").ConfigureAwait(false);
        var fresh = after.GetValueOrDefault(id, "");
        s.Record("old", old);
        s.Record("fresh", fresh);
        if (fresh.Length == 0 || string.Equals(fresh, old, StringComparison.OrdinalIgnoreCase) || fresh.Any(char.IsAsciiDigit)
            || after.Any(n => n.Key != id && string.Equals(n.Value, fresh, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AcceptanceException($"{id} renamed from \"{old}\" to \"{fresh}\", want a fresh unnumbered name no other owned pawn holds");
        }

        await ExpectUnchangedAsync(s, "nickname-again", id, leave, cancellationToken).ConfigureAwait(false);
    }
}

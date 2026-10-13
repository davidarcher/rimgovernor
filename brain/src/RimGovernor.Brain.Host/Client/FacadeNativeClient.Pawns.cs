using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;
using PolicyMechInput = RimGovernor.Brain.Policy.MechInput;

namespace RimGovernor.Brain.Host;

// Go combat_pawns.go, pawns.go, work_pawns.go, tend_pawns.go, home_colonists.go and biotech_mechs.go: the pawn-list
// reads. Each differs only in which PawnDetails families it asks for; the reply validator refuses any family the
// request did not select (PawnReadChecks, FacadeNativeClient.PawnChecks.cs).
internal sealed partial class FacadeNativeClient
{
    /// <summary>The main-thread milliseconds the tend pair reachability may spend (Go <c>TendReachBudgetMS</c>).</summary>
    internal const uint PawnsTendReachBudgetMs = 50;

    /// <summary>
    /// Go <c>ReadPawns</c>: observes exact ids including dead pawns. Missing rows, CAS tokens and drafted state never
    /// imply death, write permission or controller ownership.
    /// </summary>
    internal Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(), cancellationToken);

    /// <summary>
    /// Go <c>ReadCombatPawns</c>: retains optional detailed facts and issues for exact ids. A complete query is not
    /// proof that absent health, gear or capability is known.
    /// </summary>
    internal Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadCombatPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(Combat: true), cancellationToken);

    /// <summary>
    /// Go <c>ReadRoundsPawns</c>: combat, work, care, schedule and social detail: the shared routine census feeds every
    /// routine planner from this one read.
    /// </summary>
    internal Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadRoundsPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, RoundsPawnDetails, cancellationToken);

    /// <summary>
    /// Go <c>ReadTendPawns</c>: combat facts, work settings and care policy for exact doctor and patient ids, plus the
    /// tend detail: the doctor-side native gates and reachability between the requested pawns.
    /// </summary>
    internal Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadTendPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(Combat: true, Work: true, Care: true, Tend: true), cancellationToken);

    private static PawnDetailSelection RoundsPawnDetails { get; } = new(Combat: true, Work: true, Care: true, Schedule: true, Social: true);

    /// <summary>Go <c>ValidateRoundsPawnSnapshot</c>: a pawn snapshot read with the rounds detail, against the ids asked for.</summary>
    internal static void ValidateRoundsPawnSnapshot(PbObservations.PawnSnapshot? snapshot, PbCommon.Identity? id, IReadOnlyList<string> ids) =>
        PawnReadChecks.ValidateDetailedPawnSnapshot(snapshot, id, ids, RoundsPawnDetails);

    /// <summary>Go <c>readPawnDetails</c>.</summary>
    internal async Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadPawnDetailsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, PawnDetailSelection want, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw Contract("pawn IDs outside 1..256");
        }
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var copied = ids.ToArray();
        foreach (var id in copied)
        {
            ValidId(id);
            if (!requested.Add(id))
            {
                throw Contract("duplicate requested pawn");
            }
        }

        // Each PawnSettings slice has its own wire flag: Settings gates only native's CarePolicy() (medical_care,
        // self_tend), Work its priorities and Schedule the 24 TimetableSlot rows. Requesting schedule must never also
        // set Settings -- doing so live-fires CarePolicy and returns MedicalCare/SelfTend the caller never asked for,
        // which the settings validator correctly refuses as unrequested detail.
        var request = PawnDetailsRequest(identity, copied, want);
        var reply = await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Observed is not { } observed)
        {
            throw Contract("pawn read outcome missing");
        }
        PawnReadChecks.PawnsSnapshotSelected(observed, identity, requested, want);
        return (reply, NoReceipt);
    }

    /// <summary>
    /// Go <c>pawnDetailsRequest</c>: the exact request <see cref="ReadPawnDetailsAsync"/> issues for
    /// <paramref name="ids"/> (validated, in the caller's order); the bundle seeds its pawn table's colonist rows under
    /// the routine form of it.
    /// </summary>
    internal static PbObservations.ListPawnsRequest PawnDetailsRequest(PbCommon.Identity identity, IReadOnlyList<string> ids, PawnDetailSelection want)
    {
        var details = new PbObservations.PawnDetails
        {
            Needs = false,
            Health = want.Combat,
            Equipment = want.Combat,
            Biography = want.Combat,
            Settings = want.Care,
            Social = want.Social,
            Animals = want.Combat,
        };
        if (want.Work)
        {
            details = details with { Work = true, Needs = true };
        }
        if (want.Schedule)
        {
            details = details with { Schedule = true };
        }
        if (want.Tend)
        {
            details = details with { Tend = true, TendReachBudgetMs = PawnsTendReachBudgetMs };
        }
        return new PbObservations.ListPawnsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.PawnFilter { Ids = [.. ids], IncludeDead = true },
            Details = details,
        };
    }

    /// <summary>
    /// Go <c>ReadHomeColonists</c>: lists every current-map living colonist's identity and Doctor work-type
    /// enablement: who is home. Unlike the id-scoped reads this one is not: the family must first learn who stays home
    /// before it can ask about their facts.
    /// </summary>
    internal async Task<(PbObservations.ListPawnsReply Value, BridgeResult Receipt)> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);

        // Native defaults every unset detail family to requested, and the home-colonist check refuses any family
        // beyond work and needs, so the rest are declined explicitly (the same shape PawnDetailsRequest sends).
        var request = new PbObservations.ListPawnsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.PawnFilter { Colonist = true, IncludeDead = false },
            Details = new PbObservations.PawnDetails
            {
                Work = true,
                Needs = true,
                Health = false,
                Equipment = false,
                Biography = false,
                Settings = false,
                Social = false,
                Animals = false,
                Schedule = false,
            },
        };
        var reply = await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Observed is not { } observed)
        {
            throw Contract("home colonist read outcome missing");
        }
        HomeColonistsSelected(observed, identity);
        return (reply, NoReceipt);
    }

    /// <summary>Go <c>homeColonistsSelected</c>: the roster names itself as the requested set.</summary>
    private static void HomeColonistsSelected(PbObservations.PawnSnapshot? v, PbCommon.Identity id)
    {
        if (v is null)
        {
            throw Contract("home colonist snapshot missing");
        }
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Pawns)
        {
            if (row is null || row.Pawn is null)
            {
                throw Contract("home colonist pawn missing");
            }
            requested.Add(row.Pawn.Id ?? "");
        }
        PawnReadChecks.PawnsSnapshotSelected(v, id, requested, new PawnDetailSelection(Work: true));
    }

    /// <summary>
    /// Go <c>ReadMechs</c>: reads the mechs the ids name (a mechanitor's controlled mechs) as the mech planners' input
    /// rows. No ids reads nothing. A row that is no mech, or carries no kind or mech block, is a contract failure,
    /// never skipped.
    /// </summary>
    internal async Task<IReadOnlyList<PolicyMechInput>> ReadMechsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var (reply, _) = await ReadPawnsAsync(identity, ids, cancellationToken).ConfigureAwait(false);
        return MechInputs(reply.Outcome?.Observed);
    }

    /// <summary>Go <c>MechInputs</c>: lifts mech pawn rows into planner input.</summary>
    internal static IReadOnlyList<PolicyMechInput> MechInputs(PbObservations.PawnSnapshot? snapshot)
    {
        var result = new List<PolicyMechInput>();
        foreach (var row in snapshot?.Pawns ?? [])
        {
            var id = row.Pawn?.Id ?? "";
            if (!row.Mechanoid.GetValueOrDefault() || row.KindDefName is null)
            {
                throw Contract($"mech {id}: not a mechanoid with a kind");
            }
            if (!PawnBiotech(row.Biotech).TryGetValue(out var biotech) || !biotech.Mech.TryGetValue(out var mech) || mech is null)
            {
                throw Contract($"mech {id}: no Biotech mech block");
            }
            result.Add(new PolicyMechInput
            {
                Id = new PolicyPawnId(id),
                Kind = row.KindDefName,
                Mech = mech,
                Downed = row.Downed.GetValueOrDefault(),
                Drafted = row.Drafted.GetValueOrDefault(),
            });
        }
        return result;
    }
}

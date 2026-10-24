using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using PolicyMechInput = RimGovernor.Brain.Policy.MechInput;

namespace RimGovernor.Brain.BuildingRuntime;

// The pawn-list reads. Each differs only in which PawnDetails families it asks for.
public sealed partial class FacadeNativeClient
{
    /// <summary>The main-thread milliseconds the tend pair reachability may spend.</summary>
    internal const uint PawnsTendReachBudgetMs = 50;

    /// <summary>
    /// Observes exact ids including dead pawns. Missing rows, CAS tokens and drafted state never
    /// imply death, write permission or controller ownership.
    /// </summary>
    internal Task<PbObservations.PawnSnapshot> ReadPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(), cancellationToken);

    /// <summary>
    /// Retains optional detailed facts and issues for exact ids. A complete query is not
    /// proof that absent health, gear or capability is known.
    /// </summary>
    internal Task<PbObservations.PawnSnapshot> ReadCombatPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(Combat: true), cancellationToken);

    /// <summary>
    /// Combat facts, work settings and care policy for exact doctor and patient ids, plus the
    /// tend detail: the doctor-side native gates and reachability between the requested pawns.
    /// </summary>
    internal Task<PbObservations.PawnSnapshot> ReadTendPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadPawnDetailsAsync(identity, ids, new PawnDetailSelection(Combat: true, Work: true, Care: true, Tend: true), cancellationToken);

    internal async Task<PbObservations.PawnSnapshot> ReadPawnDetailsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, PawnDetailSelection want, CancellationToken cancellationToken)
    {
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw BridgeContractException.Contract("pawn IDs outside 1..256");
        }
        var copied = ids.ToArray();

        var request = PawnDetailsRequest(identity, copied, want);
        return await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The exact request <see cref="ReadPawnDetailsAsync"/> issues for
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
            Animals = want.Combat,
        };
        if (want.Work)
        {
            details = details with { Work = true, Needs = true };
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
    /// Lists every current-map living colonist's identity and Doctor work-type
    /// enablement: who is home. Unlike the id-scoped reads this one is not: the family must first learn who stays home
    /// before it can ask about their facts.
    /// </summary>
    internal async Task<PbObservations.PawnSnapshot> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {

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
        return await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the mechs the ids name (a mechanitor's controlled mechs) as the mech planners' input
    /// rows. No ids reads nothing. A row that is no mech, or carries no kind or mech block, is a contract failure,
    /// never skipped.
    /// </summary>
    internal async Task<IReadOnlyList<PolicyMechInput>> ReadMechsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var reply = await ReadPawnsAsync(identity, ids, cancellationToken).ConfigureAwait(false);
        return MechInputs(reply);
    }

    /// <summary>Lifts mech pawn rows into planner input.</summary>
    internal static IReadOnlyList<PolicyMechInput> MechInputs(PbObservations.PawnSnapshot? snapshot)
    {
        var result = new List<PolicyMechInput>();
        foreach (var row in snapshot?.Pawns ?? [])
        {
            var id = row.Pawn.Id ?? "";
            if (!row.Mechanoid)
            {
                throw BridgeContractException.Contract($"mech {id}: not a mechanoid");
            }
            if (!BiotechFactsRules.PawnBiotech(row.Biotech).TryGetValue(out var biotech) || !biotech.Mech.TryGetValue(out var mech) || mech is null)
            {
                throw BridgeContractException.Contract($"mech {id}: no Biotech mech block");
            }
            result.Add(new PolicyMechInput
            {
                Id = new PawnId(id),
                Kind = row.KindDefName,
                Mech = mech,
                Downed = row.Downed,
                Drafted = row.Drafted,
            });
        }
        return result;
    }
}

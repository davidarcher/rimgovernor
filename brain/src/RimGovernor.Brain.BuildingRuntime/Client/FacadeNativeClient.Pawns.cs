using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using PolicyMechInput = RimGovernor.Brain.Policy.MechInput;

namespace RimGovernor.Brain.BuildingRuntime;

// The pawn-list reads. Every row is whole; the tend table is its own read.
public sealed partial class FacadeNativeClient
{
    /// <summary>The main-thread milliseconds the tend pair reachability may spend.</summary>
    internal const uint PawnsTendReachBudgetMs = 50;

    /// <summary>
    /// Observes exact ids including dead pawns, each row whole. Missing rows, CAS tokens and drafted state never
    /// imply death, write permission or controller ownership.
    /// </summary>
    internal async Task<PbObservations.PawnSnapshot> ReadPawnsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw BridgeContractException.Contract("pawn IDs outside 1..256");
        }
        var request = new PbObservations.ListPawnsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.PawnFilter { Ids = [.. ids], IncludeDead = true },
        };
        return await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tend table for exact doctor and patient ids: the doctor-side native gates and reachability between the
    /// requested pawns, within <see cref="PawnsTendReachBudgetMs"/>.
    /// </summary>
    internal async Task<PbObservations.TendDoctorSnapshot> ReadTendDoctorsAsync(PbCommon.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw BridgeContractException.Contract("pawn IDs outside 1..256");
        }
        var request = new PbObservations.TendDoctorsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            PawnIds = [.. ids],
            ReachBudgetMs = PawnsTendReachBudgetMs,
        };
        return await Facade.ReadTendDoctorsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every current-map living colonist: who is home. Unlike the id-scoped reads this one is not: the family
    /// must first learn who stays home before it can ask about their facts.
    /// </summary>
    internal async Task<PbObservations.PawnSnapshot> ReadHomeColonistsAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ListPawnsRequest
        {
            Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
            Filter = new PbObservations.PawnFilter { Colonist = true, IncludeDead = false },
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
    internal static IReadOnlyList<PolicyMechInput> MechInputs(PbObservations.PawnSnapshot snapshot)
    {
        var result = new List<PolicyMechInput>();
        foreach (var row in snapshot.Pawns)
        {
            var id = row.Pawn.Id ?? "";
            if (!row.Mechanoid)
            {
                throw BridgeContractException.Contract($"mech {id}: not a mechanoid");
            }
            if (row.Biotech is not PbObservations.PawnBiotechState.Active { Facts: var block } || RimGovernor.Brain.Policy.PawnBiotech.Of(block).Mech is not { } mech)
            {
                throw BridgeContractException.Contract($"mech {id}: no Biotech mech block");
            }
            result.Add(new PolicyMechInput
            {
                Id = new PawnId(id),
                Kind = row.KindDefName,
                Mech = mech,
                Place = row.Pawn.Place,
                Downed = row.Downed,
                Drafted = row.Drafted,
            });
        }
        return result;
    }
}

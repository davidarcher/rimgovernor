using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using PolicyMechInput = RimGovernor.Brain.Policy.MechInput;

namespace RimGovernor.Brain.Runtime;

// The pawn-list reads. Every row is whole; the tend table is its own read.
public sealed partial class GameClient
{
    /// <summary>The main-thread milliseconds the tend pair reachability may spend.</summary>
    internal const uint PawnsTendReachBudgetMs = 50;

    /// <summary>
    /// Observes exact ids including dead pawns, each row whole. Missing rows, CAS tokens and drafted state never
    /// imply death, write permission or controller ownership.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ReadPawnsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw BridgeContractException.Contract("pawn IDs outside 1..256");
        }
        var request = new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Ids = [.. ids], IncludeDead = true },
        };
        return await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tend table for exact doctor and patient ids: the doctor-side native gates and reachability between the
    /// requested pawns, within <see cref="PawnsTendReachBudgetMs"/>.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.TendDoctorSnapshot> ReadTendDoctorsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 1 || ids.Count > 256)
        {
            throw BridgeContractException.Contract("pawn IDs outside 1..256");
        }
        var request = new RimGovernor.Brain.GameOrders.Observations.TendDoctorsRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            PawnIds = [.. ids],
            ReachBudgetMs = PawnsTendReachBudgetMs,
        };
        return await Facade.ReadTendDoctorsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every current-map living colonist: who is home. Unlike the id-scoped reads this one is not: the family
    /// must first learn who stays home before it can ask about their facts.
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> ReadHomeColonistsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListPawnsRequest
        {
            Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
            Filter = new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Colonist = RimGovernor.Brain.GameFacts.Observations.PawnMembership.Only, IncludeDead = false },
        };
        return await Facade.ListPawnsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the mechs the ids name (a mechanitor's controlled mechs) as the mech planners' input
    /// rows. No ids reads nothing. A row that is no mech, or carries no kind or mech block, is a contract failure,
    /// never skipped.
    /// </summary>
    internal async Task<IReadOnlyList<PolicyMechInput>> ReadMechsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var reply = await ReadPawnsAsync(identity, ids, cancellationToken).ConfigureAwait(false);
        return MechInputs(reply);
    }

    /// <summary>Lifts mech pawn rows into planner input.</summary>
    internal static IReadOnlyList<PolicyMechInput> MechInputs(RimGovernor.Brain.GameFacts.Observations.PawnSnapshot snapshot)
    {
        var result = new List<PolicyMechInput>();
        foreach (var row in snapshot.Pawns)
        {
            var id = row.Pawn.Id ?? "";
            if (!row.Mechanoid)
            {
                throw BridgeContractException.Contract($"mech {id}: not a mechanoid");
            }
            if (row.Biotech is not RimGovernor.Brain.GameFacts.Observations.PawnBiotechState.Active { Facts: var block } || block.Mech is null)
            {
                throw BridgeContractException.Contract($"mech {id}: no Biotech mech block");
            }
            result.Add(new PolicyMechInput { Observed = row });
        }
        return result;
    }
}

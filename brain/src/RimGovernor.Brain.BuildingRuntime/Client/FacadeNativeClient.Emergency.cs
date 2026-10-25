using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using PolicyEmergencyFacts = RimGovernor.Brain.Policy.EmergencyFacts;
using PolicyEmergencyPawn = RimGovernor.Brain.Policy.EmergencyPawn;
using PolicyEmergencyThreat = RimGovernor.Brain.Policy.EmergencyThreat;
using PolicyThreatKind = RimGovernor.Brain.Policy.ThreatKind;

namespace RimGovernor.Brain.BuildingRuntime;

// PawnMentalState and ClassifyThreat (which only the emergency decoder uses).
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Observes basic health and the full status threat census, its pawn references joined
    /// against the pawn table. It does not bind a controller direction or plan or authorize treatment, combat or building.
    /// </summary>
    /// <param name="identity">The world the read is for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async Task<Bridge.Contracts.EmergencyObservation> ReadEmergencyAsync(
        PbCommon.Identity identity,
        CancellationToken cancellationToken)
    {
        var observed = await Facade.ReadStatusAsync(EmergencyRequest(identity), cancellationToken).ConfigureAwait(false);
        var pawns = await FramePawnsAsync(identity, cancellationToken).ConfigureAwait(false);
        return EmergencyDecode.DecodeEmergencyStatus(observed, pawns);
    }

    /// <summary>The status read <see cref="ReadEmergencyAsync"/> issues.</summary>
    internal static PbObservations.StatusRequest EmergencyRequest(PbCommon.Identity id) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = id } };

    /// <summary>The emergency status decoder and the threat classifier it uses.</summary>
    internal static class EmergencyDecode
    {
        /// <summary>The Chebyshev radius around a colonist within which an unowned downed pawn or predator is a threat.</summary>
        internal const int ThreatProximityRadius = 30;

        /// <summary>
        /// Shared by live reads and captured replay. Each pawn reference resolves
        /// against <paramref name="pawns"/>, the frame's pawn table; one the table has no map row for is Unseen, held
        /// as stale until a later frame carries it. The facts carry no player authority or controller plan identity.
        /// </summary>
        internal static Bridge.Contracts.EmergencyObservation DecodeEmergencyStatus(PbObservations.StatusSnapshot v, Pawns pawns)
        {
            var unseen = new List<PawnId>();
            var colonists = new List<PolicyEmergencyPawn>();
            foreach (var reference in v.Colonists)
            {
                if (EmergencyPawn(reference, pawns, unseen) is { } pawn)
                {
                    colonists.Add(pawn);
                }
                else
                {
                    throw BridgeContractException.Contract($"colonist {reference.Id}: the pawn table holds no map row" );
                }
            }
            var patients = new List<Policy.EmergencyDependentPatient>();
            foreach (var patient in v.DependentPatients.Patients)
            {
                if (EmergencyPawn(patient.Pawn, pawns, unseen) is { } pawn)
                {
                    patients.Add(new(pawn, patient.Group, patient.Rescue, patient.Tend));
                }
            }
            // The native's threat fact rows are classified; the threats are listed kind by kind in ThreatKind order,
            // each kind in row order.
            var byKind = new Dictionary<PolicyThreatKind, List<PolicyEmergencyThreat>>();
            foreach (var row in v.Threats.Pawns)
            {
                var kinds = ClassifyThreat(row);
                if (kinds.Count == 0 || EmergencyPawn(row.Pawn, pawns, unseen) is not { } seen)
                {
                    continue;
                }
                var state = seen.Observed;
                foreach (var kind in kinds)
                {
                    var threat = new PolicyEmergencyThreat.PawnThreat(state, row, kind);
                    if (!byKind.TryGetValue(kind, out var list))
                    {
                        byKind[kind] = list = [];
                    }
                    list.Add(threat);
                }
            }
            var threats = new List<PolicyEmergencyThreat>();
            foreach (var kind in new[] { PolicyThreatKind.Hostile, PolicyThreatKind.HuntingPredator, PolicyThreatKind.IgnoredHunter, PolicyThreatKind.NearbyPredator, PolicyThreatKind.NearbyDowned })
            {
                if (byKind.TryGetValue(kind, out var list))
                {
                    threats.AddRange(list);
                }
            }
            foreach (var row in v.Threats.HostileBuildings)
            {
                threats.Add(EmergencyBuilding(row));
            }
            return new Bridge.Contracts.EmergencyObservation(
                v.Context,
                new PolicyEmergencyFacts { Colonists = colonists, DependentPatients = patients, Threats = threats, Unseen = unseen.Distinct().ToList() });
        }

        /// <summary>
        /// Reads one census reference's facts from its table row (returned too); a reference without a map row is
        /// listed in <paramref name="unseen"/> and yields null.
        /// </summary>
        private static PolicyEmergencyPawn? EmergencyPawn(PbCommon.Ref reference, Pawns pawns, List<PawnId> unseen)
        {
            if (!pawns.TryRow(reference, out var row) || row.Pawn.Place is not O.EntityPlace.OnMap)
            {
                unseen.Add(new PawnId(reference.Id ?? ""));
                return null;
            }
            return new PolicyEmergencyPawn { Observed = row };
        }

        /// <summary>
        /// Reads one hostile-building row. A spawned building the census lists is standing
        /// (not dead) and never downed; the attack order sends its CAS token back as the target precondition and its
        /// occupied rect is the ranged target.
        /// </summary>
        private static PolicyEmergencyThreat.BuildingThreat EmergencyBuilding(PbObservations.ThreatBuilding row)
        {
            if (row.Building.Place is not O.EntityPlace.OnMap)
            {
                throw new BridgeContractException($"status hostile building {row.Building.Id} is unplaced");
            }
            return new PolicyEmergencyThreat.BuildingThreat(row);
        }

        /// <summary>
        /// The kinds a threat row is. A manhunter, a faction-hostile pawn or a prison breaker
        /// is Hostile; a predator hunt is an ignored hunter when the hunter is ours or hunts prey that is not ours,
        /// else a hunting predator; else an unowned pawn within <see cref="ThreatProximityRadius"/> of a colonist is
        /// nearby downed and/or nearby predator.
        /// </summary>
        internal static IReadOnlyList<PolicyThreatKind> ClassifyThreat(PbObservations.ThreatPawn row)
        {
            if ((row.MentalState ?? "").Contains("manhunter", StringComparison.OrdinalIgnoreCase) || row.FactionHostile || row.PrisonBreak)
            {
                return [PolicyThreatKind.Hostile];
            }
            if (row.PredatorHunt)
            {
                return row.Ours || row.Prey is { Ours: false } ? [PolicyThreatKind.IgnoredHunter] : [PolicyThreatKind.HuntingPredator];
            }
            if (row.Ours || row.NearestColonist is not PbObservations.ColonistDistance.Nearest { Cells: <= ThreatProximityRadius })
            {
                return [];
            }
            var kinds = new List<PolicyThreatKind>();
            if (row.Downed)
            {
                kinds.Add(PolicyThreatKind.NearbyDowned);
            }
            if (row.Predator)
            {
                kinds.Add(PolicyThreatKind.NearbyPredator);
            }
            return kinds;
        }
    }
}

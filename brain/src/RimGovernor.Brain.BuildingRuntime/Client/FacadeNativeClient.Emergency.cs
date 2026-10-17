using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using PolicyEmergencyFacts = RimGovernor.Brain.Policy.EmergencyFacts;
using PolicyEmergencyPawn = RimGovernor.Brain.Policy.EmergencyPawn;
using PolicyEmergencyThreat = RimGovernor.Brain.Policy.EmergencyThreat;
using PolicyMentalState = RimGovernor.Brain.Policy.MentalState;
using PolicyThreatKind = RimGovernor.Brain.Policy.ThreatKind;

namespace RimGovernor.Brain.BuildingRuntime;

// Go emergency.go (ReadEmergency, DecodeEmergencyStatus and the row decoders), mental_state.go (PawnMentalState) and
// threat_classify.go (ClassifyThreat, which only the emergency decoder uses).
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>ReadEmergency</c>: observes basic health and the full status threat census, its pawn references joined
    /// against the pawn table. It does not bind a controller direction or plan or authorize treatment, combat or building.
    /// </summary>
    /// <param name="identity">The world the read is for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async Task<Bridge.Contracts.EmergencyObservation> ReadEmergencyAsync(
        PbCommon.Identity identity,
        CancellationToken cancellationToken)
    {
        var reply = await Facade.ReadStatusAsync(EmergencyRequest(identity), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var observed = reply.Outcome.Observed!;
        var pawns = await FramePawnsAsync(identity, cancellationToken).ConfigureAwait(false);
        return EmergencyDecode.DecodeEmergencyStatus(observed, pawns);
    }

    /// <summary>Go <c>emergencyRequest</c>: the status read <see cref="ReadEmergencyAsync"/> issues; a bundle's emergency section is seeded into the step cache under the same request.</summary>
    internal static PbObservations.StatusRequest EmergencyRequest(PbCommon.Identity id) =>
        new() { Scope = new PbObservations.ReadScope { ExpectedIdentity = id }, Colonists = true, Threats = true };

    /// <summary>The emergency status decoder and the threat classifier it uses.</summary>
    internal static class EmergencyDecode
    {
        /// <summary>Go <c>ThreatProximityRadius</c>: the Chebyshev radius around a colonist within which an unowned downed pawn or predator is a threat.</summary>
        internal const double ThreatProximityRadius = 30.0;

        /// <summary>
        /// Go <c>DecodeEmergencyStatus</c>: shared by live reads and captured replay. Each pawn reference resolves
        /// against <paramref name="pawns"/>, the frame's pawn table; one the table lacks leaves that pawn's facts
        /// unknown until a later frame. The facts carry no player authority or controller plan identity.
        /// </summary>
        internal static Bridge.Contracts.EmergencyObservation DecodeEmergencyStatus(PbObservations.StatusSnapshot v, Pawns pawns) =>
            EmergencyStatus(v, pawns);

        internal static Fact<bool> EmergencyBool(bool? v) => v is { } b ? Known(b) : Unknown<bool>();

        /// <summary>Go <c>emergencyPawn</c>: reads one census reference's facts from its table row (returned too); an unresolved reference has only its id known.</summary>
        private static (PolicyEmergencyPawn Pawn, PbObservations.PawnState? State) EmergencyPawn(PbCommon.Ref? reference, Pawns pawns)
        {
            if (!pawns.TryRow(reference!, out var row))
            {
                return (new PolicyEmergencyPawn { Id = new PawnId(reference!.Id ?? "") }, null);
            }
            return (EmergencyFacts(row), row);
        }

        /// <summary>Go <c>emergencyFacts</c>.</summary>
        private static PolicyEmergencyPawn EmergencyFacts(PbObservations.PawnState row)
        {
            var result = new PolicyEmergencyPawn
            {
                Id = new PawnId(row.Pawn.Id ?? ""),
                Dead = Known(row.Dead),
                Downed = Known(row.Downed),
                InBed = Known(row.InBed),
                // A known value is an active state; no mental state reads as unknown to the policy.
                MentalState = row.MentalState is { } m ? new PolicyMentalState { DefName = m.DefName, IsAggro = m.IsAggro, TicksInState = m.Ticks } : null,
            };
            if (row.Pawn.Position is { } at)
            {
                result = result with { Position = Known(new DomainCell(at.X, at.Z)) };
            }
            if (row.Health is { } h)
            {
                result = result with { Bleeding = Known(h.Bleeding), NeedsTend = Known(h.NeedsTend) };
            }
            return result;
        }

        /// <summary>Go <c>emergencyStatus</c>.</summary>
        private static Bridge.Contracts.EmergencyObservation EmergencyStatus(PbObservations.StatusSnapshot v, Pawns pawns)
        {
            var context = v.Context!;
            // Both sections are requested. The colonist census is complete unless an issue names it, and then it lists no one.
            var threatCensus = v.Threats!;
            var complete = Known(true);
            foreach (var issue in v.Issues)
            {
                if ((issue.Field ?? "") == "colonists")
                {
                    complete = Unknown<bool>();
                }
            }
            var colonists = new List<PolicyEmergencyPawn>();
            var threats = new List<PolicyEmergencyThreat>();
            var statuses = new Dictionary<PawnId, PolicyEmergencyPawn>();

            PolicyEmergencyPawn Observe(PolicyEmergencyPawn pawn)
            {
                if (statuses.TryGetValue(pawn.Id, out var prior))
                {
                    if (prior.Dead.IsKnown)
                    {
                        pawn = pawn with { Dead = prior.Dead };
                    }
                    if (prior.Downed.IsKnown)
                    {
                        pawn = pawn with { Downed = prior.Downed };
                    }
                }
                statuses[pawn.Id] = pawn;
                return pawn;
            }

            foreach (var reference in v.Colonists)
            {
                var (pawn, _) = EmergencyPawn(reference, pawns);
                Observe(pawn);
                colonists.Add(pawn);
            }

            Fact<EquatableArray<Policy.EmergencyDependentPatient>> dependentPatients = default;
            if (v.DependentPatients is { } census)
            {
                var patients = new List<Policy.EmergencyDependentPatient>();
                foreach (var reference in census.Patients)
                {
                    var (pawn, _) = EmergencyPawn(reference.Pawn, pawns);
                    Observe(pawn);
                    patients.Add(new(pawn, reference.Group.GetValueOrDefault(), EmergencyBool(reference.Rescue), EmergencyBool(reference.Tend)));
                }
                dependentPatients = Known(EquatableArray.From(patients));
            }
            // Go classifies the native's threat fact rows; the threats are listed kind by kind in ThreatKind order,
            // each kind in row order.
            var byKind = new Dictionary<PolicyThreatKind, List<PolicyEmergencyThreat>>();
            foreach (var row in threatCensus.Pawns)
            {
                var kinds = ClassifyThreat(row);
                if (kinds.Count == 0)
                {
                    continue;
                }
                var (pawn, state) = EmergencyPawn(row.Pawn, pawns);
                Observe(pawn);
                foreach (var kind in kinds)
                {
                    var threat = new PolicyEmergencyThreat { Id = pawn.Id, Kind = kind, Dead = pawn.Dead, Downed = pawn.Downed };
                    if (kind == PolicyThreatKind.Hostile)
                    {
                        threat = threat with { Passive = EmergencyBool(row.Passive) };
                    }
                    if (row.NearestColonistDistance is not null)
                    {
                        threat = threat with { Distance = Known(row.NearestColonistDistance.GetValueOrDefault()) };
                    }
                    if (state is not null)
                    {
                        threat = threat with { Animal = Known(state.Animal), Fogged = Known(state.Fogged) };
                        if (state.Pawn.Position is { } at)
                        {
                            threat = threat with { Position = Known(new DomainCell(at.X, at.Z)) };
                        }
                    }
                    if (!byKind.TryGetValue(kind, out var list))
                    {
                        byKind[kind] = list = [];
                    }
                    list.Add(threat);
                }
            }
            foreach (var kind in new[] { PolicyThreatKind.Hostile, PolicyThreatKind.HuntingPredator, PolicyThreatKind.IgnoredHunter, PolicyThreatKind.NearbyPredator, PolicyThreatKind.NearbyDowned })
            {
                if (byKind.TryGetValue(kind, out var list))
                {
                    threats.AddRange(list);
                }
            }
            foreach (var row in threatCensus.HostileBuildings)
            {
                threats.Add(EmergencyBuilding(row));
            }
            return new Bridge.Contracts.EmergencyObservation(context, new PolicyEmergencyFacts { ColonistsComplete = complete, Colonists = colonists, DependentPatients = dependentPatients, Threats = threats });
        }

        /// <summary>
        /// Go <c>emergencyBuilding</c>: reads one hostile-building row. A spawned building the census lists is standing
        /// (not dead) and never downed; the attack order sends its CAS token back as the target precondition and its
        /// occupied rect is the ranged target.
        /// </summary>
        private static PolicyEmergencyThreat EmergencyBuilding(PbObservations.ThreatBuilding row)
        {
            var result = new PolicyEmergencyThreat
            {
                Id = new PawnId(row.Building.Id),
                Kind = PolicyThreatKind.HostileBuilding,
                Dead = Known(false),
                Downed = Known(false),
                Animal = Known(false),
                SnapshotToken = row.BuildingSnapshot.Token ?? "",
                Definition = row.Building.DefName,
                Cells = RectCells(row.Occupied),
                Passive = Known(row.Passive),
            };
            if (row.NearestColonistDistance is not null)
            {
                result = result with { Distance = Known((double)row.NearestColonistDistance.GetValueOrDefault()) };
            }
            return result;
        }

        /// <summary>
        /// Go <c>ClassifyThreat</c>: the kinds a threat row is. A manhunter, a faction-hostile pawn or a prison breaker
        /// is Hostile; a predator hunt is a hunting predator or, when the hunter or its prey is ours, an ignored hunter;
        /// else an unowned pawn within <see cref="ThreatProximityRadius"/> of a colonist is nearby downed and/or nearby predator.
        /// </summary>
        internal static IReadOnlyList<PolicyThreatKind> ClassifyThreat(PbObservations.ThreatPawn row)
        {
            if ((row.MentalState ?? "").Contains("manhunter", StringComparison.OrdinalIgnoreCase) || row.FactionHostile.GetValueOrDefault() || row.PrisonBreak.GetValueOrDefault())
            {
                return [PolicyThreatKind.Hostile];
            }
            if (row.PredatorHunt.GetValueOrDefault())
            {
                if (row.Ours.GetValueOrDefault() || (row.Prey is not null && !row.PreyIsOurs.GetValueOrDefault()))
                {
                    return [PolicyThreatKind.IgnoredHunter];
                }
                return [PolicyThreatKind.HuntingPredator];
            }
            if (row.Ours.GetValueOrDefault() || row.NearestColonistDistance is null || row.NearestColonistDistance.GetValueOrDefault() > ThreatProximityRadius)
            {
                return [];
            }
            var kinds = new List<PolicyThreatKind>();
            if (row.Downed.GetValueOrDefault())
            {
                kinds.Add(PolicyThreatKind.NearbyDowned);
            }
            if (row.Predator.GetValueOrDefault())
            {
                kinds.Add(PolicyThreatKind.NearbyPredator);
            }
            return kinds;
        }
    }
}

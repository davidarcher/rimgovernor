using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using PolicyEmergencyFacts = RimGovernor.Brain.Policy.EmergencyFacts;
using PolicyEmergencyPawn = RimGovernor.Brain.Policy.EmergencyPawn;
using PolicyEmergencyThreat = RimGovernor.Brain.Policy.EmergencyThreat;
using PolicyPawnId = RimGovernor.Brain.Policy.PawnId;
using PolicyMentalState = RimGovernor.Brain.Policy.MentalState;
using PolicyThreatKind = RimGovernor.Brain.Policy.ThreatKind;

namespace RimGovernor.Brain.Host;

// Go emergency.go (ReadEmergency, DecodeEmergencyStatus and the row decoders), mental_state.go (PawnMentalState) and
// threat_classify.go (ClassifyThreat, which only the emergency decoder uses).
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>ReadEmergency</c>: observes basic health and the full status threat census, its pawn references joined
    /// against the pawn table. It does not bind a controller direction or plan or authorize treatment, combat or building.
    /// </summary>
    /// <param name="identity">The world the read is for.</param>
    /// <param name="framePawns">
    /// Go <c>client.FramePawns</c>: the frame's pawn table for <paramref name="identity"/>, read only once the status read
    /// observed something. The frame reads belong to another part of the client, so the caller binds them here.
    /// </param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal async Task<(Bridge.Contracts.EmergencyObservation Value, BridgeResult Receipt)> ReadEmergencyAsync(
        PbCommon.Identity identity,
        Func<PbCommon.Identity, CancellationToken, Task<Pawns>> framePawns,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var reply = await Facade.ReadStatusAsync(EmergencyRequest(identity), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Observed is not { } observed)
        {
            throw Contract("emergency status outcome missing");
        }
        var pawns = await framePawns(identity, cancellationToken).ConfigureAwait(false);
        return (EmergencyDecode.DecodeEmergencyStatus(observed, pawns, identity), NoReceipt);
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
        /// Go <c>DecodeEmergencyStatus</c>: shares live boundary validation with captured replay. Each pawn reference
        /// resolves against <paramref name="pawns"/>, the frame's pawn table; one the table lacks leaves that pawn's facts
        /// unknown until a later frame. The facts carry no player authority or controller plan identity.
        /// </summary>
        internal static Bridge.Contracts.EmergencyObservation DecodeEmergencyStatus(PbObservations.StatusSnapshot? v, Pawns pawns, PbCommon.Identity id)
        {
            ValidateIdentity(id);
            if (v is null)
            {
                throw Contract("emergency status missing");
            }
            BuildingUnknown(v);
            return EmergencyStatus(v, pawns, id);
        }

        internal static Fact<bool> EmergencyBool(bool? v) => v is { } b ? Known(b) : Unknown<bool>();

        /// <summary>Go <c>emergencyIssues</c>.</summary>
        internal static void EmergencyIssues(IEnumerable<PbObservations.ReadIssue> issues, Func<string, bool> present)
        {
            foreach (var issue in issues)
            {
                if (issue is null || issue.Field is null || !IsValidId(issue.Field))
                {
                    throw Contract("invalid emergency read issue");
                }
                ValidateUnavailable(issue.Unavailable);
                if (present(issue.Field))
                {
                    throw Contract("emergency field is both present and unavailable");
                }
            }
        }

        /// <summary>
        /// Go <c>PawnMentalState</c>: preserves absence from older producers as unknown. A known value describes an
        /// active state; it never invents a state from mood alone.
        /// </summary>
        internal static Fact<PolicyMentalState> PawnMentalState(PbObservations.PawnState? row)
        {
            var unknown = Unknown<PolicyMentalState>();
            if (row is null)
            {
                return unknown;
            }
            EmergencyIssues(row.Issues, field => field switch
            {
                "mental_state" => row.MentalState is not null,
                "mental_state_is_aggro" => row.MentalStateIsAggro is not null,
                "mental_state_ticks" => row.MentalStateTicks is not null,
                _ => false,
            });
            if (row.MentalState is not null && !IsValidId(row.MentalState))
            {
                throw Contract("invalid mental state defName");
            }
            if (row.MentalStateTicks is not null && row.MentalStateTicks.GetValueOrDefault() < 0)
            {
                throw Contract("negative mental state age");
            }
            if (row.MentalState is null || row.MentalStateIsAggro is null || row.MentalStateTicks is null)
            {
                return unknown;
            }
            return Known(new PolicyMentalState { DefName = row.MentalState, IsAggro = row.MentalStateIsAggro.GetValueOrDefault(), TicksInState = row.MentalStateTicks.GetValueOrDefault() });
        }

        /// <summary>Go <c>emergencyPawn</c>: reads one census reference's facts from its table row (returned too); an unresolved reference has only its id known.</summary>
        private static (PolicyEmergencyPawn Pawn, PbObservations.PawnState? State) EmergencyPawn(PbCommon.Ref? reference, Pawns pawns)
        {
            if (!ValidRef(reference))
            {
                throw Contract("emergency pawn reference malformed");
            }
            if (!pawns.TryRow(reference, out var row))
            {
                return (new PolicyEmergencyPawn { Id = new PolicyPawnId(reference.Id ?? "") }, null);
            }
            return (EmergencyFacts(row), row);
        }

        /// <summary>Go <c>emergencyFacts</c>.</summary>
        private static PolicyEmergencyPawn EmergencyFacts(PbObservations.PawnState row)
        {
            EmergencyIssues(row.Issues, field => field switch
            {
                "dead" => row.Dead is not null,
                "downed" => row.Downed is not null,
                "in_bed" or "inBed" => row.InBed is not null,
                "health" => row.Health is not null,
                _ => false,
            });
            var result = new PolicyEmergencyPawn
            {
                Id = new PolicyPawnId(row.Pawn?.Id ?? ""),
                Dead = EmergencyBool(row.Dead),
                Downed = EmergencyBool(row.Downed),
                InBed = EmergencyBool(row.InBed),
            };
            if (row.Pawn?.Position is { X: not null, Z: not null } at)
            {
                result = result with { Position = Known(new DomainCell(at.X.GetValueOrDefault(), at.Z.GetValueOrDefault())) };
            }
            result = result with { MentalState = PawnMentalState(row) };
            if (row.Health is { } h)
            {
                EmergencyIssues(h.Issues, field => (field.StartsWith("health.", StringComparison.Ordinal) ? field["health.".Length..] : field) switch
                {
                    "bleeding" => h.Bleeding is not null,
                    "needs_tend" or "needsTend" => h.NeedsTend is not null,
                    _ => false,
                });
                result = result with { Bleeding = EmergencyBool(h.Bleeding), NeedsTend = EmergencyBool(h.NeedsTend) };
            }
            return result;
        }

        /// <summary>Go <c>emergencyStatus</c>.</summary>
        private static Bridge.Contracts.EmergencyObservation EmergencyStatus(PbObservations.StatusSnapshot v, Pawns pawns, PbCommon.Identity id)
        {
            ValidateContext(v.Context);
            var context = v.Context!;
            if (!SameIdentity(context.Identity, id))
            {
                throw Contract("emergency identity mismatch");
            }

            // Both sections are requested. The colonist census is complete unless an issue names it, and then it lists no one.
            if (v.Threats is null)
            {
                throw Contract("requested emergency section missing");
            }
            EmergencyIssues(v.Issues, field => (field == "colonists" && v.Colonists.Count > 0) || field == "threats");
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
            var statuses = new Dictionary<PolicyPawnId, PolicyEmergencyPawn>();

            PolicyEmergencyPawn Observe(PolicyEmergencyPawn pawn)
            {
                if (statuses.TryGetValue(pawn.Id, out var prior))
                {
                    foreach (var (a, b) in new[] { (prior.Dead, pawn.Dead), (prior.Downed, pawn.Downed) })
                    {
                        if (a.TryGetValue(out var x) && b.TryGetValue(out var y) && x != y)
                        {
                            throw Contract("conflicting emergency pawn status");
                        }
                    }
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

            var seen = new HashSet<PolicyPawnId>();
            foreach (var reference in v.Colonists)
            {
                var (pawn, _) = EmergencyPawn(reference, pawns);
                if (!seen.Add(pawn.Id))
                {
                    throw Contract("duplicate emergency colonist");
                }
                Observe(pawn);
                colonists.Add(pawn);
            }

            // Go classifies the native's threat fact rows; the threats are listed kind by kind in ThreatKind order,
            // each kind in row order.
            var byKind = new Dictionary<PolicyThreatKind, List<PolicyEmergencyThreat>>();
            seen = [];
            foreach (var row in v.Threats.Pawns)
            {
                if (row is null)
                {
                    throw Contract("missing threat pawn");
                }
                if ((row.NearestColonistDistance is not null && !(row.NearestColonistDistance.GetValueOrDefault() >= 0))
                    || (row.MentalState is not null && !IsValidId(row.MentalState))
                    || !OptionalRef(row.Faction))
                {
                    throw Contract("invalid emergency threat facts");
                }
                var kinds = ClassifyThreat(row);
                if (kinds.Count == 0)
                {
                    continue;
                }
                var (pawn, state) = EmergencyPawn(row.Pawn, pawns);
                if (!seen.Add(pawn.Id))
                {
                    throw Contract("duplicate emergency threat");
                }
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
                        threat = threat with { Animal = EmergencyBool(state.Animal), Fogged = EmergencyBool(state.Fogged) };
                        if (state.Pawn?.Position is { X: not null, Z: not null } at)
                        {
                            threat = threat with { Position = Known(new DomainCell(at.X.GetValueOrDefault(), at.Z.GetValueOrDefault())) };
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
            seen = [];
            foreach (var row in v.Threats.HostileBuildings)
            {
                var threat = EmergencyBuilding(row, context);
                if (seen.Contains(threat.Id) || (statuses.TryGetValue(threat.Id, out var existing) && existing.Id.Value.Length != 0))
                {
                    throw Contract("duplicate emergency hostile building");
                }
                seen.Add(threat.Id);
                threats.Add(threat);
            }
            return new Bridge.Contracts.EmergencyObservation(context, new PolicyEmergencyFacts { ColonistsComplete = complete, Colonists = colonists, Threats = threats });
        }

        /// <summary>
        /// Go <c>emergencyBuilding</c>: reads one hostile-building row. A spawned building the census lists is standing
        /// (not dead) and never downed; its CAS token must be the row's own, scoped to this context, since the attack
        /// order sends it back as the target precondition.
        /// </summary>
        private static PolicyEmergencyThreat EmergencyBuilding(PbObservations.ThreatBuilding? row, PbCommon.ObservationContext context)
        {
            if (row is null || row.Building is null)
            {
                throw Contract("missing threat building");
            }
            PawnReadChecks.PawnsEntity(row.Building, context);
            if (row.BuildingSnapshot is null || row.Building.DefName is null)
            {
                throw Contract("threat building snapshot or definition missing");
            }
            if ((row.HitPoints is not null && row.HitPoints.GetValueOrDefault() < 0) || (row.NearestColonistDistance is not null && row.NearestColonistDistance.GetValueOrDefault() < 0))
            {
                throw Contract("invalid threat building facts");
            }

            // The occupied rect is the ranged target: every cell in bounds, none twice, at least one (a spawned building occupies its position).
            var cells = RectCells(row.Occupied);
            if (cells.Count == 0 || cells[0].X < 0 || cells[0].Z < 0)
            {
                throw Contract("invalid threat building occupied rect");
            }
            var result = new PolicyEmergencyThreat
            {
                Id = new PolicyPawnId(row.Building.Id ?? ""),
                Kind = PolicyThreatKind.HostileBuilding,
                Dead = Known(false),
                Downed = Known(false),
                Animal = Known(false),
                SnapshotToken = row.BuildingSnapshot.Token ?? "",
                Definition = row.Building.DefName,
                Cells = cells,
                Passive = EmergencyBool(row.Passive),
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

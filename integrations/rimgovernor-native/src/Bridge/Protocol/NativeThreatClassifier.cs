#nullable enable

using System;
using System.Collections.Generic;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// The facts the status read's threat classification looks at for one
    /// living non-colonist pawn: cheap field reads only, gathered before any
    /// pawn row, control snapshot or distance scan is paid for (#646).
    internal struct ThreatFacts
    {
        internal bool Ours;
        /// The pawn's mental state def name, or null.
        internal string? Mental;
        /// A non-player faction hostile to the player.
        internal bool FactionHostile;
        internal string? FactionId;
        /// The current job is PredatorHunt.
        internal bool PredatorHunt;
        /// The hunt's target resolves to a pawn (a live pawn or a corpse's).
        internal bool HasPrey;
        internal bool PreyOurs;
        internal bool Downed;
        internal bool Predator;
        internal int X, Z;
    }

    /// Filter-first threat classification (#646): each pawn is classified
    /// from its ThreatFacts, and only a pawn that lands in a threat list gets
    /// its full PawnState projection (entity, job, lord, needs, the pawn
    /// control observation) and its nearest-colonist distance. Healthy
    /// non-predator wildlife costs one facts read and nothing else. A retained
    /// candidate still pays an O(colonists) Chebyshev scan, so the worst case
    /// stays O(pawns * colonists).
    ///
    /// Game-independent so the probes can drive it with counted projectors;
    /// NativeObservationTools.Status supplies the RimWorld adapters.
    internal static class NativeThreatClassifier
    {
        /// What one Collect call did, for the hop's observation account.
        internal struct Scan { internal long Examined, Candidates, Projections, ProximityChecks; }

        internal static Scan Collect<T>(IReadOnlyList<T> pawns, Func<T, ThreatFacts> facts,
            IReadOnlyList<(int X, int Z)> colonists, double radius,
            Func<T, Obs.PawnState> project, Func<T, Obs.EntityRef> prey, Obs.ThreatsSnapshot threats)
        {
            var scan = new Scan();
            foreach (var pawn in pawns) {
                scan.Examined++;
                var f = facts(pawn);
                var manhunter = f.Mental?.IndexOf("Manhunter", StringComparison.OrdinalIgnoreCase) >= 0;
                var hostile = manhunter || f.FactionHostile;
                int? nearest;
                if (hostile || f.PredatorHunt) nearest = Nearest(colonists, f.X, f.Z, ref scan);
                else {
                    // The proximity branch only ever keeps an unowned downed
                    // pawn or predator inside a positive radius; everything
                    // else is discarded without a distance scan.
                    if (radius <= 0 || f.Ours || !f.Downed && !f.Predator) continue;
                    nearest = Nearest(colonists, f.X, f.Z, ref scan);
                    if (!nearest.HasValue || nearest.Value > radius) continue;
                }
                scan.Candidates++;
                scan.Projections++;
                var row = project(pawn);
                if (nearest.HasValue) row.NearestColonistDistance = nearest.Value;
                row.Hostile = hostile;
                var threat = new Obs.ThreatPawn { Pawn = row };
                if (hostile) { row.HostileReason = manhunter ? "manhunter:"+f.Mental : "faction:"+f.FactionId; threats.Hostiles.Add(threat); }
                else if (f.PredatorHunt) {
                    threat.PredatorIsOurs = f.Ours;
                    if (f.HasPrey) { threat.Prey = prey(pawn); threat.PreyIsOurs = f.PreyOurs; }
                    row.HostileReason = "predatorHunt";
                    if (f.Ours || f.HasPrey && !f.PreyOurs) { threat.IgnoredReason = f.Ours ? "player-owned predator" : "prey is not player-owned"; threats.IgnoredHunters.Add(threat); }
                    else threats.HuntingPredators.Add(threat);
                } else {
                    // A downed predator is in both lists: the downed copy is
                    // cloned before the predator reason is written.
                    if (f.Downed) { var downed = threat.Clone(); downed.Pawn.HostileReason = "downed"; threats.DownedNear.Add(downed); }
                    if (f.Predator) { row.HostileReason = "predator_near"; threats.WildPredatorsNear.Add(threat); }
                }
            }
            ObservationWork.ThreatScan(scan.Examined, scan.Candidates, scan.Projections, scan.ProximityChecks);
            return scan;
        }

        /// Chebyshev distance to the nearest colonist, or null with none.
        private static int? Nearest(IReadOnlyList<(int X, int Z)> colonists, int x, int z, ref Scan scan)
        {
            if (colonists.Count == 0) return null;
            scan.ProximityChecks++;
            var best = int.MaxValue;
            foreach (var c in colonists) best = Math.Min(best, Math.Max(Math.Abs(c.X-x), Math.Abs(c.Z-z)));
            return best;
        }
    }
}

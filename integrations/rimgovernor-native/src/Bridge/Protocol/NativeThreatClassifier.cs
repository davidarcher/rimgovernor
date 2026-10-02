#nullable enable

using System;
using System.Collections.Generic;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// The threat facts of one living non-colonist pawn: cheap field reads
    /// only, gathered before any pawn reference or distance scan is paid
    /// for (#646).
    internal struct ThreatFacts
    {
        internal bool Ours;
        /// The pawn's mental state def name, or null.
        internal string? Mental;
        /// A non-player faction hostile to the player.
        internal bool FactionHostile;
        /// A prisoner of the colony breaking out (#1080); a held prisoner
        /// is never FactionHostile.
        internal bool PrisonBreak;
        internal string? FactionId;
        /// The current job is PredatorHunt.
        internal bool PredatorHunt;
        /// The hunt's target resolves to a pawn (a live pawn or a corpse's).
        internal bool HasPrey;
        internal bool PreyOurs;
        internal bool Downed;
        internal bool Predator;
        internal int X, Z;
        /// Insects (#948): dormant, or awake but targeting nothing of the
        /// player's. Other hostiles with CompCanBeDormant (mech clusters,
        /// #1335): asleep. Null for every other pawn.
        internal bool? Passive;
    }

    /// Filter-first threat fact rows (#646, #1356): the native emits facts,
    /// Go classifies them (bridge.ClassifyThreat). A pawn is kept when any
    /// fact a threat rule reads is set (a mental state, faction hostility, a
    /// prison break, a predator hunt), or when it is an unowned downed pawn
    /// or predator within the radius of a colonist; only a kept pawn gets
    /// its pawn table reference (#1343) and nearest-colonist distance.
    /// Healthy non-predator wildlife costs one facts read and nothing else;
    /// the worst case stays O(pawns * colonists).
    ///
    /// Game-independent so the probes can drive it with counted projectors;
    /// NativeObservationTools.Status supplies the RimWorld adapters.
    internal static class NativeThreatClassifier
    {
        /// What one Collect call did, for the hop's observation account.
        internal struct Scan { internal long Examined, Candidates, Projections, ProximityChecks; }

        /// Whether f carries a fact a threat rule reads regardless of
        /// distance; such a pawn also gets combat detail in the pawn table.
        internal static bool Engaged(ThreatFacts f) => f.Mental != null || f.FactionHostile || f.PrisonBreak || f.PredatorHunt;

        internal static Scan Collect<T>(IReadOnlyList<T> pawns, Func<T, ThreatFacts> facts,
            IReadOnlyList<(int X, int Z)> colonists, double radius,
            Func<T, RimGovernor.Protocol.Common.Ref> project, Func<T, Obs.EntityRef> prey, Obs.ThreatsSnapshot threats)
        {
            var scan = new Scan();
            foreach (var pawn in pawns) {
                scan.Examined++;
                var f = facts(pawn);
                int? nearest;
                if (Engaged(f)) nearest = Nearest(colonists, f.X, f.Z, ref scan);
                else {
                    if (radius <= 0 || f.Ours || !f.Downed && !f.Predator) continue;
                    nearest = Nearest(colonists, f.X, f.Z, ref scan);
                    if (!nearest.HasValue || nearest.Value > radius) continue;
                }
                scan.Candidates++;
                scan.Projections++;
                var row = new Obs.ThreatPawn { Pawn = project(pawn), Ours = f.Ours, FactionHostile = f.FactionHostile, PrisonBreak = f.PrisonBreak,
                    PredatorHunt = f.PredatorHunt, Predator = f.Predator, Downed = f.Downed };
                if (nearest.HasValue) row.NearestColonistDistance = nearest.Value;
                if (f.Mental != null) row.MentalState = f.Mental;
                if (f.FactionId != null) row.Faction = new RimGovernor.Protocol.Common.Ref { Id = f.FactionId };
                if (f.Passive.HasValue) row.Passive = f.Passive.Value;
                if (f.HasPrey) { row.Prey = prey(pawn); row.PreyIsOurs = f.PreyOurs; }
                threats.Pawns.Add(row);
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

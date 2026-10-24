#nullable enable

using System;
using System.Collections.Generic;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    /// The threat facts of one living non-colonist pawn: cheap field reads
    /// only, gathered before any pawn reference or distance scan is paid
    /// for.
    internal struct ThreatFacts
    {
        internal bool Ours;
        /// The pawn's mental state def name, or null.
        internal string? Mental;
        /// A non-player faction hostile to the player.
        internal bool FactionHostile;
        /// A prisoner of the colony breaking out; a held prisoner
        /// is never FactionHostile.
        internal bool PrisonBreak;
        /// The current job is PredatorHunt.
        internal bool PredatorHunt;
        /// The hunt's target resolves to a pawn (a live pawn or a corpse's).
        internal bool HasPrey;
        internal bool PreyOurs;
        internal bool Downed;
        internal bool Predator;
        internal int X, Z;
        /// Faction-hostile insects: dormant, or awake but targeting nothing of the
        /// player's. Other faction hostiles with CompCanBeDormant (such as mech clusters): asleep. False for every other pawn.
        internal bool Passive;
    }

    /// The facts of one faction that decide whether a raid can come from it.
    internal struct RaidFactionFacts
    {
        internal string Def;
        internal bool Player, Defeated, Hostile;
        /// The def has a pawn group maker of the Combat kind.
        internal bool Combat;
    }

    /// Filter-first threat fact rows: the native emits facts,
    /// the brain classifies them (bridge.ClassifyThreat). A pawn is kept when any
    /// fact a threat rule reads is set (a mental state, faction hostility, a
    /// prison break, a predator hunt), or when it is an unowned downed pawn
    /// or predator within the radius of a colonist; only a kept pawn gets
    /// its pawn table reference and nearest-colonist distance.
    /// Healthy non-predator wildlife costs one facts read and nothing else;
    /// the worst case stays O(pawns * colonists).
    ///
    /// Game-independent so the probes can drive it with counted projectors;
    /// NativeObservationTools.Status supplies the RimWorld adapters.
    internal static class NativeThreatClassifier
    {
        /// What one Collect call did: the probe's proof that discarded pawns are never projected.
        internal struct Scan { internal long Examined, Candidates, Projections, ProximityChecks; }

        /// The def names of the factions a raid can come from (ThreatFacts.HostileFactions): not the player, not defeated,
        /// hostile to the player, with a Combat group maker; one name per def, in ordinal order.
        internal static IReadOnlyList<string> HostileRaidFactions<T>(IEnumerable<T> factions, Func<T, RaidFactionFacts> facts)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var faction in factions) {
                var f = facts(faction);
                if (!f.Player && !f.Defeated && f.Hostile && f.Combat) names.Add(f.Def);
            }
            return new List<string>(names);
        }

        /// Whether f carries a fact a threat rule reads regardless of
        /// distance; such a pawn also gets combat detail in the pawn table.
        internal static bool Engaged(ThreatFacts f) => f.Mental != null || f.FactionHostile || f.PrisonBreak || f.PredatorHunt;

        internal static Scan Collect<T>(IReadOnlyList<T> pawns, Func<T, ThreatFacts> facts,
            IReadOnlyList<(int X, int Z)> colonists, double radius,
            Func<T, RimGovernor.Contracts.Wire.Common.Ref> project, ref Obs.ThreatsSnapshot threats)
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
                    PredatorHunt = f.PredatorHunt, Predator = f.Predator, Downed = f.Downed, Passive = f.Passive, MentalState = f.Mental,
                    NearestColonist = nearest is int cells ? new Obs.ColonistDistance.Nearest(cells) : new Obs.ColonistDistance.NoColonist(),
                    Prey = f.HasPrey ? new Obs.ThreatPrey(f.PreyOurs) : null };
                threats = threats with { Pawns = [.. threats.Pawns, row] };
            }
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

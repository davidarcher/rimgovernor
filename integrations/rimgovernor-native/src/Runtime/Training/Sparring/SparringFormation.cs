#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RimGovernor.Runtime
{
    // A free, eligible pawn as formation sees it: plain data, no game types.
    public sealed class SparringCandidate
    {
        public int Id;
        public int Melee;
        // Ids of the last bout's members: a different opponent is preferred.
        public int[] Previous = new int[0];
    }

    // One pawn in a running bout, for opponent choice.
    public sealed class SparringFighter
    {
        public int Id;
        public int Team;
        public int X;
        public int Z;
        public bool Standing;
    }

    // Who spars with whom (#2708), pure over simple data. Matchmaking is a
    // preference, never a filter: every free candidate is grouped, ranked by how
    // close its Melee level is to the bout's; a level below MinPreferredLevel
    // only ranks last (it is the first left out when markers run short).
    public static class SparringFormation
    {
        public const int MinSize = 2;
        public const int MaxSize = 4;
        public const int MinPreferredLevel = 3;

        // Bout sizes for n pawns: the fewest bouts that fit MaxSize, spread evenly
        // (5 -> 3+2, 6 -> 3+3, 7 -> 4+3), largest first. Empty below MinSize.
        public static List<int> Sizes(int n)
        {
            var sizes = new List<int>();
            if (n < MinSize) return sizes;
            var bouts = (n + MaxSize - 1) / MaxSize;
            for (var i = 0; i < bouts; i++) sizes.Add(n / bouts + (i < n % bouts ? 1 : 0));
            return sizes;
        }

        // Groups of candidate ids, at most freeMarkers pawns in all. A pawn is
        // seated by rank (level 3 and up first, then id); then each bout is seeded by
        // the highest-level pawn left and filled with the pawns nearest that level,
        // those who fought the seed last time after the rest.
        public static List<List<int>> Form(IReadOnlyList<SparringCandidate> free, int freeMarkers)
        {
            var seated = free.OrderBy(c => c.Melee >= MinPreferredLevel ? 0 : 1).ThenBy(c => c.Id)
                .Take(Math.Max(0, freeMarkers)).ToList();
            var groups = new List<List<int>>();
            var left = seated.OrderByDescending(c => c.Melee).ThenBy(c => c.Id).ToList();
            foreach (var size in Sizes(seated.Count))
            {
                var seed = left[0];
                left.RemoveAt(0);
                var mates = left
                    .OrderBy(c => seed.Previous.Contains(c.Id) || c.Previous.Contains(seed.Id) ? 1 : 0)
                    .ThenBy(c => Math.Abs(c.Melee - seed.Melee)).ThenBy(c => c.Id)
                    .Take(size - 1).ToList();
                foreach (var mate in mates) left.Remove(mate);
                groups.Add(new List<int> { seed.Id }.Concat(mates.Select(m => m.Id)).ToList());
            }
            return groups;
        }

        // Team per member (parallel to the input order). Pairs and a three fight
        // free-for-all, each pawn its own team; a four splits 2v2, the highest
        // level with the lowest.
        public static int[] Teams(IReadOnlyList<SparringCandidate> members)
        {
            var teams = new int[members.Count];
            if (members.Count != 4)
            {
                for (var i = 0; i < teams.Length; i++) teams[i] = i;
                return teams;
            }
            var ranked = Enumerable.Range(0, 4).OrderByDescending(i => members[i].Melee).ThenBy(i => members[i].Id).ToList();
            for (var rank = 0; rank < 4; rank++) teams[ranked[rank]] = rank == 0 || rank == 3 ? 0 : 1;
            return teams;
        }

        // The id of the living, standing member of another team nearest to self,
        // the lower id on a tie; -1 when none is left.
        public static int Opponent(SparringFighter self, IReadOnlyList<SparringFighter> fighters)
        {
            SparringFighter best = null;
            long bestDistance = long.MaxValue;
            foreach (var other in fighters)
            {
                if (other.Id == self.Id || other.Team == self.Team || !other.Standing) continue;
                long dx = other.X - self.X, dz = other.Z - self.Z;
                var distance = dx * dx + dz * dz;
                if (distance < bestDistance || distance == bestDistance && other.Id < best.Id)
                {
                    best = other;
                    bestDistance = distance;
                }
            }
            return best?.Id ?? -1;
        }
    }
}

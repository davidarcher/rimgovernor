#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomeBridge.BridgeTools
{
    // Starting-team composition policy. Pure: every criterion is a
    // function of the pawn facts, so the result depends only on the seeded
    // Rand stream the caller rerolls from. The game adapter
    // (ProtoLifecycleNewColonyTools) reads PawnFacts off the real pawns.
    //
    // The trait and passion figures were checked against the game's defs
    // (Core TraitDefs and Assembly-CSharp SkillRecord): the shipped trait
    // defNames are Pyromaniac, Wimp, Tough, Nimble and FastLearner (singular
    // traits) and the spectrum traits Nerves (degree 2 iron-willed, -2
    // volatile), Industriousness (2 industrious, -2 slothful) and DrugDesire
    // (2 chemical fascination); there is no IronWilled, Volatile, Slothful or
    // ChemicalFascination defName. SkillRecord learning factors are 0.35 none,
    // 1.0 minor, 1.5 major.
    internal enum TeamSkill { Plants, Cooking, Construction, Medicine, Mining, Shooting, Melee }

    internal readonly struct TraitFact
    {
        internal readonly string Def;
        internal readonly int Degree;
        internal TraitFact(string def, int degree) { Def = def; Degree = degree; }
    }

    internal readonly struct PawnFacts
    {
        // Indexed by (int)TeamSkill; the arrays are the pawn's, never mutated here.
        internal readonly int[] Levels;
        internal readonly int[] Passions; // 0 none, 1 minor (one flame), 2 major (burning)
        internal readonly bool[] SkillWorkEnabled; // the skill's work type is not disabled for the pawn
        internal readonly bool CanHaul;
        internal readonly bool CanBuildWalls; // Construction enabled and level at the wall's prerequisite
        internal readonly bool PermanentBadHealth;
        internal readonly TraitFact[] Traits;

        internal PawnFacts(int[] levels, int[] passions, bool[] skillWorkEnabled, bool canHaul, bool canBuildWalls,
            bool permanentBadHealth, TraitFact[] traits)
        {
            Levels = levels; Passions = passions; SkillWorkEnabled = skillWorkEnabled; CanHaul = canHaul;
            CanBuildWalls = canBuildWalls; PermanentBadHealth = permanentBadHealth; Traits = traits;
        }

        internal bool Has(string def, int? degree = null) =>
            Traits.Any(t => t.Def == def && (!degree.HasValue || t.Degree == degree.Value));
    }

    internal sealed class TeamPolicyException : Exception
    {
        internal TeamPolicyException(string message) : base(message) { }
    }

    internal static class NativeTeamPolicy
    {
        // Total rerolls across the team, not per pawn. Measured 40-80 ms per
        // reroll and 5-17 rerolls to accept a team, so 1,000 caps a failure near a minute.
        internal const int RerollBudget = 1000;

        // Hard rejects: the reason the pawn can never be on the team, or null.
        internal static string? HardReject(PawnFacts p)
        {
            if (!p.CanBuildWalls) return "cannot Construct";
            if (!p.CanHaul) return "cannot Haul";
            if (p.Has("Pyromaniac")) return "trait Pyromaniac";
            if (p.Has("Nerves", -2)) return "trait Volatile";
            if (p.Has("Wimp")) return "trait Wimp";
            if (p.Has("Industriousness", -2)) return "trait Slothful";
            if (p.Has("DrugDesire", 2)) return "trait Chemical Fascination";
            if (p.PermanentBadHealth) return "a permanent bad health condition";
            return null;
        }

        // Required coverage and combat counts for a team of this size. Combat
        // scales down under 4 colonists: one shooter from 2, one melee from 3.
        internal static int ShootersNeeded(int teamSize) => Math.Min(2, teamSize / 2);
        internal static int MeleeNeeded(int teamSize) => teamSize >= 3 ? 1 : 0;

        internal static readonly TeamSkill[] CoverageSkills =
            { TeamSkill.Plants, TeamSkill.Cooking, TeamSkill.Construction, TeamSkill.Medicine, TeamSkill.Mining };

        // The first unmet requirement of the whole team, or null when acceptable.
        internal static string? Unmet(IReadOnlyList<PawnFacts> team)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var reject = HardReject(team[i]);
                if (reject != null) return "colonist " + i + " is rejected: " + reject;
            }
            return UnmetCoverage(team);
        }

        private static string? UnmetCoverage(IReadOnlyList<PawnFacts> team)
        {
            foreach (var skill in CoverageSkills)
                if (!team.Any(p => p.SkillWorkEnabled[(int)skill]))
                    return "no colonist is capable of " + skill;
            var shooters = team.Count(p => p.Passions[(int)TeamSkill.Shooting] > 0);
            if (shooters < ShootersNeeded(team.Count))
                return "needs " + ShootersNeeded(team.Count) + " Shooting-passion colonists, has " + shooters;
            var melee = team.Count(p => p.Passions[(int)TeamSkill.Melee] > 0);
            if (melee < MeleeNeeded(team.Count))
                return "needs " + MeleeNeeded(team.Count) + " Melee-passion colonists, has " + melee;
            return null;
        }

        internal static float LearnMultiplier(int passion) => passion >= 2 ? 1.5f : passion == 1 ? 1f : 0.35f;

        // Skill the team wants a colonist to have: grower near 8, cook near 6,
        // builder near 4. A level past the target adds nothing, so a burning
        // passion at a low level outscores a high level with no passion.
        private static float Target(TeamSkill skill) =>
            skill == TeamSkill.Plants ? 8f : skill == TeamSkill.Cooking ? 6f : skill == TeamSkill.Construction ? 4f : 0f;

        internal static float Score(PawnFacts p)
        {
            var score = 0f;
            foreach (TeamSkill skill in Enum.GetValues(typeof(TeamSkill)))
            {
                var passion = p.Passions[(int)skill];
                var target = Target(skill);
                if (target > 0f)
                    score += LearnMultiplier(passion) * Math.Min(p.Levels[(int)skill], target) / target * 10f;
                else
                    score += passion * 2f; // medicine, mining, shooting, melee: the passion itself
            }
            if (p.Has("Nerves", 2)) score += 2f;
            if (p.Has("Tough")) score += 2f;
            if (p.Has("Industriousness", 2)) score += 2f;
            if (p.Has("FastLearner")) score += 2f;
            if (p.Has("Nimble")) score += 2f;
            return score;
        }

        // Which colonist to reroll next, or -1 when the team is acceptable.
        // Hard-rejected colonists go first, lowest index first. Otherwise the
        // lowest-scoring colonist whose removal keeps the coverage that is
        // already met (so a reroll never trades one gap for another by
        // construction); when every colonist is load-bearing, the lowest score.
        internal static int NextReroll(IReadOnlyList<PawnFacts> team)
        {
            for (var i = 0; i < team.Count; i++)
                if (HardReject(team[i]) != null) return i;
            if (UnmetCoverage(team) == null) return -1;
            var order = Enumerable.Range(0, team.Count).OrderBy(i => Score(team[i])).ThenBy(i => i).ToList();
            var gaps = Gaps(team, team.Count);
            foreach (var i in order)
            {
                var without = team.Where((_, j) => j != i).ToList();
                if (Gaps(without, team.Count) <= gaps) return i;
            }
            return order[0];
        }

        // Unmet coverage skills plus missing shooters and melee fighters,
        // judged against a team of the given size.
        private static int Gaps(IReadOnlyList<PawnFacts> team, int teamSize) =>
            CoverageSkills.Count(s => !team.Any(p => p.SkillWorkEnabled[(int)s]))
            + Math.Max(0, ShootersNeeded(teamSize) - team.Count(p => p.Passions[(int)TeamSkill.Shooting] > 0))
            + Math.Max(0, MeleeNeeded(teamSize) - team.Count(p => p.Passions[(int)TeamSkill.Melee] > 0));

        // Rerolls the team until acceptable. read(i) returns colonist i's facts
        // now; reroll(i) replaces colonist i with a fresh draw from the seeded
        // stream. Returns the rerolls used; throws TeamPolicyException naming
        // the unmet requirement when the budget runs out.
        internal static int Run(int teamSize, Func<int, PawnFacts> read, Action<int> reroll, int budget = RerollBudget)
        {
            var rerolls = 0;
            while (true)
            {
                var team = Enumerable.Range(0, teamSize).Select(read).ToList();
                var next = NextReroll(team);
                if (next < 0) return rerolls;
                if (rerolls >= budget)
                    throw new TeamPolicyException("Starting team unmet after " + budget + " rerolls: " + Unmet(team) + ".");
                reroll(next);
                rerolls++;
            }
        }
    }
}

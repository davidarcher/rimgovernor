using System;
using System.Collections.Generic;
using System.Linq;
using HomeBridge.BridgeTools;

// Team-composition policy on constructed pawn facts.
internal static class NativeTeamPolicyProbe
{
    private sealed class Spec
    {
        public int Plants = 0, Cooking = 0, Construction = 5, Medicine = 0, Mining = 0, Shooting = 0, Melee = 0;
        public int PlantsP = 0, CookingP = 0, ConstructionP = 0, ShootingP = 0, MeleeP = 0;
        public HashSet<TeamSkill> Disabled = new HashSet<TeamSkill>();
        public bool CanHaul = true, Health;
        public List<TraitFact> Traits = new List<TraitFact>();
    }

    private static PawnFacts Make(Action<Spec> edit = null)
    {
        var s = new Spec();
        edit?.Invoke(s);
        var levels = new[] { s.Plants, s.Cooking, s.Construction, s.Medicine, s.Mining, s.Shooting, s.Melee };
        var passions = new[] { s.PlantsP, s.CookingP, s.ConstructionP, 0, 0, s.ShootingP, s.MeleeP };
        var enabled = Enum.GetValues(typeof(TeamSkill)).Cast<TeamSkill>().Select(k => !s.Disabled.Contains(k)).ToArray();
        return new PawnFacts(levels, passions, enabled, s.CanHaul, enabled[(int)TeamSkill.Construction] && s.Construction >= 3, s.Health, s.Traits.ToArray());
    }

    private static PawnFacts Good() => Make();
    private static PawnFacts Shooter() => Make(s => s.ShootingP = 1);
    private static PawnFacts Fighter() => Make(s => s.MeleeP = 1);
    private static List<PawnFacts> Team4() => new List<PawnFacts> { Shooter(), Shooter(), Fighter(), Good() };

    internal static void Invoke()
    {
        Require(NativeTeamPolicy.Unmet(Team4()) == null, "a full team is acceptable");
        Require(NativeTeamPolicy.NextReroll(Team4()) == -1, "an acceptable team needs no reroll");

        // Each hard reject.
        var rejects = new Dictionary<string, PawnFacts>
        {
            { "Construction", Make(s => s.Disabled.Add(TeamSkill.Construction)) },
            { "Construction level", Make(s => s.Construction = 2) },
            { "Haul", Make(s => s.CanHaul = false) },
            { "Pyromaniac", Make(s => s.Traits.Add(new TraitFact("Pyromaniac", 0))) },
            { "Volatile", Make(s => s.Traits.Add(new TraitFact("Nerves", -2))) },
            { "Wimp", Make(s => s.Traits.Add(new TraitFact("Wimp", 0))) },
            { "Slothful", Make(s => s.Traits.Add(new TraitFact("Industriousness", -2))) },
            { "Chemical Fascination", Make(s => s.Traits.Add(new TraitFact("DrugDesire", 2))) },
            { "health", Make(s => s.Health = true) },
        };
        foreach (var pair in rejects)
        {
            Require(NativeTeamPolicy.HardReject(pair.Value) != null, "hard reject: " + pair.Key);
            var team = Team4(); team[3] = pair.Value;
            Require(NativeTeamPolicy.NextReroll(team) == 3, "rejected colonist rerolled first: " + pair.Key);
            Require(NativeTeamPolicy.Unmet(team).Contains("colonist 3"), "unmet names the rejected colonist: " + pair.Key);
        }
        // The tolerated neighbours of the rejected traits stay legal.
        foreach (var ok in new[] { new TraitFact("Nerves", 2), new TraitFact("Industriousness", 1), new TraitFact("DrugDesire", 1), new TraitFact("Industriousness", 2) })
            Require(NativeTeamPolicy.HardReject(Make(s => s.Traits.Add(ok))) == null, "legal trait " + ok.Def + ":" + ok.Degree);

        // Each coverage gap (Construction is also a hard reject, covered above).
        foreach (var skill in NativeTeamPolicy.CoverageSkills.Where(k => k != TeamSkill.Construction))
        {
            var team = new List<PawnFacts>
            {
                Make(s => { s.Disabled.Add(skill); s.ShootingP = 1; }), Make(s => { s.Disabled.Add(skill); s.ShootingP = 1; }),
                Make(s => { s.Disabled.Add(skill); s.MeleeP = 1; }), Make(s => s.Disabled.Add(skill)),
            };
            Require(NativeTeamPolicy.Unmet(team) == "no colonist is capable of " + skill, "coverage gap " + skill);
            Require(NativeTeamPolicy.NextReroll(team) >= 0, "coverage gap rerolls " + skill);
        }
        // Combat counts and scaling.
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Good(), Good(), Fighter(), Good() }).StartsWith("needs 2 Shooting"), "four colonists need two shooters");
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Shooter(), Fighter(), Good(), Good() }).StartsWith("needs 2 Shooting"), "one shooter of four is short");
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Shooter(), Shooter(), Good(), Good() }).StartsWith("needs 1 Melee"), "melee required at four");
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Shooter(), Fighter(), Good() }) == null, "three colonists: one shooter, one melee");
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Shooter(), Good() }) == null, "two colonists: one shooter, no melee");
        Require(NativeTeamPolicy.Unmet(new List<PawnFacts> { Good() }) == null, "one colonist: no combat requirement");
        // A short team rerolls a non-load-bearing colonist, never the lone shooter or fighter.
        var next = NativeTeamPolicy.NextReroll(new List<PawnFacts> { Shooter(), Fighter(), Good(), Good() });
        Require(next == 2 || next == 3, "the lone shooter and the fighter are kept, got " + next);

        // Passions over levels.
        var burning4 = Make(s => { s.Plants = 4; s.PlantsP = 2; });
        var none8 = Make(s => { s.Plants = 8; });
        var one8 = Make(s => { s.Plants = 8; s.PlantsP = 1; });
        Require(NativeTeamPolicy.Score(burning4) > NativeTeamPolicy.Score(none8), "burning level 4 outscores no-passion level 8");
        Require(NativeTeamPolicy.Score(one8) > NativeTeamPolicy.Score(none8), "a flame beats none at equal level");
        Require(NativeTeamPolicy.Score(Make(s => { s.Plants = 8; s.PlantsP = 2; })) > NativeTeamPolicy.Score(one8), "burning beats one flame");
        Require(NativeTeamPolicy.Score(Make(s => { s.Plants = 12; })) == NativeTeamPolicy.Score(none8), "a level past the target adds nothing");
        Require(NativeTeamPolicy.LearnMultiplier(0) == 0.35f && NativeTeamPolicy.LearnMultiplier(1) == 1f && NativeTeamPolicy.LearnMultiplier(2) == 1.5f, "learning multipliers");
        var plain = NativeTeamPolicy.Score(Good());
        foreach (var bonus in new[] { new TraitFact("Nerves", 2), new TraitFact("Tough", 0), new TraitFact("Industriousness", 2), new TraitFact("FastLearner", 0), new TraitFact("Nimble", 0) })
            Require(NativeTeamPolicy.Score(Make(s => s.Traits.Add(bonus))) > plain, "trait bonus " + bonus.Def);

        // Run rerolls the rejected colonist, counts the rerolls, stops when acceptable.
        var pool = new Queue<PawnFacts>(new[] { rejects["Wimp"], rejects["Pyromaniac"], Good() });
        var live = Team4(); live[3] = rejects["Volatile"];
        var used = NativeTeamPolicy.Run(4, i => live[i], i => live[i] = pool.Dequeue());
        Require(used == 3 && pool.Count == 0, "run rerolled three times, got " + used);
        Require(NativeTeamPolicy.Unmet(live) == null, "run ends acceptable");

        // Budget exhaustion is a total and names the unmet requirement.
        var always = Team4(); always[3] = rejects["Wimp"];
        var rolled = 0;
        try
        {
            NativeTeamPolicy.Run(4, i => always[i], i => { rolled++; always[i] = rejects["Wimp"]; }, 25);
            throw new Exception("budget exhaustion must throw");
        }
        catch (TeamPolicyException e)
        {
            Require(rolled == 25, "budget is a total: " + rolled);
            Require(e.Message.Contains("25 rerolls") && e.Message.Contains("trait Wimp"), "error names the unmet requirement: " + e.Message);
        }
        Require(NativeTeamPolicy.RerollBudget == 1000, "total budget is 1,000");
        Console.WriteLine("native-team-policy: hard rejects, coverage, combat scaling, passion ordering, budget passed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

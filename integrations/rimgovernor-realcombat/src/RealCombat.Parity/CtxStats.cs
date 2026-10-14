using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;

// IStatSource whose hypothetical-context requests are answered by a real generated pawn built to the context.
sealed class CtxStats(IStatSource inner) : IStatSource
{
    readonly Dictionary<string, Pawn> cache = new();
    Pawn PawnFor(StatSubject s)
    {
        var ctx = s.Context!; var kind = ctx.Pawn.KindDef.Value; var skills = ctx.Pawn.SkillLevels.Value;
        var key = kind + "|" + string.Join(",", skills.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value));
        if (cache.TryGetValue(key, out var p)) return p;
        Rand.PushState(4242);
        try
        {
            var kd = DefDatabase<PawnKindDef>.GetNamed(kind);
            var faction = kind == "Colonist" ? Faction.OfPlayer : Find.FactionManager.AllFactionsListForReading.First(f => f.def.defName == "PirateWaster");
            p = PawnGenerator.GeneratePawn(kd, faction);
        }
        finally { Rand.PopState(); }
        p.apparel.DestroyAll(); p.equipment.DestroyAllEquipment();
        foreach (var tr in p.story.traits.allTraits.ToList()) p.story.traits.RemoveTrait(tr);
        foreach (var h in p.health.hediffSet.hediffs.ToList()) p.health.RemoveHediff(h);
        foreach (var sk in p.skills.skills) { sk.Level = skills.TryGetValue(sk.def.defName, out var l) ? l : sk.Level; sk.passion = Passion.None; }
        return cache[key] = p;
    }
    float Read(string stat, StatSubject s, bool post) => PawnFor(s).GetStatValue(DefDatabase<StatDef>.GetNamed(stat), post);
    public StatResult Evaluate(string stat, StatSubject s) => s.Context == null ? inner.Evaluate(stat, s) : new StatResult(Read(stat, s, true), true);
    public float Value(string stat, StatSubject s) => s.Context == null ? inner.Value(stat, s) : Read(stat, s, true);
    public bool ShouldShowFor(string stat, StatSubject s) => s.Context == null ? inner.ShouldShowFor(stat, s) : true;
    public float ValueWithoutPostProcess(string stat, StatSubject s) => s.Context == null ? inner.ValueWithoutPostProcess(stat, s) : Read(stat, s, false);
    public IReadOnlyList<string> AllowedStuffsFor(string def) => inner.AllowedStuffsFor(def);
    public IReadOnlyList<StatCost> CostListAdjusted(string def, string stuff) => inner.CostListAdjusted(def, stuff);
    public IReadOnlyList<StatCost> TerrainCostListAdjusted(string t) => inner.TerrainCostListAdjusted(t);
}

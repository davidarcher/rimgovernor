
namespace RimGovernor.Brain.Simulation.Combat.Import;

// The per-part injury model of a pawn kind's race, derived from the catalog (A39).
internal sealed partial class Importer
{
    // Body part tags and capacity defs the ported capacity workers read.
    private const string TagConsciousness = "ConsciousnessSource";
    private const string TagBloodPumping = "BloodPumpingSource";
    private const string TagBreathingSource = "BreathingSource";
    private const string TagBreathingPath = "BreathingPathway";
    private const string TagBreathingCage = "BreathingSourceCage";
    private const string TagKidney = "BloodFiltrationKidney";
    private const string TagLiver = "BloodFiltrationLiver";
    private const string TagFiltration = "BloodFiltrationSource";
    private const string TagMetabolism = "MetabolismSource";
    private const string TagPelvis = "Pelvis";
    private const string TagSpine = "Spine";
    private const string TagMovingCore = "MovingLimbCore";
    private const string TagMovingSegment = "MovingLimbSegment";
    private const string TagMovingDigit = "MovingLimbDigit";
    private const string TagSight = "SightSource";
    private const string TagManipCore = "ManipulationLimbCore";
    private const string TagManipSegment = "ManipulationLimbSegment";
    private const string TagManipDigit = "ManipulationLimbDigit";

    private const string CapMoving = "Moving";

    // The hediff a destroyed part leaves.
    private const string MissingPartHediff = "MissingBodyPart";

    // The class of the hediffs a damage def adds.
    private const string InjuryHediffClass = "Hediff_Injury";

    // The capacities with lethalFlesh the sim evaluates (Pawn_HealthTracker.ShouldBeDead's CapableOf loop), each
    // with the worker the sim ports. Any other lethalFlesh capacity, or a different worker class, fails the import.
    private static readonly Dictionary<string, string> s_lethalCapacities = new(StringComparer.Ordinal)
    {
        ["Consciousness"] = "PawnCapacityWorker_Consciousness",
        ["BloodPumping"] = "PawnCapacityWorker_BloodPumping",
        ["Breathing"] = "PawnCapacityWorker_Breathing",
        ["BloodFiltration"] = "PawnCapacityWorker_BloodFiltration",
        ["Metabolism"] = "PawnCapacityWorker_Metabolism",
    };

    // The other capacities the downing and firing rules read.
    private static readonly Dictionary<string, string> s_limbCapacities = new(StringComparer.Ordinal)
    {
        ["Moving"] = "PawnCapacityWorker_Moving",
        ["Manipulation"] = "PawnCapacityWorker_Manipulation",
    };

    // Derives the per-part injury model of a pawn kind's race from the catalog (A39): the body def's parts with
    // their health, coverage and capacity tags, and the scalars from the pawn's stats and the damage def. It
    // fails on anything the ported rules do not cover.
    private Body BodyOf(Verse.RaceProperties race, double healthScale, double shock, double incoming, bool ignoresPainShock)
    {
        var dmg = _dmg ?? throw Unsupported("no damage def settled before the pawns are derived");
        var def = race.body;
        var bodyName = def?.defName ?? "";
        if (def?.corePart is null)
        {
            throw Unsupported($"catalog has no body def \"{bodyName}\"");
        }
        if (ignoresPainShock)
        {
            throw Unsupported("a pawn kind that ignores pain shock is not modeled");
        }
        CheckCapacities();
        var (pps, missingPps) = PainRates();
        var (bleed, missingBleed, merge) = BleedRates();

        // Pawn_HealthTracker.CanBleed: a blood def, a flesh race, a positive factor.
        var canBleed = race.BloodDef is not null && race.bleedRateFactor > 0;
        var flesh = race.FleshType; // a race with none is Normal
        if (flesh is null || !flesh.isOrganic)
        {
            canBleed = false;
        }
        var moving = cat.DefRow<Verse.PawnCapacityDef>(CapMoving)
            ?? throw Unsupported($"catalog has no pawn capacity \"{CapMoving}\"");

        var parts = new List<BodyPart>();
        var tags = new List<IReadOnlyList<string>>();
        Walk(def.corePart, -1, 1);

        void Walk(Verse.BodyPartRecord part, int parent, double parentAbs)
        {
            var partDef = part.def ?? throw Unsupported("a body part of body def \"" + bodyName + "\" has no def");
            bool inside;
            switch (part.depth)
            {
                case Verse.BodyPartDepth.Inside:
                    inside = true;
                    break;
                case Verse.BodyPartDepth.Outside:
                    inside = false;
                    break;
                default:
                    throw Unsupported($"body part \"{partDef.defName}\" has no depth");
            }

            // BodyDef.ResolveReferences: coverageAbs is the part's share of the body less its children's.
            var abs = 1.0;
            if (parent >= 0)
            {
                abs = parentAbs * part.coverage;
            }
            var own = abs;
            var children = part.parts ?? [];
            foreach (var c in children)
            {
                own -= abs * c.coverage;
            }
            var weight = own * HitChanceFactor(partDef, dmg.Def);
            if (partDef.conceptual || !(weight > 0))
            {
                weight = 0;
            }
            var maxHealth = Math.Ceiling((double)((float)partDef.hitPoints * (float)healthScale)); // BodyPartDef.GetMaxHealth
            var idx = parts.Count;
            var partTags = (partDef.tags ?? []).Select(t => t.defName).ToList();
            parts.Add(new BodyPart
            {
                Parent = parent,
                Inside = inside,
                Coverage = own,
                HitWeight = weight,
                MaxHealth = maxHealth,
                Solid = partDef.IsSolidInDefinition_Debug,
                Destroyable = partDef.destroyableByDamage,
                HitPoints = partDef.hitPoints,
                BleedRate = partDef.bleedRate,
                Tagged = partTags.Count > 0,
                HasChildren = children.Count > 0,
            });
            tags.Add(partTags);
            foreach (var c in children)
            {
                Walk(c, idx, abs);
            }
        }

        int[] WithTag(string tag)
        {
            var found = new List<int>();
            for (var i = 0; i < tags.Count; i++)
            {
                if (tags[i].Contains(tag, StringComparer.Ordinal))
                {
                    found.Add(i);
                }
            }
            return [.. found];
        }

        // BodyPartRecord.GetChildParts(tag): the part itself if tagged, then its descendants in order.
        // GetConnectedParts(tag) climbs through tagged parents first.
        bool Has(int i, string tag) => tags[i].Contains(tag, StringComparer.Ordinal);

        bool InSubtree(int i, int root)
        {
            for (var p = parts[i].Parent; p >= 0; p = parts[p].Parent)
            {
                if (p == root)
                {
                    return true;
                }
            }
            return false;
        }

        int[] ChildParts(int root, string tag)
        {
            var found = new List<int>();
            for (var i = root; i < parts.Count; i++)
            {
                if ((i == root || InSubtree(i, root)) && Has(i, tag))
                {
                    found.Add(i);
                }
            }
            return [.. found];
        }

        Limb[] Limbs(string core, string segment, string digit)
        {
            var found = new List<Limb>();
            foreach (var c in WithTag(core))
            {
                var top = c;
                for (var p = parts[top].Parent; p >= 0 && Has(p, segment); p = parts[top].Parent)
                {
                    top = p;
                }
                found.Add(new Limb(c, ChildParts(top, segment), ChildParts(c, digit)));
            }
            return [.. found];
        }

        var movingLimbs = Limbs(TagMovingCore, TagMovingSegment, TagMovingDigit);
        var manipLimbs = Limbs(TagManipCore, TagManipSegment, TagManipDigit);
        if (movingLimbs.Length == 0 || manipLimbs.Length == 0)
        {
            throw Unsupported($"body def \"{bodyName}\" has no moving or manipulating limb");
        }
        return new Body
        {
            Parts = parts,
            HealthScale = healthScale,
            PainPerSeverity = pps,
            MissingPainPerSeverity = missingPps,
            PainShock = shock,
            IncomingDamageFactor = incoming,
            HarmAllLayers = dmg.HarmAllLayers,
            OverkillLow = dmg.OverkillLow,
            OverkillHigh = dmg.OverkillHigh,
            BleedPerSeverity = bleed,
            MissingBleedPerSeverity = missingBleed,
            MergeInjuries = merge,
            BleedFactor = canBleed ? race.bleedRateFactor : 0,
            LethalDamage = Verse.HealthTuning.BaseTotalDamageLethalThreshold * healthScale,
            MovingMinForCapable = moving.minForCapable,
            Consciousness = WithTag(TagConsciousness),
            BloodPumping = WithTag(TagBloodPumping),
            BreathingSource = WithTag(TagBreathingSource),
            BreathingPathway = WithTag(TagBreathingPath),
            BreathingCage = WithTag(TagBreathingCage),
            FiltrationKidney = WithTag(TagKidney),
            FiltrationLiver = WithTag(TagLiver),
            FiltrationSource = WithTag(TagFiltration),
            Metabolism = WithTag(TagMetabolism),
            Pelvis = WithTag(TagPelvis),
            Spine = WithTag(TagSpine),
            Sight = WithTag(TagSight),
            Moving = movingLimbs,
            Manipulation = manipLimbs,
        };
    }

    // Fails unless every capacity the sim evaluates is the one the sim ports and no other capacity is lethal to flesh.
    private void CheckCapacities()
    {
        foreach (var name in CapacityNames())
        {
            var c = cat.DefRow<Verse.PawnCapacityDef>(name) ?? throw Unsupported($"catalog has no pawn capacity \"{name}\"");
            var ported = s_lethalCapacities.TryGetValue(name, out var worker);
            var isLimb = s_limbCapacities.TryGetValue(name, out var limb);
            if (ported || isLimb)
            {
                var want = worker + limb;
                var cls = c.workerClass?.Name ?? "";
                if (!string.Equals(cls, want, StringComparison.Ordinal))
                {
                    throw Unsupported($"pawn capacity \"{name}\" worker \"{c.workerClass}\" is not the ported \"{want}\"");
                }
                if (ported && (!c.lethalFlesh || c.minForCapable != 0))
                {
                    throw Unsupported($"pawn capacity \"{name}\" is not lethal below 0");
                }
            }
            else if (c.lethalFlesh)
            {
                throw Unsupported($"pawn capacity \"{name}\" is lethal to flesh and not ported");
            }
        }
    }

    // The catalog's pawn capacity def names in ordinal order (Go ranged a map; the order only picks which failure is reported first).
    private List<string> CapacityNames()
    {
        var names = new List<string>(cat.Rows<Verse.PawnCapacityDef>().Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // Reads the pain per severity of the damage's injury hediff and of the missing-part hediff, and fails when the
    // injury hediff has anything the per-part model leaves out (stages, lethal severity, painless chance).
    private (double Injury, double Missing) PainRates()
    {
        var (h, m) = InjuryHediffs();
        return (h.injuryProps.painPerSeverity, m.injuryProps.painPerSeverity);
    }

    // The damage's injury hediff and the missing-part hediff, refusing an injury hediff with anything the per-part
    // model leaves out (stages, lethal severity, painless chance).
    private (Verse.HediffDef Injury, Verse.HediffDef Missing) InjuryHediffs()
    {
        var dmg = _dmg ?? throw Unsupported("no damage def settled before the injury hediffs");
        var h = cat.DefRow<Verse.HediffDef>(dmg.Hediff);
        if (h?.injuryProps is null)
        {
            throw Unsupported($"catalog has no injury hediff \"{dmg.Hediff}\"");
        }
        if (h.hediffClass != typeof(Verse.Hediff_Injury))
        {
            throw Unsupported($"hediff \"{dmg.Hediff}\" class \"{h.hediffClass}\" is not {InjuryHediffClass}");
        }
        if (h.stages is { Count: > 0 } || h.lethalSeverity > 0 || h.chanceToCauseNoPain > 0)
        {
            throw Unsupported($"hediff \"{dmg.Hediff}\" has stages, a lethal severity or a painless chance, which the per-part model leaves out");
        }
        var m = cat.DefRow<Verse.HediffDef>(MissingPartHediff);
        if (m?.injuryProps is null)
        {
            throw Unsupported($"catalog has no hediff \"{MissingPartHediff}\"");
        }
        return (h, m);
    }

    // Reads the bleed rate per severity of the damage's injury hediff and of the missing-part hediff, and whether
    // the injury hediff merges (injuryProps.canMerge). PainRates has already checked both hediffs exist.
    private (double Injury, double Missing, bool Merge) BleedRates()
    {
        var (h, m) = InjuryHediffs();
        return (h.injuryProps.bleedRate, m.injuryProps.bleedRate, h.injuryProps.canMerge);
    }

    // BodyPartDef.GetHitChanceFactorFor.
    private static double HitChanceFactor(Verse.BodyPartDef part, string damageDef)
    {
        foreach (var e in part.hitChanceFactors ?? [])
        {
            if (string.Equals(e.Key.defName, damageDef, StringComparison.Ordinal))
            {
                return e.Value;
            }
        }
        return 1;
    }
}

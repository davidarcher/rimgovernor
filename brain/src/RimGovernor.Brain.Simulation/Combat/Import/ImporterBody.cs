using Defs = RimGovernor.Protocol.Defs;

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
    private Body BodyOf(Defs.RaceProperties race, double healthScale, double shock, double incoming, bool ignoresPainShock)
    {
        var dmg = _dmg!;
        var bodyName = race.Body;
        var def = cat.DefRow<Defs.BodyDef>(bodyName);
        if (def?.CorePart is null)
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
        var canBleed = race.BloodDef.Length != 0 && race.BleedRateFactor > 0;
        var fleshName = race.FleshType.Length == 0 ? "Normal" : race.FleshType; // a race with none is Normal
        var flesh = cat.DefRow<Defs.FleshTypeDef>(fleshName);
        if (flesh is null || !flesh.IsOrganic)
        {
            canBleed = false;
        }
        var constants = Constants();
        var moving = cat.DefRow<Defs.PawnCapacityDef>(CapMoving)
            ?? throw Unsupported($"catalog has no pawn capacity \"{CapMoving}\"");

        var parts = new List<BodyPart>();
        var tags = new List<IReadOnlyList<string>>();
        Walk(def.CorePart, -1, 1);

        void Walk(Defs.BodyPartRecord part, int parent, double parentAbs)
        {
            var partDef = cat.DefRow<Defs.BodyPartDef>(part.Def)
                ?? throw Unsupported($"catalog has no body part def \"{part.Def}\"");
            bool inside;
            switch (part.Depth)
            {
                case Defs.BodyPartDepth.Inside:
                    inside = true;
                    break;
                case Defs.BodyPartDepth.Outside:
                    inside = false;
                    break;
                default:
                    throw Unsupported($"body part \"{part.Def}\" has no depth");
            }

            // BodyDef.ResolveReferences: coverageAbs is the part's share of the body less its children's.
            var abs = 1.0;
            if (parent >= 0)
            {
                abs = parentAbs * part.Coverage;
            }
            var own = abs;
            foreach (var c in part.Parts)
            {
                own -= abs * c.Value.Coverage;
            }
            var weight = own * HitChanceFactor(partDef, dmg.Def);
            if (partDef.Conceptual || !(weight > 0))
            {
                weight = 0;
            }
            var maxHealth = Math.Ceiling((double)((float)partDef.HitPoints * (float)healthScale)); // BodyPartDef.GetMaxHealth
            var idx = parts.Count;
            parts.Add(new BodyPart
            {
                Parent = parent,
                Inside = inside,
                Coverage = own,
                HitWeight = weight,
                MaxHealth = maxHealth,
                Solid = partDef.Solid,
                Destroyable = partDef.DestroyableByDamage,
                HitPoints = partDef.HitPoints,
                BleedRate = partDef.BleedRate,
                Tagged = partDef.Tags.Count > 0,
                HasChildren = part.Parts.Count > 0,
            });
            tags.Add([.. partDef.Tags]);
            foreach (var c in part.Parts)
            {
                Walk(c.Value, idx, abs);
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
            BleedFactor = canBleed ? race.BleedRateFactor : 0,
            LethalDamage = constants.HealthTuning.BaseTotalDamageLethalThreshold * healthScale,
            MovingMinForCapable = moving.MinForCapable,
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
            var c = cat.DefRow<Defs.PawnCapacityDef>(name) ?? throw Unsupported($"catalog has no pawn capacity \"{name}\"");
            var ported = s_lethalCapacities.TryGetValue(name, out var worker);
            var isLimb = s_limbCapacities.TryGetValue(name, out var limb);
            if (ported || isLimb)
            {
                var want = worker + limb;
                var cls = c.WorkerClass;
                if (!string.Equals(cls, want, StringComparison.Ordinal) && !cls.EndsWith("." + want, StringComparison.Ordinal))
                {
                    throw Unsupported($"pawn capacity \"{name}\" worker \"{c.WorkerClass}\" is not the ported \"{want}\"");
                }
                if (ported && (!c.LethalFlesh || c.MinForCapable != 0))
                {
                    throw Unsupported($"pawn capacity \"{name}\" is not lethal below 0");
                }
            }
            else if (c.LethalFlesh)
            {
                throw Unsupported($"pawn capacity \"{name}\" is lethal to flesh and not ported");
            }
        }
    }

    // The catalog's pawn capacity def names in ordinal order (Go ranged a map; the order only picks which failure is reported first).
    private List<string> CapacityNames()
    {
        var names = new List<string>(cat.Rows(Defs.PawnCapacityDef.Descriptor.FullName).Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // Reads the pain per severity of the damage's injury hediff and of the missing-part hediff, and fails when the
    // injury hediff has anything the per-part model leaves out (stages, lethal severity, painless chance).
    private (double Injury, double Missing) PainRates()
    {
        var dmg = _dmg!;
        var h = cat.DefRow<Defs.HediffDef>(dmg.Hediff);
        if (h?.InjuryProps is null)
        {
            throw Unsupported($"catalog has no injury hediff \"{dmg.Hediff}\"");
        }
        if (h.HediffClass != InjuryHediffClass && !h.HediffClass.EndsWith("." + InjuryHediffClass, StringComparison.Ordinal))
        {
            throw Unsupported($"hediff \"{dmg.Hediff}\" class \"{h.HediffClass}\" is not {InjuryHediffClass}");
        }
        if (h.Stages.Count > 0 || h.LethalSeverity > 0 || h.ChanceToCauseNoPain > 0)
        {
            throw Unsupported($"hediff \"{dmg.Hediff}\" has stages, a lethal severity or a painless chance, which the per-part model leaves out");
        }
        var m = cat.DefRow<Defs.HediffDef>(MissingPartHediff);
        if (m?.InjuryProps is null)
        {
            throw Unsupported($"catalog has no hediff \"{MissingPartHediff}\"");
        }
        return (h.InjuryProps.PainPerSeverity, m.InjuryProps.PainPerSeverity);
    }

    // Reads the bleed rate per severity of the damage's injury hediff and of the missing-part hediff, and whether
    // the injury hediff merges (injuryProps.canMerge). PainRates has already checked both hediffs exist.
    private (double Injury, double Missing, bool Merge) BleedRates()
    {
        var h = cat.DefRow<Defs.HediffDef>(_dmg!.Hediff)!;
        var m = cat.DefRow<Defs.HediffDef>(MissingPartHediff)!;
        return (h.InjuryProps.BleedRate, m.InjuryProps.BleedRate, h.InjuryProps.CanMerge);
    }

    // BodyPartDef.GetHitChanceFactorFor.
    private static double HitChanceFactor(Defs.BodyPartDef part, string damageDef)
    {
        foreach (var e in part.HitChanceFactors)
        {
            if (string.Equals(e.Key, damageDef, StringComparison.Ordinal))
            {
                return e.Value;
            }
        }
        return 1;
    }
}

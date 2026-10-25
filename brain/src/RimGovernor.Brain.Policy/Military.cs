using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The next pest hunt the census supports (#3259): the selected pest rows, at most the hunting budget. The runtime
/// selects them (<see cref="PestRules.SelectPestAcquisition"/>) and names the Method by the rows, so the same
/// selection continues its open Method.
/// </summary>
/// <param name="Method">The hunt's MethodId, <c>pest-hunt-&lt;hash of the rows&gt;</c>.</param>
/// <param name="Rows">The pest rows, one hunt each.</param>
public sealed record PestHunt(MethodId Method, EquatableArray<AcquisitionSource> Rows);

/// <summary>An undispatched pest hunt action of the Need's own whose animal left the wild-animal census.</summary>
public readonly record struct StaleHunt(PlanId Plan, ActionId Action);

/// <summary>
/// What the military family reads (#3259). Defense: the hostiles standing, the armed and unarmed fighters and the
/// colonist count the foothold sizes for. Pests: the pest census, the Need's own undispatched hunts whose animal
/// left the census, the next hunt and whether an outdoor hazard holds hunting. Training: the assigned combat
/// capability review. Equipment: the gear census, the crafting spot owed down and the mortar shell review. A
/// section the run does not read stays unknown.
/// </summary>
public sealed record MilitaryFacts
{
    public HostileCensus Hostiles { get; init; } = new HostileCensus.AwaitingEmergency();

    /// <summary>
    /// The food plan's squad-hunt prey (<see cref="HuntSquadRules.HuntRequest"/>): a fight with no hostile standing.
    /// Empty when the plan opens no squad hunt or is unread; the combat Need is judged on the hostiles first.
    /// </summary>
    public EquatableArray<PawnId> HuntPrey { get; init; }

    public long Armed { get; init; }

    /// <summary>Living, conscious colonists able to fight who hold no weapon.</summary>
    public long Unarmed { get; init; }

    /// <summary>The colonist count the foothold sizes for (<see cref="Rules.FootholdCount"/>).</summary>
    public long Foothold { get; init; }

    /// <summary>The pests in the wild-animal census (<see cref="PestRules.PestCensus"/>).</summary>
    public int Pests { get; init; }

    /// <summary>The Need's undispatched hunts whose animal died or left the map: the work step cancels them.</summary>
    public EquatableArray<StaleHunt> StaleHunts { get; init; }

    /// <summary>The next pest hunt, or null when every pest is hunted, no hunter can, or the census is unread.</summary>
    public PestHunt? Hunt { get; init; }

    /// <summary>No pest hunt was selected for want of a hunter.</summary>
    public bool HuntersMissing { get; init; }

    /// <summary>The acquisition census could not be read for a selection.</summary>
    public bool HuntsUnread { get; init; }

    /// <summary>An outdoor hazard holds chop, hunt and forage like it holds remote work.</summary>
    public bool HuntingHeld { get; init; }

    /// <summary>The training review; null while the family read none, which raises <see cref="RequiredFactException"/> when the Need derives.</summary>
    public TrainingReview? Training { get; init; }

    /// <summary>The colonists' gear census (loadouts, apparel policies, outfits) the equipment Need is judged on.</summary>
    private GearObservation? _gear;

    public GearObservation Gear
    {
        get => _gear ?? throw new RequiredFactException("gear census");
        init => _gear = value;
    }

    /// <summary>A built mortar's shell stock is below half its target (<see cref="ArmoryShellsRules.ShellsShort"/>).</summary>
    public bool ShellsShort { get; init; }

    /// <summary>
    /// The drafted colonists no live plan needs (the undraft sweep's candidates); none while a threat holds the
    /// drafts, unknown while the colonist census is unread or incomplete.
    /// </summary>
    public int StrayDrafts { get; init; }

    /// <summary>The defense, training and equipment facts a review already holds; the pest section stays unread.</summary>
    public static MilitaryFacts From(ReviewFacts f) => new()
    {
        Hostiles = f.Hostiles,
        Armed = f.Armed,
        Unarmed = f.Unarmed,
        Foothold = Rules.FootholdCount(f),
        Training = TrainingRules.ReviewTraining(f),
        Gear = f.Gear,
        ShellsShort = f.ShellsShort,
    };
}

/// <summary>
/// The military family (#3259): basic defense, pest clearing, combat training and equipment upkeep. It replaced
/// the EnsureBasicDefense, ClearPests, MaintainTraining and MaintainEquipment Concerns, their inspections and the
/// equip, pest acquisition, training, gear and armory planners.
/// <list type="bullet">
/// <item><c>military/defense</c> (Wellbeing): no hostile stands, the foothold's armed fighters are armed, and no
/// fighting-capable colonist stands unarmed. Arming is not yet a supported forecast outcome, so an unmet Need is
/// held as <c>unsupported_forecast_equipment</c>.</item>
/// <item><c>military/pests</c> (Wellbeing): no pest in the wild-animal census and no stale hunt of the Need's own.
/// Its Method is the next pest hunt; hunts of other pests run side by side and a second hunt of one animal is
/// refused by its claim.</item>
/// <item><c>military/training</c> (Development): no assigned combat-capability holder is forecast below its
/// requirement. Its Methods are the range and ring rooms the runtime's work step builds.</item>
/// <item><c>military/equipment</c> (Wellbeing): every colonist's loadout and apparel policy is recovered, no crafting
/// spot is owed down beside a weapon bench and no built mortar is short of shells. Its proposed Methods are one
/// apparel policy per pawn (claimed by <c>gear/&lt;pawn&gt;</c>, so two pawns run side by side and a second policy
/// for one pawn is refused) and, once no policy is proposed, the outfit prune (claimed by <c>policy/Outfit</c>).
/// The runtime's work step places or retires the crafting spot and walks the workshop ladder for replacement
/// gear; worn-gear replacement itself is not a supported forecast outcome.</item>
/// <item><c>military/restore-workers</c> (SeriousHarm, #3311): no drafted colonist stands that no live plan needs
/// while no hostile does; an unread census raises <see cref="RequiredFactException"/>. It has no Methods: its work step is the undraft
/// sweep, which replaced the RestoreWorkers Concern and the undraft planner.</item>
/// <item><c>military/combat</c> (SeriousHarm, #3311): one per map, <see cref="Rules.CombatSituation"/> over
/// the hostiles and the squad-hunt prey: unmet while a hostile stands or a squad hunt is open, met otherwise; an
/// unread hostile count raises <see cref="RequiredFactException"/>. It proposes no Methods: the defense runner fights it (<c>engage-&lt;map&gt;</c>,
/// <c>subdue-&lt;colonist&gt;</c>, <c>strip-&lt;raider&gt;</c>, <c>permit-&lt;hash&gt;</c>, its batches and the
/// settings restoration). It replaced the combat incident Concern.</item>
/// </list>
/// </summary>
public sealed class MilitaryFamily(bool defense = true, bool pests = true, bool training = true, bool equipment = true, bool restoreWorkers = true, bool combat = true) : IFamily<MilitaryFacts>
{
    public const string FamilyId = "military";

    public static readonly NeedId Defense = NeedId.Of(FamilyId, "defense");

    public static readonly NeedId Pests = NeedId.Of(FamilyId, "pests");

    public static readonly NeedId Training = NeedId.Of(FamilyId, "training");

    public static readonly NeedId Equipment = NeedId.Of(FamilyId, "equipment");

    public static readonly NeedId RestoreWorkers = NeedId.Of(FamilyId, "restore-workers");

    public static readonly NeedId Combat = NeedId.Of(FamilyId, "combat");

    /// <summary>The stable refusal subject of an unmet defense Need.</summary>
    public const string UnsupportedEquipment = "unsupported_forecast_equipment";

    /// <summary>The stable refusal subject of worn or missing gear no proposed Method replaces.</summary>
    public const string UnsupportedApparelReplacement = "unsupported_forecast_apparel_replacement";

    /// <summary>The most apparel policies one run proposes.</summary>
    public const int MaxApparelPolicies = 16;

    public string Id => FamilyId;

    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms, FactFamily.Emergency, FactFamily.World, FactFamily.Research];

    /// <summary>Restore-workers and combat read the colonist rows and the emergency census in the immediate pass.</summary>
    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = restoreWorkers || combat ? [FactFamily.Pawns, FactFamily.Emergency] : [];

    public IReadOnlyList<Need> Derive(MilitaryFacts facts)
    {
        var needs = new List<Need>(DeriveSeriousHarm(facts));
        if (defense)
        {
            needs.Add(DefenseNeed(facts));
        }
        if (pests)
        {
            needs.Add(PestsNeed(facts));
        }
        if (training)
        {
            needs.Add(TrainingNeed(facts.Training));
        }
        if (equipment)
        {
            needs.Add(EquipmentNeed(facts));
        }
        return needs;
    }

    /// <summary>Combat and restore-workers, the SeriousHarm Needs.</summary>
    public IReadOnlyList<Need> DeriveSeriousHarm(MilitaryFacts facts) =>
    [
        .. combat ? [CombatNeed(facts)] : Array.Empty<Need>(),
        .. restoreWorkers ? [RestoreWorkersNeed(facts)] : Array.Empty<Need>(),
    ];

    /// <summary>
    /// Defense is held; pests proposes its next hunt unless hunting is held; equipment proposes its apparel
    /// policies, else its outfit prune; training and the equipment buildings are the work step's.
    /// </summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, MilitaryFacts facts)
    {
        var output = new List<NeedMethod>();
        foreach (var need in ready)
        {
            if (need.Id == Defense)
            {
                output.Add(NeedMethod.Held(Defense, UnsupportedEquipment,
                    "Observed equipment demand requires shared forecast worker, timing and outcome alternatives."));
            }
            else if (need.Id == Pests && !facts.HuntingHeld && facts.Hunt is { Rows.Count: > 0 } hunt)
            {
                output.Add(new NeedMethod(Pests, hunt.Method, "pest " + hunt.Rows[0].Definition, id => HuntPlan(id, hunt)));
            }
            else if (need.Id == Equipment)
            {
                output.AddRange(EquipmentMethods(facts.Gear));
            }
        }
        return output;
    }

    /// <summary>
    /// The equipment Need's proposals: the apparel policy each unblocked pawn's role and loadout want and it does
    /// not hold, at most <see cref="MaxApparelPolicies"/>; with none, the prune of every outfit no pawn keeps. A
    /// Method is named by its value alone, so the same policy continues its open Method.
    /// </summary>
    public static IReadOnlyList<NeedMethod> EquipmentMethods(GearObservation gear)
    {
        var output = new List<NeedMethod>();
        foreach (var pawn in gear.Pawns)
        {
            if (output.Count == MaxApparelPolicies)
            {
                break;
            }
            if (pawn.Blocked || !ApparelPolicyRules.DesiredApparelPolicy(pawn, out var desired) || desired is not { } value)
            {
                continue;
            }
            output.Add(new NeedMethod(Equipment, ValueMethod("apparel-policy", ApparelPolicyEncoded(value)), "apparel policy",
                id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewApparelPolicyAction(new ActionId(id.Value + "-0"), value)])));
        }
        if (output.Count > 0)
        {
            return output;
        }
        var drop = gear.OutfitsToPrune();
        if (drop.Count > 0)
        {
            var prune = new PolicyPrune(PolicyDatabase.Outfit, drop);
            output.Add(new NeedMethod(Equipment, ValueMethod("outfit-prune", "[" + string.Join(" ", prune.Ids) + "]"), "outfit prune",
                id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPolicyPruneAction(new ActionId(id.Value + "-0"), prune)])));
        }
        return output;
    }

    /// <summary>
    /// The equipment predicate: the gear review recovered and no mortar short of shells. A shell shortage is unmet
    /// whatever the gear census says; otherwise an unread gear census raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static bool EquipmentMet(MilitaryFacts f) => !f.ShellsShort && GearRules.ReviewGear(f.Gear).Recovered;

    private static Need EquipmentNeed(MilitaryFacts f)
    {
        var ok = EquipmentMet(f);
        // The gear review's deficit while gear is short; a retire or a shell shortage alone is a whole deficit.
        var deficit = ok ? 0.0 : GearRules.ReviewGear(f.Gear) is { Recovered: false } gear ? gear.Deficit : 1.0;
        return new Need { Id = Equipment, Tier = NeedTier.Wellbeing, Satisfaction = ok ? Satisfaction.Met : Satisfaction.Unmet, Deficit = deficit };
    }

    /// <summary>
    /// The apparel policy's JSON spec (definitions sorted), the seed of its MethodId. Doubles print shortest
    /// round-trip over the 0..1 HP range.
    /// </summary>
    public static string ApparelPolicyEncoded(ApparelPolicy policy)
    {
        var s = policy.Spec;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("Pawn", s.Pawn.Value);
            w.WriteString("Token", s.Token);
            w.WriteString("Name", s.Name);
            w.WriteStartArray("Definitions");
            foreach (var d in s.Definitions)
            {
                w.WriteStringValue(d);
            }
            w.WriteEndArray();
            w.WriteNumber("MinHP", s.MinHP);
            w.WriteNumber("MaxHP", s.MaxHP);
            w.WriteNumber("MinQuality", s.MinQuality);
            w.WriteNumber("MaxQuality", s.MaxQuality);
            // Written only when set, so a default policy keeps its MethodId.
            if (s.AllowTainted)
            {
                w.WriteBoolean("AllowTainted", true);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>A MethodId named by its value alone: <c>&lt;prefix&gt;-&lt;hash of the value&gt;</c>.</summary>
    private static MethodId ValueMethod(string prefix, string value) =>
        new(prefix + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16)));

    /// <summary>
    /// The basic-defense predicate: the foothold's armed fighters (<see cref="Rules.ArmedTarget"/>) with
    /// no hostile standing, and no fighting-capable colonist left unarmed; unknown when the facts do not say.
    /// </summary>
    public static bool DefenseMet(MilitaryFacts f)
    {
        var armed = f.Armed >= Math.Min(2, f.Foothold);
        return f.Hostiles.ThreatCount == 0 && armed && f.Unarmed == 0;
    }

    private static Need DefenseNeed(MilitaryFacts f)
    {
        var ok = DefenseMet(f);
        var deficit = ok ? 0.0 : 1.0;
        var n = f.Foothold;
        var stock = f.Armed;
        if (!ok && n > 0)
        {
            // The armed shortfall's share of the target; with the target armed, the unarmed share of the fighters.
            var target = Rules.ArmedTarget(n);
            var share = Math.Max(0, (double)(target - stock) / target);
            if (f.Unarmed > 0 && share == 0)
            {
                share = (double)f.Unarmed / (f.Unarmed + stock);
            }
            deficit = share;
        }
        return new Need { Id = Defense, Tier = NeedTier.Wellbeing, Satisfaction = ok ? Satisfaction.Met : Satisfaction.Unmet, Deficit = deficit };
    }

    /// <summary>
    /// The restore-workers predicate: unmet while a stray draft stands and no hostile does; met with no stray draft
    /// or while a hostile stands (the fight owns the drafts); unknown while either census is unread.
    /// </summary>
    public static bool RestoreWorkersMet(MilitaryFacts f) => f.StrayDrafts == 0 || f.Hostiles.ThreatCount > 0;

    private static Need RestoreWorkersNeed(MilitaryFacts f)
    {
        var ok = RestoreWorkersMet(f);
        return new Need
        {
            Id = RestoreWorkers,
            Tier = NeedTier.SeriousHarm,
            Satisfaction = ok ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = ok ? 0.0 : f.StrayDrafts,
        };
    }

    /// <summary>
    /// The combat Need: <see cref="Rules.CombatSituation"/> over the hostiles and the squad-hunt prey. Its
    /// deficit is the hostiles standing plus the prey.
    /// </summary>
    private static Need CombatNeed(MilitaryFacts f)
    {
        var situation = Rules.CombatSituation(f.Hostiles, f.HuntPrey.Count);
        var hostiles = f.Hostiles.ThreatCount;
        var met = situation == Situation.Clear;
        return new Need
        {
            Id = Combat,
            Tier = NeedTier.SeriousHarm,
            Satisfaction = met ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = met ? 0.0 : (double)(hostiles + f.HuntPrey.Count),
        };
    }

    private static Need PestsNeed(MilitaryFacts f)
    {
        DomainException.Require(f.Pests >= 0, "negative pest census");
        var owed = f.Pests + f.StaleHunts.Count;
        return new Need
        {
            Id = Pests,
            Tier = NeedTier.Wellbeing,
            Satisfaction = owed == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = (double)owed,
        };
    }

    private static Need TrainingNeed(TrainingReview? review)
    {
        var r = review ?? throw new RequiredFactException("training review");
        return new Need
        {
            Id = Training,
            Tier = NeedTier.Development,
            Satisfaction = r.Below == 0 ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = r.Below == 0 || r.Capable == 0 ? 0.0 : (double)r.Below / r.Capable,
        };
    }

    private static PlanSpec HuntPlan(PlanId id, PestHunt hunt) =>
        new(id, new PlanRevision(1), hunt.Rows.Select((row, i) =>
            DomainAction.NewAcquisitionAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id.Value}-{i}")), new Acquisition(row.Id, row.Resource, row.Cell))));

}

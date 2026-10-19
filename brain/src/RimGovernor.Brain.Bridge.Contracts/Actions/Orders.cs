using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Contracts.Wire.Operations;
using WireDesign = RimGovernor.Contracts.Wire.Common.IdeoligionDesign;

namespace RimGovernor.Brain.Bridge.Contracts;

// The intents that name a pawn and a colony
// decision.
public static class Orders
{
    /// <summary>The ResearchIntent of one research selection. Native judges the project against live research state when it applies.</summary>
    internal static Op.Action ResearchAction(DomainAction action)
    {
        var v = action.ResearchSelect ?? throw BridgeContractException.Contract("not a research select action");
        WireValidation.ValidId(v.Project);
        return new Op.Action { Intent = new Op.ResearchIntent { ProjectDef = v.Project } };
    }

    /// <summary>
    /// The DialogIntent of one dialog answer, the exact observed window (or joiner letter) ID,
    /// option position and label, and for a joiner letter its snapshot token.
    /// </summary>
    internal static Op.Action DialogAction(DomainAction action)
    {
        var v = action.DialogAnswer ?? throw BridgeContractException.Contract("not a dialog answer action");
        if (v.WindowId < 0 || v.OptionIndex < 0 || (v.LetterToken.Length != 0 && v.OptionIndex != 0))
        {
            throw BridgeContractException.Contract("invalid dialog target");
        }
        var intent = new Op.DialogIntent { WindowId = v.WindowId, OptionIndex = v.OptionIndex, OptionLabel = v.OptionLabel };
        if (v.LetterToken.Length != 0)
        {
            intent = intent with { JoinerLetterToken = v.LetterToken };
        }
        return new Op.Action { Intent = intent };
    }

    // Each domain mode to the exclusive interaction native sets. Enslave and Convert exist
    // only while Ideology is active, and native refuses them otherwise.
    private static Op.PrisonerInteraction? PrisonerInteractionWire(PrisonerInteractionMode mode) =>
        mode switch
        {
            PrisonerInteractionMode.Recruit => Op.PrisonerInteraction.AttemptRecruit,
            PrisonerInteractionMode.Maintain => Op.PrisonerInteraction.MaintainOnly,
            PrisonerInteractionMode.ReduceResistance => Op.PrisonerInteraction.ReduceResistance,
            PrisonerInteractionMode.Release => Op.PrisonerInteraction.Release,
            PrisonerInteractionMode.Enslave => Op.PrisonerInteraction.Enslave,
            PrisonerInteractionMode.Convert => Op.PrisonerInteraction.Convert,
            _ => null,
        };

    /// <summary>
    /// The PrisonerInteractionIntent of one prisoner custody order. Native checks
    /// the prisoner and the mode's eligibility live when it applies; a prisoner already set to the mode applies again.
    /// </summary>
    internal static Op.Action PrisonerInteractionAction(DomainAction action)
    {
        var v = action.PrisonerInteraction ?? throw BridgeContractException.Contract("not a prisoner interaction action");
        if (PrisonerInteractionWire(v.Interaction) is not { } wire)
        {
            throw BridgeContractException.Contract("invalid prisoner interaction");
        }
        return new Op.Action { Intent = new Op.PrisonerInteractionIntent { PawnId = v.Pawn.Value, Interaction = wire } };
    }

    /// <summary>
    /// The AcceptQuestIntent of one quest acceptance, the quest, the accepter when the
    /// quest requires one, and the reward option; native checks the game's own acceptance rules when it applies.
    /// </summary>
    internal static Op.Action QuestAcceptAction(DomainAction action)
    {
        var accept = action.QuestAccept ?? throw BridgeContractException.Contract("not a quest accept action");
        if (accept.RewardChoice < -1)
        {
            throw BridgeContractException.Contract("quest accept reward choice invalid");
        }
        var intent = new Op.AcceptQuestIntent { QuestId = accept.Quest.Value, RewardChoice = accept.RewardChoice };
        if (accept.AccepterPawn.Value.Length != 0)
        {
            intent = intent with { AccepterPawnId = accept.AccepterPawn.Value };
        }
        return new Op.Action { Intent = intent };
    }

    internal static Op.Action QuestShuttleAction(DomainAction action)
    {
        var s = action.QuestShuttle ?? throw BridgeContractException.Contract("not a quest shuttle action");
        var intent = new Op.QuestShuttleIntent { QuestId = s.Quest.Value, Launch = s.Launch };
        switch (s.Loading)
        {
            case ShuttleLoading.Autoload:
                intent = intent with { Loading = new Op.QuestShuttleIntentLoading.AutoloadCase(s.Autoload) };
                break;
            case ShuttleLoading.ExplicitPawns:
                intent = intent with { Loading = new Op.QuestShuttleIntentLoading.ExplicitPawnsCase(new Op.ShuttlePawnList { PawnIds = [.. s.Pawns.Select(pawn => pawn.Value)] }) };
                break;
        }
        return new Op.Action { Intent = intent };
    }

    internal static Op.Action HackDesignationAction(DomainAction action)
    {
        var h = action.HackDesignation ?? throw BridgeContractException.Contract("not a hack designation action");
        return new Op.Action { Intent = new Op.HackDesignationIntent { Target = IntentActionRules.NewRef(h.Target), Enabled = h.Enabled } };
    }

    internal static Op.Action GiveItemAction(DomainAction action)
    {
        var g = action.GiveItem ?? throw BridgeContractException.Contract("not a give item action");
        return new Op.Action
        {
            Intent = new Op.GiveItemIntent
            {
                Hauler = IntentActionRules.NewRef(g.Hauler.Value),
                Recipient = IntentActionRules.NewRef(g.Recipient.Value),
                Definition = g.Definition,
                ExpectedRemaining = g.ExpectedRemaining,
            },
        };
    }

    /// <summary>
    /// The RitualIntent of one ritual command. A start names the colonist the ritual is for, a
    /// begin the organizer, the ritual precept, the spot and the exact role and spectator assignments; native checks the
    /// ritual waits for the command, or may begin, when it applies.
    /// </summary>
    internal static Op.Action RitualAction(DomainAction action)
    {
        var ritual = action.Ritual ?? throw BridgeContractException.Contract("not a ritual action");
        var intent = new Op.RitualIntent { PawnId = ritual.Pawn.Value, Ritual = ritual.Kind.Value, Verb = Domain.Wire.Name(ritual.Verb) };
        switch (ritual.Verb)
        {
            case RitualVerb.Start:
                break;
            case RitualVerb.Begin:
                intent = intent with { Spot = IntentActionRules.WireCell(ritual.Spot) };
                foreach (var slot in ritual.Slots)
                {
                    intent = intent with { Roles = [.. intent.Roles, new Op.RitualRoleAssignment { Slot = slot.Slot, PawnIds = [.. slot.Pawns.Select(pawn => pawn.Value)] }] };
                }
                intent = intent with { SpectatorPawnIds = [.. intent.SpectatorPawnIds, .. ritual.Spectators.Select(pawn => pawn.Value)] };
                break;
            default:
                throw BridgeContractException.Contract("ritual requires a pawn, a known ritual and a known verb");
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>The wire form of a domain ideoligion design.</summary>
    public static WireDesign WireIdeoligionDesign(IdeoligionDesign v) => new()
    {
        Memes = [.. v.Memes],
        Precepts = [.. v.Precepts],
        Fluid = v.Fluid,
        VeneratedAnimals = [.. v.VeneratedAnimals],
        StyleCategories = [.. v.StyleCategories],
    };

    internal static Op.Action IdeoligionReformAction(DomainAction action)
    {
        var v = action.IdeoligionReform ?? throw BridgeContractException.Contract("not an ideoligion reform action");
        return new Op.Action
        {
            Intent = new Op.IdeoligionReformIntent
            {
                IdeoId = v.IdeoId,
                Expected = WireIdeoligionDesign(v.Expected),
                Design = WireIdeoligionDesign(v.Design),
                ExpectedReformCount = v.Count,
            },
        };
    }

    /// <summary>
    /// Checks shape only. Native owns compatibility, faction restrictions,
    /// required issues and the game's selection limits.
    /// </summary>
    public static void ValidateIdeoligionDesign(WireDesign? design)
    {
        if (design is null || design.Fluid is null || design.Memes.Count == 0 || design.Memes.Count > 5 || design.Precepts.Count > 256
            || design.VeneratedAnimals.Count > 256 || design.StyleCategories.Count > 256)
        {
            throw BridgeContractException.Contract("invalid ideoligion design");
        }
        foreach (var names in new[] { design.Memes, design.Precepts, design.VeneratedAnimals, design.StyleCategories })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (!WireValidation.IsValidId(name) || !seen.Add(name))
                {
                    throw BridgeContractException.Contract("invalid or duplicate ideoligion selection");
                }
            }
        }
    }

    /// <summary>
    /// The AbilityIntent of one ability use, the pawn, the source arm and the target arm.
    /// Native's guard for the source checks eligibility against live state when it applies.
    /// </summary>
    internal static Op.Action AbilityAction(DomainAction action)
    {
        var ability = action.Ability ?? throw BridgeContractException.Contract("not an ability action");
        var intent = new Op.AbilityIntent { PawnId = ability.Pawn.Value };
        var source = ability.Source;
        switch (source.Kind)
        {
            case AbilitySourceKind.Permit:
                intent = intent with { Source = new Op.AbilityIntentSource.PermitCase(new Op.PermitAbility { FactionDef = source.Faction, Permit = source.Def }) };
                break;
            case AbilitySourceKind.Psycast:
                intent = intent with { Source = new Op.AbilityIntentSource.PsycastCase(new Op.PsycastAbility { Ability = source.Def }) };
                break;
            default:
                throw BridgeContractException.Contract($"unsupported ability source \"{Domain.Wire.Name(source.Kind)}\"");
        }
        var target = ability.Target;
        switch (target.Kind)
        {
            case AbilityTargetKind.None:
                intent = intent with { Target = new Op.AbilityIntentTarget.NoTargetCase(new Op.AbilityNoTarget()) };
                break;
            case AbilityTargetKind.Cell:
                intent = intent with { Target = new Op.AbilityIntentTarget.CellCase(IntentActionRules.WireCell(target.Cell)) };
                break;
            case AbilityTargetKind.Pawn:
                intent = intent with { Target = new Op.AbilityIntentTarget.PawnCase(target.Id) };
                break;
            case AbilityTargetKind.Thing:
                intent = intent with { Target = new Op.AbilityIntentTarget.ThingCase(target.Id) };
                break;
            default:
                throw BridgeContractException.Contract($"unsupported ability target \"{Domain.Wire.Name(target.Kind)}\"");
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>The Actions/Apply gathering arm, the organizer starts one vanilla GatheringDef. The intent names no spot; the game chooses it.</summary>
    internal static Op.Action GatheringAction(DomainAction action)
    {
        var gathering = action.Gathering ?? throw BridgeContractException.Contract("not a gathering action");
        return new Op.Action { Intent = new Op.GatheringIntent { GatheringDef = gathering.Def, Organizer = IntentActionRules.NewRef(gathering.Organizer.Value) } };
    }

    /// <summary>The Actions/Apply conversion arm, the moral guide's Convert ability on one colonist.</summary>
    internal static Op.Action ConversionAction(DomainAction action)
    {
        var conversion = action.Conversion ?? throw BridgeContractException.Contract("not a conversion action");
        return new Op.Action { Intent = new Op.ConversionIntent { GuideId = conversion.Guide.Value, TargetId = conversion.Target.Value } };
    }

    /// <summary>
    /// The arm of one door_control action, the existing combat_orders door order for the
    /// cell, so no new native intent. Native refuses a cell that is no player door as a result in the CombatOrdersEffect
    /// (refusal not_a_door); the door facts the cell came from are read from that same door, so the next review sees a
    /// door still held open when an order did not take.
    /// </summary>
    internal static Op.Action DoorControlAction(DomainAction action)
    {
        var door = action.DoorControl ?? throw BridgeContractException.Contract("not a door_control action");
        var mode = door.HoldOpen ? Op.CombatDoorMode.HoldOpen : Op.CombatDoorMode.Close;
        var command = new Op.CombatOrders
        {
            Orders = [new Op.CombatOrder { Order = new Op.CombatOrderOrder.DoorCase(new Op.CombatDoor { Cell = IntentActionRules.WireCell(door.Cell), Mode = mode }) }],
        };
        CombatOrdersRules.ValidateCombatOrders(command);
        return new Op.Action { Intent = command };
    }

    /// <summary>
    /// The FormCaravanIntent of one caravan departure, the crew, the whole pack by
    /// definition (journey food included) and the destination tile; native checks formation when it applies.
    /// </summary>
    internal static Op.Action CaravanDepartureAction(DomainAction action)
    {
        var departure = action.CaravanDeparture ?? throw BridgeContractException.Contract("not a caravan departure action");
        if (departure.Crew.Count == 0 || departure.DestinationTile < 0)
        {
            throw BridgeContractException.Contract("caravan departure requires a crew and a destination");
        }
        var intent = new Op.FormCaravanIntent { DestinationTile = departure.DestinationTile, Mode = (Op.CaravanMode)departure.Mode };
        foreach (var pawn in departure.Crew)
        {
            intent = intent with { PawnIds = [.. intent.PawnIds, pawn.Value] };
        }
        foreach (var item in departure.Cargo)
        {
            if (item.Count == 0 || item.Count > int.MaxValue)
            {
                throw BridgeContractException.Contract("invalid caravan cargo item");
            }
            intent = intent with { Cargo = [.. intent.Cargo, new Op.DefCount { DefName = item.Definition, Count = (int)item.Count }] };
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>
    /// The AssignIntent of one ownership transfer, the pawn, the assignable thing (a bed, a
    /// throne) and the thing of that kind the pawn owned when the planner chose (or none). Native checks the pawn, the
    /// thing and that previous ownership live when it applies; a pawn that already owns the thing applies again.
    /// </summary>
    internal static Op.Action AssignAction(DomainAction action)
    {
        var v = action.Assign ?? throw BridgeContractException.Contract("not an assign action");
        var previous = v.Previous.Clear ? new Op.Assignment { Value = new Op.AssignmentValue.ClearCase() } : new Op.Assignment { Value = new Op.AssignmentValue.EntityIdCase(v.Previous.Id) };
        var intent = new Op.AssignIntent { PawnId = v.Pawn.Value, ThingId = v.Thing, ExpectedPrevious = previous };
        if (v.Swap)
        {
            intent = intent with { Swap = true };
        }
        return new Op.Action { Intent = intent };
    }

    // Each domain method to the order native applies (HusbandryActionHandler).
    private static Op.HusbandryOrder? HusbandryOrderWire(HusbandryMethod method) =>
        method switch
        {
            HusbandryMethod.Train => Op.HusbandryOrder.Train,
            HusbandryMethod.Slaughter => Op.HusbandryOrder.Slaughter,
            HusbandryMethod.Tame => Op.HusbandryOrder.Tame,
            HusbandryMethod.Release => Op.HusbandryOrder.Release,
            HusbandryMethod.AllowedArea => Op.HusbandryOrder.AllowedArea,
            HusbandryMethod.Master => Op.HusbandryOrder.Master,
            HusbandryMethod.FollowDrafted => Op.HusbandryOrder.FollowDrafted,
            HusbandryMethod.FollowFieldwork => Op.HusbandryOrder.FollowFieldwork,
            HusbandryMethod.CancelSlaughter => Op.HusbandryOrder.CancelSlaughter,
            HusbandryMethod.CancelRelease => Op.HusbandryOrder.CancelRelease,
            HusbandryMethod.Sterilize => Op.HusbandryOrder.Sterilize,
            _ => null,
        };

    /// <summary>
    /// The HusbandryIntent of one animal order. Native checks the animal and the order's
    /// eligibility live when it applies; an order that already holds applies again.
    /// </summary>
    internal static Op.Action HusbandryAction(DomainAction action)
    {
        var v = action.Husbandry ?? throw BridgeContractException.Contract("not a husbandry action");
        if (HusbandryOrderWire(v.Method) is not { } order)
        {
            throw BridgeContractException.Contract("invalid husbandry order");
        }
        var intent = new Op.HusbandryIntent { AnimalId = v.Animal.Value, Order = order };
        switch (v.Method)
        {
            case HusbandryMethod.Train:
                intent = intent with { TrainableDef = v.Argument };
                break;
            case HusbandryMethod.AllowedArea or HusbandryMethod.Master:
                // An empty argument clears the assignment.
                if (v.Argument.Length != 0)
                {
                    intent = intent with { TargetId = v.Argument };
                }
                break;
            case HusbandryMethod.FollowDrafted or HusbandryMethod.FollowFieldwork:
                intent = intent with { Follow = v.Argument == "true" };
                break;
        }
        return new Op.Action { Intent = intent };
    }

    /// <summary>
    /// The ApparelPolicyIntent of a role apparel policy, the pawn and the complete
    /// filter. Native validates the pawn and the definitions live when it applies.
    /// </summary>
    internal static Op.Action ApparelPolicyAction(DomainAction action)
    {
        var v = action.ApparelPolicy ?? throw BridgeContractException.Contract("not an apparel policy action");
        var s = v.Spec;
        return new Op.Action
        {
            Intent = new Op.ApparelPolicyIntent
            {
                PawnId = s.Pawn.Value,
                Name = s.Name,
                AllowedDefs = [.. s.Definitions],
                MinHitPoints = (float)s.MinHP,
                MaxHitPoints = (float)s.MaxHP,
                MinQuality = s.MinQuality,
                MaxQuality = s.MaxQuality,
                AllowTainted = s.AllowTainted,
            },
        };
    }
}

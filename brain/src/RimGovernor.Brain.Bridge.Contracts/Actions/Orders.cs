using DomainCell = RimGovernor.Brain.Domain.Cell;
using Op = RimGovernor.Protocol.Operations;
using WireDesign = RimGovernor.Protocol.Common.IdeoligionDesign;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go research_select.go, dialog.go (dialogAction), prisoner_interaction.go, quest_accept.go, quest_shuttle.go,
// quest_ideology_actions.go, ritual.go, ideoligion_design.go, ability.go, ignite.go, gathering.go, close_door.go,
// caravan_departure.go, assign.go, husbandry.go and apparel_policy.go: the intents that name a pawn and a colony
// decision. validateChoiceDialog and validateJoinerLetters of dialog.go validate census replies and belong to the
// census port.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>researchAction</c>: the ResearchIntent of one research selection. Native judges the project against live research state when it applies.</summary>
    private static Op.Action ResearchAction(DomainAction action)
    {
        var v = action.ResearchSelect ?? throw Contract("not a research select action");
        ValidId(v.Project);
        return new Op.Action { Research = new Op.ResearchIntent { ProjectDef = v.Project } };
    }

    /// <summary>
    /// Go's <c>dialogAction</c>: the DialogIntent of one dialog answer, the exact observed window (or joiner letter) ID,
    /// option position and label, and for a joiner letter its snapshot token.
    /// </summary>
    private static Op.Action DialogAction(DomainAction action)
    {
        var v = action.DialogAnswer ?? throw Contract("not a dialog answer action");
        if (v.WindowId < 0 || v.OptionIndex < 0 || !IsValidId(v.OptionLabel) || (v.LetterToken.Length != 0 && (v.OptionIndex != 0 || !IsValidId(v.LetterToken))))
        {
            throw Contract("invalid dialog target");
        }
        var intent = new Op.DialogIntent { WindowId = v.WindowId, OptionIndex = v.OptionIndex, OptionLabel = v.OptionLabel };
        if (v.LetterToken.Length != 0)
        {
            intent.JoinerLetterToken = v.LetterToken;
        }
        return new Op.Action { Dialog = intent };
    }

    // Go prisonerInteractionWire: each domain mode to the exclusive interaction native sets. Enslave and Convert exist
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
    /// Go's <c>prisonerInteractionAction</c>: the PrisonerInteractionIntent of one prisoner custody order. Native checks
    /// the prisoner and the mode's eligibility live when it applies; a prisoner already set to the mode applies again.
    /// </summary>
    private static Op.Action PrisonerInteractionAction(DomainAction action)
    {
        var v = action.PrisonerInteraction ?? throw Contract("not a prisoner interaction action");
        if (PrisonerInteractionWire(v.Interaction) is not { } wire || !IsValidId(v.Pawn.Value))
        {
            throw Contract("invalid prisoner interaction");
        }
        return new Op.Action { Prisoner = new Op.PrisonerInteractionIntent { PawnId = v.Pawn.Value, Interaction = wire } };
    }

    /// <summary>
    /// Go's <c>questAcceptAction</c>: the AcceptQuestIntent of one quest acceptance, the quest, the accepter when the
    /// quest requires one, and the reward option; native checks the game's own acceptance rules when it applies.
    /// </summary>
    private static Op.Action QuestAcceptAction(DomainAction action)
    {
        var accept = action.QuestAccept ?? throw Contract("not a quest accept action");
        if (!IsValidId(accept.Quest.Value) || (accept.AccepterPawn.Value.Length != 0 && !IsValidId(accept.AccepterPawn.Value)) || accept.RewardChoice < -1)
        {
            throw Contract("quest accept requires a quest id");
        }
        var intent = new Op.AcceptQuestIntent { QuestId = accept.Quest.Value, RewardChoice = accept.RewardChoice };
        if (accept.AccepterPawn.Value.Length != 0)
        {
            intent.AccepterPawnId = accept.AccepterPawn.Value;
        }
        return new Op.Action { AcceptQuest = intent };
    }

    private static Op.Action QuestShuttleAction(DomainAction action)
    {
        var s = action.QuestShuttle ?? throw Contract("not a quest shuttle action");
        var intent = new Op.QuestShuttleIntent { QuestId = s.Quest.Value, Launch = s.Launch };
        switch (s.Loading)
        {
            case ShuttleLoading.Autoload:
                intent.Autoload = s.Autoload;
                break;
            case ShuttleLoading.ExplicitPawns:
                intent.ExplicitPawns = new Op.ShuttlePawnList { PawnIds = { s.Pawns.Select(pawn => pawn.Value) } };
                break;
        }
        return new Op.Action { QuestShuttle = intent };
    }

    private static Op.Action HackDesignationAction(DomainAction action)
    {
        var h = action.HackDesignation ?? throw Contract("not a hack designation action");
        return new Op.Action { HackDesignation = new Op.HackDesignationIntent { Target = NewRef(h.Target), Enabled = h.Enabled } };
    }

    private static Op.Action GiveItemAction(DomainAction action)
    {
        var g = action.GiveItem ?? throw Contract("not a give item action");
        return new Op.Action
        {
            GiveItem = new Op.GiveItemIntent
            {
                Hauler = NewRef(g.Hauler.Value),
                Recipient = NewRef(g.Recipient.Value),
                Definition = g.Definition,
                ExpectedRemaining = g.ExpectedRemaining,
            },
        };
    }

    /// <summary>
    /// Go's <c>ritualAction</c>: the RitualIntent of one ritual command. A start names the colonist the ritual is for, a
    /// begin the organizer, the ritual precept, the spot and the exact role and spectator assignments; native checks the
    /// ritual waits for the command, or may begin, when it applies.
    /// </summary>
    private static Op.Action RitualAction(DomainAction action)
    {
        var ritual = action.Ritual ?? throw Contract("not a ritual action");
        var intent = new Op.RitualIntent { PawnId = ritual.Pawn.Value, Ritual = ritual.Kind.Value, Verb = Domain.Wire.Name(ritual.Verb) };
        switch (ritual.Verb)
        {
            case RitualVerb.Start:
                break;
            case RitualVerb.Begin:
                intent.Spot = WireCell(ritual.Spot);
                foreach (var slot in ritual.Slots)
                {
                    intent.Roles.Add(new Op.RitualRoleAssignment { Slot = slot.Slot, PawnIds = { slot.Pawns.Select(pawn => pawn.Value) } });
                }
                intent.SpectatorPawnIds.Add(ritual.Spectators.Select(pawn => pawn.Value));
                break;
            default:
                throw Contract("ritual requires a pawn, a known ritual and a known verb");
        }
        return new Op.Action { Ritual = intent };
    }

    /// <summary>Go's <c>WireIdeoligionDesign</c>: the wire form of a domain ideoligion design.</summary>
    public static WireDesign WireIdeoligionDesign(IdeoligionDesign v) => new() { Memes = { v.Memes }, Precepts = { v.Precepts }, Fluid = v.Fluid };

    private static Op.Action IdeoligionReformAction(DomainAction action)
    {
        var v = action.IdeoligionReform ?? throw Contract("not an ideoligion reform action");
        return new Op.Action
        {
            IdeoligionReform = new Op.IdeoligionReformIntent
            {
                IdeoId = v.IdeoId,
                Expected = WireIdeoligionDesign(v.Expected),
                Design = WireIdeoligionDesign(v.Design),
                ExpectedReformCount = v.Count,
            },
        };
    }

    /// <summary>
    /// Go's <c>ValidateIdeoligionDesign</c>: checks shape only. Native owns compatibility, faction restrictions,
    /// required issues and the game's selection limits.
    /// </summary>
    public static void ValidateIdeoligionDesign(WireDesign? design)
    {
        if (design is null || !design.HasFluid || design.Memes.Count == 0 || design.Memes.Count > 5 || design.Precepts.Count > 256)
        {
            throw Contract("invalid ideoligion design");
        }
        foreach (var names in new[] { design.Memes, design.Precepts })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (!IsValidId(name) || !seen.Add(name))
                {
                    throw Contract("invalid or duplicate ideoligion selection");
                }
            }
        }
    }

    /// <summary>
    /// Go's <c>abilityAction</c>: the AbilityIntent of one ability use, the pawn, the source arm and the target arm.
    /// Native's guard for the source checks eligibility against live state when it applies.
    /// </summary>
    private static Op.Action AbilityAction(DomainAction action)
    {
        var ability = action.Ability ?? throw Contract("not an ability action");
        var intent = new Op.AbilityIntent { PawnId = ability.Pawn.Value };
        var source = ability.Source;
        switch (source.Kind)
        {
            case AbilitySourceKind.Permit:
                intent.Permit = new Op.PermitAbility { FactionDef = source.Faction, Permit = source.Def };
                break;
            case AbilitySourceKind.Psycast:
                intent.Psycast = new Op.PsycastAbility { Ability = source.Def };
                break;
            default:
                throw Contract($"unsupported ability source \"{Domain.Wire.Name(source.Kind)}\"");
        }
        var target = ability.Target;
        switch (target.Kind)
        {
            case AbilityTargetKind.None:
                intent.NoTarget = new Op.AbilityNoTarget();
                break;
            case AbilityTargetKind.Cell:
                intent.Cell = WireCell(target.Cell);
                break;
            case AbilityTargetKind.Pawn:
                intent.Pawn = target.Id;
                break;
            case AbilityTargetKind.Thing:
                intent.Thing = target.Id;
                break;
            default:
                throw Contract($"unsupported ability target \"{Domain.Wire.Name(target.Kind)}\"");
        }
        return new Op.Action { Ability = intent };
    }

    /// <summary>Go's <c>igniteAction</c>: the Actions/Apply ignite arm, the pawn throws a molotov at the cell. Room occupancy is policy (BurnOccupied).</summary>
    private static Op.Action IgniteAction(DomainAction action)
    {
        var ignite = action.Ignite ?? throw Contract("not an ignite action");
        return new Op.Action { Ignite = new Op.IgniteIntent { PawnId = ignite.Pawn.Value, Cell = WireCell(ignite.Cell) } };
    }

    /// <summary>Go's <c>gatheringAction</c>: the Actions/Apply gathering arm, the organizer starts one vanilla GatheringDef. The intent names no spot; the game chooses it.</summary>
    private static Op.Action GatheringAction(DomainAction action)
    {
        var gathering = action.Gathering ?? throw Contract("not a gathering action");
        return new Op.Action { Gathering = new Op.GatheringIntent { GatheringDef = gathering.Def, Organizer = NewRef(gathering.Organizer.Value) } };
    }

    /// <summary>
    /// Go's <c>doorControlAction</c>: the arm of one door_control action, the existing combat_orders door order for the
    /// cell, so no new native intent. Native refuses a cell that is no player door as a result in the CombatOrdersEffect
    /// (refusal not_a_door); the door facts the cell came from are read from that same door, so the next review sees a
    /// door still held open when an order did not take.
    /// </summary>
    private static Op.Action DoorControlAction(DomainAction action)
    {
        var door = action.DoorControl ?? throw Contract("not a door_control action");
        var mode = door.HoldOpen ? Op.CombatDoorMode.HoldOpen : Op.CombatDoorMode.Close;
        var command = new Op.CombatOrders
        {
            Orders = { new Op.CombatOrder { Door = new Op.CombatDoor { Cell = WireCell(door.Cell), Mode = mode } } },
        };
        ValidateCombatOrders(command);
        return new Op.Action { CombatOrders = command };
    }

    /// <summary>
    /// Go's <c>caravanDepartureAction</c>: the FormCaravanIntent of one caravan departure, the crew, the whole pack by
    /// definition (journey food included) and the destination tile; native checks formation when it applies.
    /// </summary>
    private static Op.Action CaravanDepartureAction(DomainAction action)
    {
        var departure = action.CaravanDeparture ?? throw Contract("not a caravan departure action");
        if (departure.Crew.Count == 0 || departure.DestinationTile < 0)
        {
            throw Contract("caravan departure requires a crew and a destination");
        }
        var intent = new Op.FormCaravanIntent { DestinationTile = departure.DestinationTile };
        foreach (var pawn in departure.Crew)
        {
            if (!IsValidId(pawn.Value))
            {
                throw Contract("invalid caravan crew pawn");
            }
            intent.PawnIds.Add(pawn.Value);
        }
        foreach (var item in departure.Cargo)
        {
            if (!IsValidId(item.Definition) || item.Count == 0 || item.Count > int.MaxValue)
            {
                throw Contract("invalid caravan cargo item");
            }
            intent.Cargo.Add(new Op.DefCount { DefName = item.Definition, Count = (int)item.Count });
        }
        return new Op.Action { FormCaravan = intent };
    }

    /// <summary>
    /// Go's <c>assignAction</c>: the AssignIntent of one ownership transfer, the pawn, the assignable thing (a bed, a
    /// throne) and the thing of that kind the pawn owned when the planner chose (or none). Native checks the pawn, the
    /// thing and that previous ownership live when it applies; a pawn that already owns the thing applies again.
    /// </summary>
    private static Op.Action AssignAction(DomainAction action)
    {
        var v = action.Assign ?? throw Contract("not an assign action");
        var previous = v.Previous.Clear ? new Op.Assignment { Clear = new Op.Clear() } : new Op.Assignment { EntityId = v.Previous.Id };
        var intent = new Op.AssignIntent { PawnId = v.Pawn.Value, ThingId = v.Thing, ExpectedPrevious = previous };
        if (v.Swap)
        {
            intent.Swap = true;
        }
        return new Op.Action { Assign = intent };
    }

    // Go husbandryOrders: each domain method to the order native applies (HusbandryActionHandler).
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
    /// Go's <c>husbandryAction</c>: the HusbandryIntent of one animal order. Native checks the animal and the order's
    /// eligibility live when it applies; an order that already holds applies again.
    /// </summary>
    private static Op.Action HusbandryAction(DomainAction action)
    {
        var v = action.Husbandry ?? throw Contract("not a husbandry action");
        if (HusbandryOrderWire(v.Method) is not { } order || !IsValidId(v.Animal.Value))
        {
            throw Contract("invalid husbandry order");
        }
        var intent = new Op.HusbandryIntent { AnimalId = v.Animal.Value, Order = order };
        switch (v.Method)
        {
            case HusbandryMethod.Train:
                intent.TrainableDef = v.Argument;
                break;
            case HusbandryMethod.AllowedArea or HusbandryMethod.Master:
                // An empty argument clears the assignment.
                if (v.Argument.Length != 0)
                {
                    intent.TargetId = v.Argument;
                }
                break;
            case HusbandryMethod.FollowDrafted or HusbandryMethod.FollowFieldwork:
                intent.Follow = v.Argument == "true";
                break;
        }
        return new Op.Action { Husbandry = intent };
    }

    /// <summary>
    /// Go's <c>apparelPolicyAction</c>: the ApparelPolicyIntent of a role apparel policy, the pawn and the complete
    /// filter. Native validates the pawn and the definitions live when it applies.
    /// </summary>
    private static Op.Action ApparelPolicyAction(DomainAction action)
    {
        var v = action.ApparelPolicy ?? throw Contract("not an apparel policy action");
        var s = v.Spec;
        return new Op.Action
        {
            ApparelPolicy = new Op.ApparelPolicyIntent
            {
                PawnId = s.Pawn.Value,
                Name = s.Name,
                AllowedDefs = { s.Definitions },
                MinHitPoints = (float)s.MinHP,
                MaxHitPoints = (float)s.MaxHP,
                MinQuality = s.MinQuality,
                MaxQuality = s.MaxQuality,
            },
        };
    }
}

using DomainCell = RimGovernor.Brain.Domain.Cell;
using WireDesign = RimGovernor.Brain.GameFacts.Common.IdeoligionDesign;

namespace RimGovernor.Brain.GameAccess;

// The intents that name a pawn and a colony
// decision.
public static class Orders
{
    /// <summary>The ResearchIntent of one research selection. Native judges the project against live research state when it applies.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ResearchAction(DomainAction action)
    {
        var v = action.ResearchSelect ?? throw BridgeContractException.Contract("not a research select action");
        WireValidation.ValidId(v.Project);
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.ResearchIntent { ProjectDef = v.Project } };
    }

    /// <summary>
    /// The DialogIntent of one dialog answer, the exact observed window (or joiner letter) ID,
    /// option position and label, and for a joiner letter its snapshot token.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action DialogAction(DomainAction action)
    {
        var v = action.DialogAnswer ?? throw BridgeContractException.Contract("not a dialog answer action");
        if (v.WindowId < 0 || v.OptionIndex < 0 || (v.LetterToken.Length != 0 && v.OptionIndex != 0))
        {
            throw BridgeContractException.Contract("invalid dialog target");
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.DialogIntent { WindowId = v.WindowId, OptionIndex = v.OptionIndex, OptionLabel = v.OptionLabel };
        if (v.LetterToken.Length != 0)
        {
            intent = intent with { JoinerLetterToken = v.LetterToken };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    // Each domain mode to the exclusive interaction native sets. Enslave and Convert exist
    // only while Ideology is active, and native refuses them otherwise.
    private static RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction? PrisonerInteractionWire(PrisonerInteractionMode mode) =>
        mode == PrisonerInteractionMode.Recruit ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.AttemptRecruit
        : mode == PrisonerInteractionMode.Maintain ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.MaintainOnly
        : mode == PrisonerInteractionMode.ReduceResistance ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.ReduceResistance
        : mode == PrisonerInteractionMode.Release ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Release
        : mode == PrisonerInteractionMode.Enslave ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Enslave
        : mode == PrisonerInteractionMode.Convert ? RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.Convert
        : null;

    /// <summary>
    /// The PrisonerInteractionIntent of one prisoner custody order. Native checks
    /// the prisoner and the mode's eligibility live when it applies; a prisoner already set to the mode applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action PrisonerInteractionAction(DomainAction action)
    {
        var v = action.PrisonerInteraction ?? throw BridgeContractException.Contract("not a prisoner interaction action");
        if (PrisonerInteractionWire(v.Interaction) is not { } wire)
        {
            throw BridgeContractException.Contract("invalid prisoner interaction");
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.PrisonerInteractionIntent { PawnId = v.Pawn.Value, Interaction = wire } };
    }

    /// <summary>
    /// The AcceptQuestIntent of one quest acceptance, the quest, the accepter when the
    /// quest requires one, and the reward option; native checks the game's own acceptance rules when it applies.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action QuestAcceptAction(DomainAction action)
    {
        var accept = action.QuestAccept ?? throw BridgeContractException.Contract("not a quest accept action");
        if (accept.RewardChoice < -1)
        {
            throw BridgeContractException.Contract("quest accept reward choice invalid");
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.AcceptQuestIntent { QuestId = accept.Quest.Value, RewardChoice = accept.RewardChoice == -1 ? new RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.NoChoice() : new RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.Choose(accept.RewardChoice)};
        if (accept.AccepterPawn.Value.Length != 0)
        {
            intent = intent with { AccepterPawnId = accept.AccepterPawn.Value };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action QuestShuttleAction(DomainAction action)
    {
        var s = action.QuestShuttle ?? throw BridgeContractException.Contract("not a quest shuttle action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent { QuestId = s.Quest.Value, Launch = s.Launch };
        switch (s.Loading)
        {
            case ShuttleLoading.Autoload:
                intent = intent with { Loading = new RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.AutoloadCase(s.Autoload) };
                break;
            case ShuttleLoading.ExplicitPawns:
                intent = intent with { Loading = new RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.ExplicitPawnsCase(new RimGovernor.Brain.GameOrders.Operations.ShuttlePawnList { PawnIds = [.. s.Pawns.Select(pawn => pawn.Value)] }) };
                break;
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action HackDesignationAction(DomainAction action)
    {
        var h = action.HackDesignation ?? throw BridgeContractException.Contract("not a hack designation action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent { Target = IntentActionRules.NewRef(h.Target), Enabled = h.Enabled } };
    }

    internal static RimGovernor.Brain.GameOrders.Operations.Action GiveItemAction(DomainAction action)
    {
        var g = action.GiveItem ?? throw BridgeContractException.Contract("not a give item action");
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.GiveItemIntent
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
    internal static RimGovernor.Brain.GameOrders.Operations.Action RitualAction(DomainAction action)
    {
        var ritual = action.Ritual ?? throw BridgeContractException.Contract("not a ritual action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.RitualIntent
        {
            PawnId = ritual.Pawn.Value,
            Ritual = ritual.Kind.Value,
            Verb = Domain.Wire.Name(ritual.Verb),
            Location = new RimGovernor.Brain.GameOrders.Operations.RitualLocation.NotStarting(),
        };
        if (ritual.Verb == RitualVerb.Begin)
        {
            intent = intent with { Location = new RimGovernor.Brain.GameOrders.Operations.RitualLocation.At(ritual.Spot) };
            foreach (var slot in ritual.Slots)
            {
                intent = intent with
                {
                    Roles =
                    [
                        .. intent.Roles,
                        new RimGovernor.Brain.GameOrders.Operations.RitualRoleAssignment
                        {
                            Slot = slot.Slot,
                            PawnIds = [.. slot.Pawns.Select(pawn => pawn.Value)],
                        },
                    ],
                };
            }
            intent = intent with
            {
                SpectatorPawnIds =
                [
                    .. intent.SpectatorPawnIds,
                    .. ritual.Spectators.Select(pawn => pawn.Value),
                ],
            };
        }
        else if (ritual.Verb != RitualVerb.Start)
        {
            throw BridgeContractException.Contract(
                "ritual requires a pawn, a known ritual and a known verb"
            );
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
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

    internal static RimGovernor.Brain.GameOrders.Operations.Action IdeoligionReformAction(DomainAction action)
    {
        var v = action.IdeoligionReform ?? throw BridgeContractException.Contract("not an ideoligion reform action");
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.IdeoligionReformIntent
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
        if (design is null || design.Memes.Count == 0 || design.Memes.Count > 5 || design.Precepts.Count > 256
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
    internal static RimGovernor.Brain.GameOrders.Operations.Action AbilityAction(DomainAction action)
    {
        var ability = action.Ability ?? throw BridgeContractException.Contract("not an ability action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.AbilityIntent { PawnId = ability.Pawn.Value };
        var source = ability.Source;
        intent = intent with { Source = AbilitySourceWire(source) };
        var target = ability.Target;
        intent = intent with { Target = AbilityTargetWire(target) };
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    private static RimGovernor.Brain.GameOrders.Operations.AbilityIntentSource AbilitySourceWire(AbilitySource source)
    {
        if (source.Kind == AbilitySourceKind.Permit)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentSource.PermitCase(
                new RimGovernor.Brain.GameOrders.Operations.PermitAbility { FactionDef = source.Faction, Permit = source.Def }
            );
        }
        if (source.Kind == AbilitySourceKind.Psycast)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentSource.PsycastCase(
                new RimGovernor.Brain.GameOrders.Operations.PsycastAbility { Ability = source.Def }
            );
        }
        throw BridgeContractException.Contract(
            $"unsupported ability source \"{Domain.Wire.Name(source.Kind)}\""
        );
    }

    private static RimGovernor.Brain.GameOrders.Operations.AbilityIntentTarget AbilityTargetWire(AbilityTarget target)
    {
        if (target.Kind == AbilityTargetKind.None)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentTarget.NoTargetCase(new RimGovernor.Brain.GameOrders.Operations.AbilityNoTarget());
        }
        if (target.Kind == AbilityTargetKind.Cell)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentTarget.CellCase(IntentActionRules.WireCell(target.Cell));
        }
        if (target.Kind == AbilityTargetKind.Pawn)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentTarget.PawnCase(target.Id);
        }
        if (target.Kind == AbilityTargetKind.Thing)
        {
            return new RimGovernor.Brain.GameOrders.Operations.AbilityIntentTarget.ThingCase(target.Id);
        }
        throw BridgeContractException.Contract(
            $"unsupported ability target \"{Domain.Wire.Name(target.Kind)}\""
        );
    }

    /// <summary>The Actions/Apply gathering arm, the organizer starts one vanilla GatheringDef. The intent names no spot; the game chooses it.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action GatheringAction(DomainAction action)
    {
        var gathering = action.Gathering ?? throw BridgeContractException.Contract("not a gathering action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.GatheringIntent { GatheringDef = gathering.Def, Organizer = IntentActionRules.NewRef(gathering.Organizer.Value) } };
    }

    /// <summary>The Actions/Apply conversion arm, the moral guide's Convert ability on one colonist.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ConversionAction(DomainAction action)
    {
        var conversion = action.Conversion ?? throw BridgeContractException.Contract("not a conversion action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.ConversionIntent { GuideId = conversion.Guide.Value, TargetId = conversion.Target.Value } };
    }

    /// <summary>
    /// The arm of one door_control action, the existing combat_orders door order for the
    /// cell, so no new native intent. Native refuses a cell that is no player door as a result in the CombatOrdersEffect
    /// (refusal not_a_door); the door facts the cell came from are read from that same door, so the next review sees a
    /// door still held open when an order did not take.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action DoorControlAction(DomainAction action)
    {
        var door = action.DoorControl ?? throw BridgeContractException.Contract("not a door_control action");
        var mode = door.HoldOpen ? RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.HoldOpen : RimGovernor.Brain.GameOrders.Operations.CombatDoorMode.Close;
        var command = new RimGovernor.Brain.GameOrders.Operations.CombatOrders
        {
            Orders = [new RimGovernor.Brain.GameOrders.Operations.CombatOrder { Order = new RimGovernor.Brain.GameOrders.Operations.CombatOrderOrder.DoorCase(new RimGovernor.Brain.GameOrders.Operations.CombatDoor { Cell = IntentActionRules.WireCell(door.Cell), Mode = mode }) }],
        };
        CombatOrdersRules.ValidateCombatOrders(command);
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = command };
    }

    /// <summary>
    /// The FormCaravanIntent of one caravan departure, the crew, the whole pack by
    /// definition (journey food included) and the destination tile; native checks formation when it applies.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action CaravanDepartureAction(DomainAction action)
    {
        var departure = action.CaravanDeparture ?? throw BridgeContractException.Contract("not a caravan departure action");
        if (departure.Crew.Count == 0 || departure.DestinationTile < 0)
        {
            throw BridgeContractException.Contract("caravan departure requires a crew and a destination");
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent { DestinationTile = departure.DestinationTile, Mode = (RimGovernor.Brain.GameOrders.Operations.CaravanMode)departure.Mode };
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
            intent = intent with { Cargo = [.. intent.Cargo, new RimGovernor.Brain.GameOrders.Operations.DefCount { DefName = item.Definition, Count = (int)item.Count }] };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>
    /// The AssignIntent of one ownership transfer, the pawn, the assignable thing (a bed, a
    /// throne) and the thing of that kind the pawn owned when the planner chose (or none). Native checks the pawn, the
    /// thing and that previous ownership live when it applies; a pawn that already owns the thing applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action AssignAction(DomainAction action)
    {
        var v = action.Assign ?? throw BridgeContractException.Contract("not an assign action");
        var previous = v.Previous.Clear ? new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase() } : new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase(v.Previous.Id) };
        var intent = new RimGovernor.Brain.GameOrders.Operations.AssignIntent { PawnId = v.Pawn.Value, ThingId = v.Thing, ExpectedPrevious = previous, Swap = false };
        if (v.Swap)
        {
            intent = intent with { Swap = true };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    // Each domain method to the order native applies (HusbandryActionHandler).
    private static RimGovernor.Brain.GameOrders.Operations.HusbandryOrder? HusbandryOrderWire(HusbandryMethod method) =>
        method == HusbandryMethod.Train ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Train
        : method == HusbandryMethod.Slaughter ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Slaughter
        : method == HusbandryMethod.Tame ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Tame
        : method == HusbandryMethod.Release ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Release
        : method == HusbandryMethod.AllowedArea ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.AllowedArea
        : method == HusbandryMethod.Master ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Master
        : method == HusbandryMethod.FollowDrafted ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.FollowDrafted
        : method == HusbandryMethod.FollowFieldwork ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.FollowFieldwork
        : method == HusbandryMethod.CancelSlaughter ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.CancelSlaughter
        : method == HusbandryMethod.CancelRelease ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.CancelRelease
        : method == HusbandryMethod.Sterilize ? RimGovernor.Brain.GameOrders.Operations.HusbandryOrder.Sterilize
        : null;

    /// <summary>
    /// The HusbandryIntent of one animal order. Native checks the animal and the order's
    /// eligibility live when it applies; an order that already holds applies again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action HusbandryAction(DomainAction action)
    {
        var v = action.Husbandry ?? throw BridgeContractException.Contract("not a husbandry action");
        if (HusbandryOrderWire(v.Method) is not { } order)
        {
            throw BridgeContractException.Contract("invalid husbandry order");
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.HusbandryIntent { AnimalId = v.Animal.Value, Order = order, Follow = false };
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
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    /// <summary>
    /// The ApparelPolicyIntent of a role apparel policy, the pawn and the complete
    /// filter. Native validates the pawn and the definitions live when it applies.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ApparelPolicyAction(DomainAction action)
    {
        var v = action.ApparelPolicy ?? throw BridgeContractException.Contract("not an apparel policy action");
        var s = v.Spec;
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.ApparelPolicyIntent
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

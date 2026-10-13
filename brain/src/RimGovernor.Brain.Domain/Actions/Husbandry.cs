namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names the direct-write animal management orders: a recursive training request; a slaughter, tame or
/// release-to-wild designation or its cancel; a sterilize surgery bill; or one Animals-tab setting (allowed
/// area, master, follow-drafted, follow-fieldwork). Each is a HusbandryIntent, a direct settings write with no
/// native job, so applied is the effect. Tame targets a wild animal, the others a player animal.
/// </summary>
public enum HusbandryMethod
{
    CancelSlaughter,
    CancelRelease,
    Train,
    Slaughter,
    Tame,
    Release,

    /// <summary>Queues the animal sterilize surgery bill (native finds the recipe on the animal's race live); applied means queued.</summary>
    Sterilize,
    AllowedArea,
    Master,
    FollowDrafted,
    FollowFieldwork,
}

/// <summary>
/// Explicit intent to write one already-observed animal's training request, slaughter/tame/release
/// designation or Animals-tab setting. Argument is the method's one parameter: the trainable def for train;
/// the area or master identity for allowed_area/master (empty clears the assignment); "true"/"false" for the
/// follow flags; empty for the designations. Native checks eligibility at apply, not here.
/// </summary>
public sealed record Husbandry : IActionPayload
{
    public Husbandry(PawnId animal, HusbandryMethod method, string argument)
    {
        DomainException.Require(animal.IsValid, "husbandry requires a valid animal identity");
        switch (method)
        {
            case HusbandryMethod.Train:
                DomainException.Require(Identity.IsValidText(argument), "husbandry training requires a valid trainable definition");
                break;
            case HusbandryMethod.Slaughter:
            case HusbandryMethod.Tame:
            case HusbandryMethod.Release:
            case HusbandryMethod.CancelSlaughter:
            case HusbandryMethod.CancelRelease:
            case HusbandryMethod.Sterilize:
                DomainException.Require(argument == "", "husbandry designation does not take an argument");
                break;
            case HusbandryMethod.AllowedArea:
            case HusbandryMethod.Master:
                DomainException.Require(argument == "" || Identity.IsValidText(argument), "husbandry assignment requires a valid target identity or none");
                break;
            case HusbandryMethod.FollowDrafted:
            case HusbandryMethod.FollowFieldwork:
                DomainException.Require(argument is "true" or "false", "husbandry follow flag requires true or false");
                break;
            default:
                throw new DomainException("invalid husbandry method");
        }
        Animal = animal;
        Method = method;
        Argument = argument;
    }

    public PawnId Animal { get; }

    public HusbandryMethod Method { get; }

    public string Argument { get; }

    /// <summary>The training argument, empty for every other method.</summary>
    public string TrainableDef => Method == HusbandryMethod.Train ? Argument : "";
}

public sealed partial record DomainAction
{
    public static DomainAction NewHusbandryAction(ActionId id, Husbandry husbandry) => Create(id, ActionKind.Husbandry, husbandry);

    public Husbandry? Husbandry => PayloadOf<Husbandry>(ActionKind.Husbandry);
}

using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.GameAccess;

public static class PrisonerCensusRules
{
    /// <summary>
    /// The interaction a population person's open native defName names: a mode the intent exposes, or any other
    /// current interaction (Execution, DLC modes the intent does not expose).
    /// </summary>
    public static PrisonerHeldInteraction PrisonerInteraction(string defName) => defName switch
    {
        "AttemptRecruit" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Recruit),
        "MaintainOnly" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Maintain),
        "ReduceResistance" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.ReduceResistance),
        "Release" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Release),
        "Enslave" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Enslave),
        "Convert" => new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.Convert),
        _ => new PrisonerHeldInteraction.Other(),
    };
}

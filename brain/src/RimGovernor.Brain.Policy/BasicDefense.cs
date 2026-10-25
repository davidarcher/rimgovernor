namespace RimGovernor.Brain.Policy;


public static class BasicDefenseRules
{
    /// <summary>
    /// The Foothold defense gate (two armed, no hostiles) plus every fighting-capable colonist holding a
    /// weapon: a club left on the ground while a colonist stands unarmed keeps military/defense unmet: arming
    /// them, or crafting a weapon when none lies loose, is held until the forecast supports it. The Foothold gate itself stays at two
    /// armed; an unknown unarmed count defers to it.
    /// </summary>
    internal static Fact<bool> BasicDefenseRecovered(Fact<bool> gate, long unarmed)
    {
        if (unarmed > 0 && gate.IsKnown)
        {
            return Known(false);
        }

        return gate;
    }
}

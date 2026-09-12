package bridge

import o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

func validateMoodNeeds(v *o.PawnNeeds) error {
	if err := pawnsIssues(v.Issues, v.ProtoReflect()); err != nil {
		return err
	}
	for _, n := range []*float64{v.Food, v.Rest, v.Mood, v.Joy, v.BreakThresholdMinor, v.BreakThresholdMajor, v.BreakThresholdExtreme} {
		if !combatNumber(n, false) {
			return contract("nonfinite pawn need")
		}
	}
	for _, s := range []*string{v.HungerCategory, v.BreakRisk} {
		if s != nil && !presentationText(s, 256) {
			return contract("invalid pawn need category")
		}
	}
	return nil
}

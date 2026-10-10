package bridge

import (
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func validateMoodNeeds(v *o.PawnNeeds) error {
	if err := pawnsIssues(v.Issues, v.ProtoReflect()); err != nil {
		return err
	}
	for _, n := range []*float64{v.Food, v.Rest, v.Mood, v.Joy, v.BreakThresholdMinor, v.BreakThresholdMajor, v.BreakThresholdExtreme} {
		if !combatNumber(n, false) {
			return contract("nonfinite pawn need")
		}
	}
	if v.HungerCategory != nil && d.HungerCategory_name[int32(v.GetHungerCategory())] == "" || v.BreakRisk != nil && o.BreakRisk_name[int32(v.GetBreakRisk())] == "" {
		return contract("invalid pawn need category")
	}
	// Psyfocus: all three or none (no Royalty, no psylink).
	if (v.Psyfocus == nil) != (v.PsyfocusTarget == nil) || (v.Psyfocus == nil) != (v.PsylinkLevel == nil) {
		return contract("partial psyfocus facts")
	}
	if v.Psyfocus != nil && (!unitFraction(v.GetPsyfocus()) || !unitFraction(v.GetPsyfocusTarget()) || v.GetPsylinkLevel() < 1) {
		return contract("invalid psyfocus facts")
	}
	return nil
}

func unitFraction(f float64) bool { return f >= 0 && f <= 1 }

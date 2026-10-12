package bridge

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// moodModel is the mood price's catalog input (#2954): the break curve built
// from the MentalBreakDef and MentalStateDef rows, and the stages of the
// thoughts the planner prices (the dark, the role thoughts). A catalog without
// the threshold stat (a hand-built one) has an unknown model, not an error.
//
// A break counts in its tier when it is generic: not anomalous and not gated on
// a trait, gene or precept. Its weight is baseCommonality and its length the
// mean days its state lasts (policy.StateDays), or for a hediff break the mean
// of the hediff's disappear range. A break the game gives no length for
// (policy.UnpricedBreak, or a modded one with neither a state nor a hediff) is
// left out, which makes the cost a lower bound.
func (catalog *DefinitionCatalog) moodModel() (policy.MoodModel, error) {
	game := policy.MoodGame()
	stat := DefRow[*d.StatDef](catalog, game.ThresholdStat)
	if stat == nil {
		return policy.MoodModel{}, nil
	}
	model := policy.MoodModel{Break: policy.MoodBreakModel{Threshold: float32Number(stat.GetDefaultBaseValue())}, Stages: map[string][]policy.MoodStage{}}
	names := make([]string, 0)
	for name := range catalogDefs[*d.MentalBreakDef](catalog) {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		row := DefRow[*d.MentalBreakDef](catalog, name)
		if row.GetAnomalousBreak() || row.GetRequiredTrait() != "" || row.GetRequiredGene() != "" || row.GetRequiredPrecept() != "" || policy.UnpricedBreak(name) {
			continue
		}
		var tier policy.MoodTier
		switch row.GetIntensity() {
		case d.MentalBreakIntensity_MENTAL_BREAK_INTENSITY_MINOR:
			tier = policy.TierMinor
		case d.MentalBreakIntensity_MENTAL_BREAK_INTENSITY_MAJOR:
			tier = policy.TierMajor
		case d.MentalBreakIntensity_MENTAL_BREAK_INTENSITY_EXTREME:
			tier = policy.TierExtreme
		default:
			continue
		}
		days, ok := catalog.breakDays(name, row, game)
		if !ok {
			continue
		}
		model.Break.Tiers[tier] = model.Break.Tiers[tier].Add(float64(row.GetBaseCommonality()), days)
	}
	thoughts := []string{game.DarkThought}
	for _, thought := range game.RoleThoughts {
		thoughts = append(thoughts, thought)
	}
	for _, name := range thoughts {
		if facts, ok := catalog.ThoughtFacts(name); ok {
			model.Stages[name] = facts.Stages
		}
	}
	return model, nil
}

// breakDays is the mean days a break takes its colonist out of work.
func (catalog *DefinitionCatalog) breakDays(name string, row *d.MentalBreakDef, game policy.MoodGameTable) (float64, bool) {
	if state := DefRow[*d.MentalStateDef](catalog, row.GetMentalState()); state != nil {
		return policy.StateDays(float64(state.GetMinTicksBeforeRecovery()), float64(state.GetMaxTicksBeforeRecovery()), float64(state.GetRecoveryMtbDays())), true
	}
	hediff := DefRow[*d.HediffDef](catalog, game.HediffBreaks[name])
	if hediff == nil {
		return 0, false
	}
	for _, comp := range hediff.GetComps() {
		if gone := comp.GetValue().GetHediffCompProperties_Disappears(); gone != nil {
			ticks := gone.GetDisappearsAfterTicks()
			return float64(ticks.GetMin()+ticks.GetMax()) / 2 / domain.TicksPerDay, true
		}
	}
	return 0, false
}

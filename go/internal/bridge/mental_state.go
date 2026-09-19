package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// PawnMentalState preserves absence from older producers as unknown. A known
// value describes an active state; it never invents a state from mood alone.
func PawnMentalState(row *o.PawnState) (domain.Fact[policy.MentalState], error) {
	unknown := domain.Unknown[policy.MentalState]()
	if row == nil {
		return unknown, nil
	}
	if err := emergencyIssues(row.Issues, func(field string) bool {
		switch field {
		case "mental_state":
			return row.MentalState != nil
		case "mental_state_is_aggro":
			return row.MentalStateIsAggro != nil
		case "mental_state_ticks":
			return row.MentalStateTicks != nil
		}
		return false
	}); err != nil {
		return unknown, err
	}
	if row.MentalState != nil && validID(row.GetMentalState()) != nil {
		return unknown, contract("invalid mental state defName")
	}
	if row.MentalStateTicks != nil && row.GetMentalStateTicks() < 0 {
		return unknown, contract("negative mental state age")
	}
	if row.MentalState == nil || row.MentalStateIsAggro == nil || row.MentalStateTicks == nil {
		return unknown, nil
	}
	return domain.Known(policy.MentalState{DefName: row.GetMentalState(), IsAggro: row.GetMentalStateIsAggro(), TicksInState: row.GetMentalStateTicks()}), nil
}

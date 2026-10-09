package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// rulesWindow reads the latest attachment, including retired methods. A queued
// intent or missing receipt cannot establish a live native lease.
func (s *ClockScheduler) rulesWindow(ctx context.Context, current domain.GenerationSnapshot, tick domain.Tick, maxTicks uint32) (uint32, error) {
	if s.config.Rules == nil {
		return maxTicks, nil
	}
	plans, err := s.player.journal.PlanHistoryWithMethods(ctx, 1, "rules-*")
	if err != nil {
		return 0, err
	}
	lease := domain.Unknown[policy.RuleLease]()
	if len(plans) == 1 {
		for _, progress := range plans[0].Progress {
			attach, ok := progress.Action().RulesAttach()
			view := progress.View()
			receipt, known := view.Receipt.Value()
			if ok && len(attach.Rules()) > 0 && view.Stage == domain.Completed && known && receipt == domain.ReceiptAccepted && view.Snapshot.SameWorld(current) && view.Snapshot.Native == current.Native {
				lease = domain.Known(policy.RuleLease{Dispatched: view.Tick, Ticks: attach.LeaseTicks()})
			}
		}
	}
	return policy.RulesWindowTicks(tick, maxTicks, lease), nil
}

// Each rules method is keyed by its review tick, so only its admitting step
// yields to Hands. A retry, unknown census or failed receipt cannot park time.
func rulesDispatchTurn(worker bool, rules *RoundsRulesResult) bool {
	return worker && rules != nil && rules.Verdict == BuildingReasonAdmitted
}

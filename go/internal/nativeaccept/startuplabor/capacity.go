package startuplabor

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// ContinuationBudget is how long a review may leave a distinct unused
// worker beside runnable ready work without admitting anything or naming
// an enforced constraint (#655): three labor-idle release periods (2,500
// ticks each), enough for a review, a planner step and a yield to land.
const ContinuationBudget domain.Tick = 7500

// CapacitySample is one automatic-mode review as the development record
// and the ready-work projection filed it.
type CapacitySample struct {
	Tick domain.Tick
	// Unused is the census's unmatched distinct workers; unknown when the
	// review had no census.
	Unused domain.Fact[int]
	// Runnable is the ready-work projection's runnable candidate count.
	Runnable int
	// Committed is the number of committed development goals.
	Committed int
	// Limiting is the reason the review gave for admitting no more.
	Limiting policy.DevelopmentReason
}

// Stranding is a stretch over which unused workers and runnable work
// coexisted with no admission and no enforced constraint.
type Stranding struct {
	From, To domain.Tick
	Unused   int
	Runnable int
	Limiting policy.DevelopmentReason
}

// enforced are the limiting reasons that are real constraints, not slack:
// an unknown or absent workforce, the stage hold, risk, labor genuinely
// taken, open work beyond the census, and player or emergency control.
// capacity_committed with a worker unused is exactly what automatic mode
// must not hide behind, so it is not listed.
var enforced = map[policy.DevelopmentReason]bool{
	policy.DevelopmentWorkersUnknown: true, policy.DevelopmentNoWorkers: true,
	policy.DevelopmentStage: true, policy.DevelopmentRisk: true,
	policy.DevelopmentLabor: true, policy.DevelopmentOvercommitted: true,
	policy.DevelopmentDisabled: true, policy.DevelopmentEmergency: true,
}

// Strandings returns every stretch longer than budget during which each
// review saw an unused worker and a runnable candidate, the committed
// count never grew and no enforced constraint was named. Samples must be
// in review order; a rewind ends the current stretch.
func Strandings(samples []CapacitySample, budget domain.Tick) []Stranding {
	if budget <= 0 {
		budget = ContinuationBudget
	}
	var (
		out    []Stranding
		open   *Stranding
		before = -1
	)
	closeOpen := func() {
		if open != nil && open.To-open.From > budget {
			out = append(out, *open)
		}
		open = nil
	}
	for i, s := range samples {
		if i > 0 && s.Tick < samples[i-1].Tick {
			closeOpen()
			before = -1
		}
		unused, known := s.Unused.Value()
		admitted := before >= 0 && s.Committed > before
		before = s.Committed
		if !known || unused == 0 || s.Runnable == 0 || enforced[s.Limiting] || admitted {
			closeOpen()
			continue
		}
		if open == nil {
			open = &Stranding{From: s.Tick}
		}
		open.To, open.Unused, open.Runnable, open.Limiting = s.Tick, unused, s.Runnable, s.Limiting
	}
	closeOpen()
	return out
}

// Row renders a stranding for the report artifact.
func (s Stranding) Row() map[string]any {
	return map[string]any{"from": s.From, "to": s.To, "unused": s.Unused, "runnable": s.Runnable, "limiting": string(s.Limiting)}
}

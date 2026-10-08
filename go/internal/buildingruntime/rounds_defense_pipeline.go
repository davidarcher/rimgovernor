package buildingruntime

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// defensePipeline is the perimeter tiers one layout step keeps in flight: the
// ones already open from earlier steps and the ones this step admits. A tier
// joins them while the stock funds it (the ledger) and it claims none of their
// cells. Native frame completion preserves construction access (#2314).
type defensePipeline struct {
	open    []domain.MethodID
	cells   map[domain.Cell]bool
	unbuilt []domain.Building
	ledger  *fundingLedger
	merged  bool
	// claimed is whether the open tiers' remaining cost is charged to the ledger.
	claimed  bool
	admitted int
	// scope is the step's tick read, taken once: the player gate holds the clock for the whole step.
	scope  observation.Identity
	scoped bool
}

func newDefensePipeline(ledger *fundingLedger) *defensePipeline {
	return &defensePipeline{cells: map[domain.Cell]bool{}, ledger: ledger}
}

// piped is whether another tier is already in flight.
func (p *defensePipeline) piped() bool { return len(p.open) > 0 || p.admitted > 0 }

// overlaps is whether any building stands on a claimed cell.
func (p *defensePipeline) overlaps(buildings []domain.Building) bool {
	for _, b := range buildings {
		if p.cells[b.Cell()] {
			return true
		}
	}
	return false
}

func (p *defensePipeline) add(buildings []domain.Building) {
	for _, b := range buildings {
		p.cells[b.Cell()] = true
	}
}

// settle drops the open tiers' buildings the census sees standing.
func (p *defensePipeline) settle(census *defenseCensus) {
	kept := p.unbuilt[:0]
	for _, b := range p.unbuilt {
		if !census.standing(b.Definition(), b.Cell()) {
			kept = append(kept, b)
		}
	}
	p.unbuilt = kept
}

// claimOpen charges the open tiers' unbuilt buildings to the ledger once, at
// the prices the tier being admitted has just read; a kind not priced yet is
// not charged. A stock reading that already nets the native blueprints and
// frames (NativeConstruction) holds their cost out of Available itself.
func (p *defensePipeline) claimOpen() {
	if p.claimed || p.ledger.stock.NativeConstruction {
		return
	}
	p.claimed = true
	for _, b := range p.unbuilt {
		if costs, known := p.ledger.priceOf(b.Definition() + "/" + b.Stuff()); known {
			p.ledger.claim(costs)
		}
	}
}

// defenseTierOpen is whether one of the open methods is the tier's: the
// tier's prefix followed by an attempt number, or a repair and an attempt
// (defenseTierMethodID).
func defenseTierOpen(open []domain.MethodID, tier policy.DefenseTierName) bool {
	prefix := defenseTierPrefix(tier)
	for _, method := range open {
		rest, ok := strings.CutPrefix(string(method), prefix)
		if !ok {
			continue
		}
		if repair, ok := strings.CutPrefix(rest, "r"); ok {
			if _, attempt, found := strings.Cut(repair, "-"); found {
				rest = attempt
			}
		}
		if rest != "" && strings.Trim(rest, "0123456789") == "" {
			return true
		}
	}
	return false
}

// defenseOpenTiers reads the goal's open plans. ok is false when one of them
// is not a pure-construction perimeter tier (a turret, a rearm, a wall
// removal): the layout then waits on it as it always did.
func defenseOpenTiers(goal store.ProjectState, plans func(domain.PlanID) (store.PlanState, error)) (pipe *defensePipeline, ok bool, err error) {
	pipe = newDefensePipeline(nil)
	for _, method := range goal.Methods {
		plan, err := plans(method.Plan)
		if err != nil {
			return nil, false, err
		}
		if !store.PlanOpen(plan) {
			continue
		}
		if !strings.HasPrefix(string(method.Method), "defense-"+policy.TierPerimeterPrefix) {
			return nil, false, nil
		}
		for _, action := range plan.Spec.Actions() {
			b, building := action.Building()
			if !building {
				return nil, false, nil
			}
			pipe.cells[b.Cell()] = true
			pipe.unbuilt = append(pipe.unbuilt, b)
		}
		pipe.open = append(pipe.open, method.Method)
	}
	return pipe, true, nil
}

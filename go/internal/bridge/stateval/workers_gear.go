package stateval

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The gear-group StatWorkers (epic #2621, #2639). StatWorker_MarketValue is in
// workers_gear_price.go, the melee workers in workers_gear_melee.go.

func gearWorkerList() []Worker {
	return []Worker{
		workerMarketValue{}, workerMeleeArmorPenetration{}, workerMeleeAverageArmorPenetration{},
		workerMeleeAverageDPS{}, workerMeleeDPS{}, workerPossibleCompOffsets{}, workerShootingAccuracy{},
	}
}

// workerShootingAccuracy is StatWorker_ShootingAccuracy: it overrides only
// GetExplanationFinalizePart (the report's example distances), so its value
// and visibility are the base worker's.
type workerShootingAccuracy struct{}

func (workerShootingAccuracy) Class() string { return "StatWorker_ShootingAccuracy" }

// workerPossibleCompOffsets is StatWorker_PossibleCompOffsets: the base value
// plus the thing's CompStatOffsetBase offset when that comp offsets this stat.
// Its other overrides only write the report.
type workerPossibleCompOffsets struct{}

func (workerPossibleCompOffsets) Class() string { return "StatWorker_PossibleCompOffsets" }

func (workerPossibleCompOffsets) Unfinalized(req *Request) (float32, error) {
	num, err := req.Evaluator.baseUnfinalized(req)
	if err != nil {
		return 0, err
	}
	g := gearFacts(req)
	if g == nil {
		return num, nil
	}
	comp, err := need(g.StatOffsetComp, "the thing's stat offset comp")
	if err != nil || comp == nil || comp.StatDef != req.Stat.GetDefName() {
		return num, err
	}
	return float32(num + comp.Offset), nil
}

// unfinalized is the stat's worker GetValueUnfinalized: the override, else the
// base worker's.
func (e *Evaluator) unfinalized(req *Request) (float32, error) {
	w, err := e.worker(req.Stat, "the worker's value computation")
	if err != nil {
		return 0, err
	}
	if uw, ok := w.(unfinalizedWorker); ok {
		return uw.Unfinalized(req)
	}
	return e.baseUnfinalized(req)
}

// statDef is the catalog's StatDef row, or an error.
func (e *Evaluator) statDef(name string) (*d.StatDef, error) {
	row := bridge.DefRow[*d.StatDef](e.catalog, name)
	if row == nil {
		return nil, fmt.Errorf("catalog has no stat def %s", name)
	}
	return row, nil
}

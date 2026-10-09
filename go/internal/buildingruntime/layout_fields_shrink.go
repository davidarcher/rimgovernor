package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// fieldTargets is each crop's full field target in cells: a food
// crop's FieldTarget for the colony, haygrass for the pens' whole need,
// and the fixed social ceiling. A crop whose target is unknown is absent
// and never shrinks.
func fieldTargets(projection observation.ColonyProjection, field policy.FieldRequest) map[string]int {
	out := map[string]int{}
	for _, crop := range field.Choices {
		if edible, known := crop.Edible.Value(); !known || !edible {
			continue
		}
		if n, known := policy.FieldTarget(field.Colonists, crop, field.ReserveDays, field.Climate.DaysRemaining).Value(); known {
			out[crop.Name] = n
		}
		// Growth runs to the capacity target, so shrink waits past it.
		if n, known := policy.FieldCapacityTarget(field.Colonists, crop, field.ReserveDays).Value(); known {
			out[crop.Name] = max(out[crop.Name], n)
		}
	}
	if name, n, ok := hayTarget(projection); ok {
		out[name] = n
	}
	for _, crop := range socialCrops(projection) {
		out[crop.Name] = policy.SocialCropCells
	}
	return out
}

// shrink gives up one surplus zone's bare cells with remove-cells,
// committed directly like a grow. handled reports a committed edit.
func (r *RoundsFieldPlanner) shrink(call, epoch context.Context, state ControlState, goal store.StandardState, projection observation.ColonyProjection, read observation.RoundsReading, field policy.FieldRequest) (RoundsFieldResult, bool, error) {
	plan, ok := planFieldShrink(projection, fieldTargets(projection, field))
	if !ok {
		return RoundsFieldResult{}, false, nil
	}
	cells := shrinkCells(plan, bareCells(projection.Cells, plan.Order))
	if len(cells) == 0 {
		return RoundsFieldResult{}, false, nil
	}
	p := r.reviewer.player
	hash := sha256.New()
	fmt.Fprintf(hash, "shrink/%s/%v", plan.ID, cells)
	method := domain.MethodID(fmt.Sprintf("fields-%x", hash.Sum(nil)[:16]))
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsFieldResult{}, false, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsFieldResult{}, false, err
	}
	id := domain.MintPlanID()
	value, err := domain.NewZoneCellEdit(plan.ID, domain.RemoveZoneCells, cells)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	action, err := domain.NewZoneCellEditAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	committed, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoundsFieldResult{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoundsFieldResult{}, false, err
	}
	now := r.reviewer.clock.Now()
	if p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge {
		return RoundsFieldResult{}, false, fmt.Errorf("%w: shrink: stale state or read", ErrControl)
	}
	if _, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, committed); err != nil {
		return RoundsFieldResult{}, false, err
	}
	clockEvent(call, "layout", "fields", "field block shrunk", "zone", plan.ID, "crop", plan.Crop, "cells", len(cells), "plan", string(id))
	return RoundsFieldResult{Verdict: BuildingReasonAdmitted, Plan: id}, true, nil
}

// Field shrink hysteresis: a crop's growing zones shrink only once
// their cells exceed its full target by fieldShrinkTrigger, and then only
// down to fieldShrinkFloor of the target, so a small dip in demand never
// flips a zone between growing and shrinking.
const (
	fieldShrinkTrigger = 1.25
	fieldShrinkFloor   = 1.10
)

// fieldShrink is one zone's surplus: Order is the zone's cells in removal
// order (outermost from its centre first), Zone its cells, and Remove how
// many to give up.
type fieldShrink struct {
	ID, Crop string
	Zone     map[domain.Cell]bool
	Order    []domain.Cell
	Remove   int
}

// planFieldShrink finds the first crop (by name) whose standing growing
// zones exceed its full target in cells past the hysteresis band, and
// returns its largest zone. Crops with no positive target never shrink, and
// a zone never gives up its last cell. Other zones never move.
func planFieldShrink(facts observation.ColonyProjection, targets map[string]int) (fieldShrink, bool) {
	growing := map[string]string{}
	for _, f := range facts.Farms {
		growing[f.ID] = f.Crop
	}
	zones := map[string]map[domain.Cell]bool{}
	for _, c := range facts.Cells {
		if id, ok := c.ZoneID.Value(); ok && id != "" {
			if _, farm := growing[id]; farm {
				if zones[id] == nil {
					zones[id] = map[domain.Cell]bool{}
				}
				zones[id][c.Cell] = true
			}
		}
	}
	total := map[string]int{}
	largest := map[string]string{}
	for id, cells := range zones {
		crop := growing[id]
		total[crop] += len(cells)
		if l := largest[crop]; l == "" || len(cells) > len(zones[l]) || len(cells) == len(zones[l]) && id < l {
			largest[crop] = id
		}
	}
	crops := make([]string, 0, len(targets))
	for crop := range targets {
		crops = append(crops, crop)
	}
	sort.Strings(crops)
	for _, crop := range crops {
		target := targets[crop]
		if target <= 0 || float64(total[crop]) <= float64(target)*fieldShrinkTrigger {
			continue
		}
		keep := int(math.Ceil(float64(target) * fieldShrinkFloor))
		id := largest[crop]
		cells := zones[id]
		remove := min(total[crop]-keep, len(cells)-1)
		if remove <= 0 {
			continue
		}
		var sx, sz int64
		for c := range cells {
			sx += int64(c.X)
			sz += int64(c.Z)
		}
		n := int64(len(cells))
		centre := domain.Cell{X: int32(sx / n), Z: int32(sz / n)}
		order := sortedCells(cells)
		sort.SliceStable(order, func(i, j int) bool { return cellDist(order[i], centre) > cellDist(order[j], centre) })
		return fieldShrink{ID: id, Crop: crop, Zone: cells, Order: order, Remove: remove}, true
	}
	return fieldShrink{}, false
}

// bareCells are the cells of order the mirror holds with no plant standing on
// them: unsown or harvested soil. A fogged cell is not held,
// so never bare.
func bareCells(held []policy.SiteCell, order []domain.Cell) map[domain.Cell]bool {
	want := make(map[domain.Cell]bool, len(order))
	for _, cell := range order {
		want[cell] = true
	}
	bare := map[domain.Cell]bool{}
	for _, cell := range held {
		if !want[cell.Cell] {
			continue
		}
		bare[cell.Cell] = !slices.ContainsFunc(cell.Things, func(t policy.Thing) bool { return t.Category == policy.ThingPlant })
	}
	return bare
}

// shrinkCells takes up to s.Remove of the zone's bare (unsown or harvested)
// cells, outermost first, skipping any whose removal would split what
// remains: native refuses a disconnected zone.
func shrinkCells(s fieldShrink, bare map[domain.Cell]bool) []domain.Cell {
	left := make(map[domain.Cell]bool, len(s.Zone))
	for c := range s.Zone {
		left[c] = true
	}
	var out []domain.Cell
	for _, c := range s.Order {
		if len(out) >= s.Remove || len(left) <= 1 {
			break
		}
		if !bare[c] {
			continue
		}
		delete(left, c)
		var start domain.Cell
		for k := range left {
			start = k
			break
		}
		if len(cellComponent(left, start)) != len(left) {
			left[c] = true
			continue
		}
		out = append(out, c)
	}
	sortCells(out)
	return out
}

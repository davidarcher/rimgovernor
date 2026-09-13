package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	MaintainFireSafety       GoalID = "MaintainFireSafety"
	SecureSupplies           GoalID = "SecureSupplies"
	MaintainEssentialRepairs GoalID = "MaintainEssentialRepairs"
	MaintainCleanFacilities  GoalID = "MaintainCleanFacilities"
)

// Each fact is a complete native section. An unavailable section cannot prove
// that its old targets disappeared, while a known empty section can.
type UpkeepObservation struct {
	Items      domain.Fact[[]UpkeepItem]
	Structures domain.Fact[[]UpkeepStructure]
	Fires      domain.Fact[[]UpkeepFire]
	Filth      domain.Fact[[]UpkeepFilth]
}
type UpkeepItem struct {
	ID                           string
	Definition                   string
	Cell                         domain.Cell
	Roofed, InStorage, Forbidden bool
	Deterioration                float64
	Medicine                     bool
	RotTicks                     domain.Fact[int64]
	Count                        int64
}
type UpkeepStructure struct {
	ID                      string
	Home                    bool
	HitPoints, MaxHitPoints int64
	Priority                int
}
type UpkeepFire struct {
	ID   string
	Home bool
	Size domain.Fact[float64]
}
type UpkeepFilth struct {
	ID        string
	Home      bool
	Room      string
	Thickness uint32
}
type UpkeepHistory struct{ Fire, Supplies, Repairs, Cleaning bool }
type UpkeepNeed struct {
	Goal     GoalID
	Priority int
	Active   bool
	Targets  domain.Fact[[]string]
	Metric   domain.Fact[float64]
	Unsafe   bool
}
type UpkeepReview struct {
	History UpkeepHistory
	Needs   []UpkeepNeed
}

// ReviewUpkeep ports the four direct native upkeep contracts. Issued work is
// supplied by the shared journal, never inferred from a receipt or target loss.
func ReviewUpkeep(v UpkeepObservation, previous UpkeepHistory, issued map[GoalID]bool) (UpkeepReview, error) {
	r := UpkeepReview{}
	add := func(goal GoalID, priority int, active bool, targets domain.Fact[[]string], metric domain.Fact[float64], unsafe bool) bool {
		if rows, known := targets.Value(); known {
			active = len(rows) > 0
		}
		active = active || issued[goal]
		if _, known := targets.Value(); !known && !active {
			priority = 4
		}
		r.Needs = append(r.Needs, UpkeepNeed{goal, priority, active, targets, metric, unsafe})
		return active
	}
	ids := func(n int) (map[string]bool, error) {
		if n > 256 {
			return nil, errors.New("upkeep section exceeds bound")
		}
		return map[string]bool{}, nil
	}
	valid := func(seen map[string]bool, id string) bool {
		if !foodID(id) || seen[id] {
			return false
		}
		seen[id] = true
		return true
	}
	targets, metric := domain.Unknown[[]string](), domain.Unknown[float64]()
	unsafe := false
	if rows, known := v.Fires.Value(); known {
		seen, err := ids(len(rows))
		if err != nil {
			return r, err
		}
		selected := []string{}
		total := 0.0
		measured := true
		for _, row := range rows {
			size, known := row.Size.Value()
			if !valid(seen, row.ID) || known && (!foodNumber(size) || size < 0) {
				return r, errors.New("invalid upkeep fire")
			}
			if !row.Home {
				continue
			}
			selected = append(selected, row.ID)
			total += size
			measured = measured && known
			unsafe = unsafe || !known || size > 1
		}
		sort.Strings(selected)
		unsafe = unsafe || len(selected) > 3
		targets = domain.Known(selected)
		if measured {
			metric = domain.Known(total)
		}
	}
	r.History.Fire = add(MaintainFireSafety, 1, previous.Fire, targets, metric, unsafe)
	targets, metric = domain.Unknown[[]string](), domain.Unknown[float64]()
	if rows, known := v.Items.Value(); known {
		seen, err := ids(len(rows))
		if err != nil {
			return r, err
		}
		selected := []UpkeepItem{}
		for _, row := range rows {
			rot, known := row.RotTicks.Value()
			if !valid(seen, row.ID) || !foodID(row.Definition) || row.Cell.X < 0 || row.Cell.Z < 0 || !foodNumber(row.Deterioration) || row.Deterioration < 0 || row.Count < 0 || known && rot < 0 {
				return r, errors.New("invalid upkeep item")
			}
			if row.Deterioration > 0 && (!row.Roofed || !row.InStorage) && !row.Forbidden {
				selected = append(selected, row)
			}
		}
		sort.Slice(selected, func(i, j int) bool {
			a, b := selected[i], selected[j]
			if a.Medicine != b.Medicine {
				return a.Medicine
			}
			x, xk := a.RotTicks.Value()
			y, yk := b.RotTicks.Value()
			if xk != yk {
				return xk
			}
			if xk && x != y {
				return x < y
			}
			return a.ID < b.ID
		})
		result := []string{}
		total := 0.0
		for _, row := range selected {
			result = append(result, row.ID)
			total += float64(row.Count)
		}
		targets, metric = domain.Known(result), domain.Known(total)
	}
	r.History.Supplies = add(SecureSupplies, 3, previous.Supplies, targets, metric, false)
	targets, metric = domain.Unknown[[]string](), domain.Unknown[float64]()
	if rows, known := v.Structures.Value(); known {
		seen, err := ids(len(rows))
		if err != nil {
			return r, err
		}
		selected := []UpkeepStructure{}
		for _, row := range rows {
			if !valid(seen, row.ID) || row.HitPoints < 0 || row.MaxHitPoints < row.HitPoints || row.Priority < 0 || row.Priority > 2 {
				return r, errors.New("invalid upkeep structure")
			}
			if row.Home && row.MaxHitPoints > 0 && row.HitPoints < row.MaxHitPoints {
				selected = append(selected, row)
			}
		}
		sort.Slice(selected, func(i, j int) bool {
			a, b := selected[i], selected[j]
			if a.Priority != b.Priority {
				return a.Priority < b.Priority
			}
			x, y := float64(a.HitPoints)/float64(a.MaxHitPoints), float64(b.HitPoints)/float64(b.MaxHitPoints)
			if x != y {
				return x < y
			}
			return a.ID < b.ID
		})
		result := []string{}
		total := 0.0
		for _, row := range selected {
			result = append(result, row.ID)
			total += float64(row.MaxHitPoints - row.HitPoints)
		}
		targets, metric = domain.Known(result), domain.Known(total)
	}
	r.History.Repairs = add(MaintainEssentialRepairs, 3, previous.Repairs, targets, metric, false)
	targets, metric = domain.Unknown[[]string](), domain.Unknown[float64]()
	if rows, known := v.Filth.Value(); known {
		seen, err := ids(len(rows))
		if err != nil {
			return r, err
		}
		selected := []UpkeepFilth{}
		for _, row := range rows {
			if !valid(seen, row.ID) || len(row.Room) > 256 {
				return r, errors.New("invalid upkeep filth")
			}
			if row.Home {
				selected = append(selected, row)
			}
		}
		critical := func(s string) bool { return s == "Kitchen" || s == "Hospital" || s == "Laboratory" }
		sort.Slice(selected, func(i, j int) bool {
			a, b := selected[i], selected[j]
			if critical(a.Room) != critical(b.Room) {
				return critical(a.Room)
			}
			return a.ID < b.ID
		})
		result := []string{}
		total := 0.0
		for _, row := range selected {
			result = append(result, row.ID)
			total += float64(row.Thickness)
		}
		targets, metric = domain.Known(result), domain.Known(total)
	}
	r.History.Cleaning = add(MaintainCleanFacilities, 3, previous.Cleaning, targets, metric, false)
	for _, n := range r.Needs {
		if x, k := n.Metric.Value(); k && (math.IsNaN(x) || math.IsInf(x, 0)) {
			return r, errors.New("upkeep metric overflow")
		}
	}
	return r, nil
}

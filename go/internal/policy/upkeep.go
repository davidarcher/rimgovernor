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
	// MaintainStorage is the ordinary (non-decaying) counterpart to
	// SecureSupplies: loose items sitting outside storage with zero
	// deterioration -- typically fresh production output waiting to reach
	// EnsureFoodStorage's stockpile -- rather than items already at risk.
	// Its own UpkeepItem selection is deliberately disjoint from
	// SecureSupplies' (Deterioration == 0 here, > 0 there), so the two never
	// compete over the same real-world item.
	MaintainStorage GoalID = "MaintainStorage"
)

// Each fact is a complete native section. An unavailable section cannot prove
// that its old targets disappeared, while a known empty section can.
type UpkeepObservation struct {
	Clearance domain.Fact[[]ClearanceTarget]
	// Chunks are the same read's rock and slag stacks in Home; a pending one
	// (no store will take it) is a clearance deficit until a dump exists.
	Chunks     domain.Fact[[]ClearanceChunk]
	Items      domain.Fact[[]UpkeepItem]
	Structures domain.Fact[[]UpkeepStructure]
	Fires      domain.Fact[[]UpkeepFire]
	Filth      domain.Fact[[]UpkeepFilth]
	// Rooms, CleaningWorkers and Tick feed the bounded cleaning response
	// (ReviewCleanliness): the measured room census, the count of workers
	// with Cleaning enabled, and the review tick the dirty-room latch is
	// aged against. An unknown census keeps the previous latch.
	Rooms           domain.Fact[RoomObservation]
	CleaningWorkers domain.Fact[int]
	Tick            domain.Tick
	// Lighting is the measured work-cell illumination census MaintainLighting
	// reviews (see lighting.go); unknown when native could not read it.
	Lighting domain.Fact[LightingObservation]
	// Flooring is the measured room terrain census MaintainFlooring reviews
	// (see flooring.go); unknown when native could not read it.
	Flooring domain.Fact[FlooringObservation]
	// Routes is the measured facility reachability and traffic census
	// MaintainRoutes reviews (issue #6 slice 5).
	Routes domain.Fact[RoutesObservation]
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
	Cell                    domain.Cell
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
	ID         string
	Definition string
	Cell       domain.Cell
	Home       bool
	// Room is the RoomRoleDef name; RoomID the room census identity the
	// filth lies in (unknown outdoors or before the native field existed).
	Room      string
	RoomID    domain.Fact[string]
	Thickness uint32
}
type UpkeepHistory struct {
	Clearance                                  bool `json:",omitempty"`
	Fire, Supplies, Repairs, Cleaning, Storage bool
	// DirtyRooms is MaintainCleanFacilities' per-room latch (see
	// ReviewCleanliness); empty for a clean colony.
	DirtyRooms []DirtyRoom `json:",omitempty"`
}
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

// ReviewUpkeep ports the five direct native upkeep contracts. Issued work is
// supplied by the shared journal, never inferred from a receipt or target loss.
// Cleaning uses the default CleanlinessPolicy; ReviewUpkeepWith takes one.
func ReviewUpkeep(v UpkeepObservation, previous UpkeepHistory, issued map[GoalID]bool) (UpkeepReview, error) {
	return ReviewUpkeepWith(v, previous, issued, DefaultCleanlinessPolicy())
}

func ReviewUpkeepWith(v UpkeepObservation, previous UpkeepHistory, issued map[GoalID]bool, cleanliness CleanlinessPolicy) (UpkeepReview, error) {
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
	storageTargets, storageMetric := domain.Unknown[[]string](), domain.Unknown[float64]()
	if rows, known := v.Items.Value(); known {
		seen, err := ids(len(rows))
		if err != nil {
			return r, err
		}
		selected := []UpkeepItem{}
		storageSelected := []UpkeepItem{}
		for _, row := range rows {
			rot, known := row.RotTicks.Value()
			if !valid(seen, row.ID) || !foodID(row.Definition) || row.Cell.X < 0 || row.Cell.Z < 0 || !foodNumber(row.Deterioration) || row.Deterioration < 0 || row.Count < 0 || known && rot < 0 {
				return r, errors.New("invalid upkeep item")
			}
			// A deteriorating stack needs a roof as well as legal storage;
			// an ordinary stack is stored once legal storage holds it. An
			// unroofed stockpile is common early on, and hauling cannot
			// move a stored stack anywhere better (#189).
			switch {
			case row.Forbidden:
			case row.Deterioration > 0 && (!row.Roofed || !row.InStorage):
				selected = append(selected, row)
			case row.Deterioration == 0 && !row.InStorage:
				storageSelected = append(storageSelected, row)
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
		// Non-perishable, so no rot-order tiebreak matters; sort by ID only
		// for determinism.
		sort.Slice(storageSelected, func(i, j int) bool { return storageSelected[i].ID < storageSelected[j].ID })
		storageResult := []string{}
		storageTotal := 0.0
		for _, row := range storageSelected {
			storageResult = append(storageResult, row.ID)
			storageTotal += float64(row.Count)
		}
		storageTargets, storageMetric = domain.Known(storageResult), domain.Known(storageTotal)
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
			if !valid(seen, row.ID) || row.HitPoints < 0 || row.MaxHitPoints < row.HitPoints || row.Priority < 0 || row.Priority > 2 || row.Cell.X < 0 || row.Cell.Z < 0 {
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
		for _, row := range rows {
			if !valid(seen, row.ID) || len(row.Room) > 256 || row.Cell.X < 0 || row.Cell.Z < 0 {
				return r, errors.New("invalid upkeep filth")
			}
		}
	}
	// Cleaning is the bounded response of issue #6 slice 2: only filth in a
	// latched-dirty workspace whose ordinary coverage has failed is a target.
	clean, err := ReviewCleanliness(v.Rooms, v.Filth, v.CleaningWorkers, previous.DirtyRooms, v.Tick, cleanliness)
	if err != nil {
		return r, err
	}
	r.History.DirtyRooms = clean.DirtyRooms
	if selected, known := clean.Targets.Value(); known {
		result := []string{}
		for _, row := range selected {
			result = append(result, row.ID)
		}
		targets, metric = domain.Known(result), clean.Metric
	}
	r.History.Cleaning = add(MaintainCleanFacilities, 3, previous.Cleaning, targets, metric, false)
	r.History.Storage = add(MaintainStorage, 3, previous.Storage, storageTargets, storageMetric, false)
	for _, n := range r.Needs {
		if x, k := n.Metric.Value(); k && (math.IsNaN(x) || math.IsInf(x, 0)) {
			return r, errors.New("upkeep metric overflow")
		}
	}

	clearanceTargets := domain.Unknown[[]string]()
	if rows, known := v.Clearance.Value(); known {
		selected := []string{}
		seen := map[string]bool{}
		for _, row := range rows {
			if !valid(seen, row.EntityID) {
				return r, errors.New("invalid clearance target")
			}
			if ClearanceHoldReason(row) == "" {
				selected = append(selected, row.EntityID)
			}
		}
		if chunks, known := v.Chunks.Value(); known {
			for _, row := range chunks {
				if !valid(seen, row.EntityID) {
					return r, errors.New("invalid clearance chunk")
				}
				if ChunkHoldReason(row) == "" {
					selected = append(selected, row.EntityID)
				}
			}
		}
		clearanceTargets = domain.Known(selected)
	}
	r.History.Clearance = add(ClearHomeObstructions, 3, previous.Clearance, clearanceTargets, domain.Unknown[float64](), false)
	return r, nil
}

// CleaningContext fills the cleaning-response inputs of f.Upkeep from the
// review's own room census, labor census and tick, so every caller of
// ReviewUpkeep sees the same coverage picture DetectRoutine did.
func (f *RoutineFacts) CleaningContext(tick domain.Tick) {
	f.Upkeep.Tick = tick
	f.Upkeep.CleaningWorkers = domain.Unknown[int]()
	if labor, known := f.Labor.Value(); known {
		f.Upkeep.CleaningWorkers = domain.Known(labor[WorkCleaning])
	}
}

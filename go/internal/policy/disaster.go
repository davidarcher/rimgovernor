package policy

import (
	"errors"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const RecoverDisasterServices GoalID = "RecoverDisasterServices"

type DisasterPhase string

const (
	DisasterDisrupted  DisasterPhase = "disrupted"
	DisasterSurvival   DisasterPhase = "temporary_survival"
	DisasterRecovering DisasterPhase = "recovering"
	DisasterRestored   DisasterPhase = "restored"
	DisasterUnknown    DisasterPhase = "unknown"
)

type DisasterService string

const (
	DisasterFood           DisasterService = "food"
	DisasterProduction     DisasterService = "production"
	DisasterSleeping       DisasterService = "sleeping"
	DisasterShelter        DisasterService = "shelter"
	DisasterTemperature    DisasterService = "temperature"
	DisasterCooking        DisasterService = "cooking"
	DisasterPower          DisasterService = "power"
	DisasterStorage        DisasterService = "storage"
	DisasterInfrastructure DisasterService = "infrastructure"
)

var disasterServices = []DisasterService{DisasterFood, DisasterProduction, DisasterSleeping, DisasterShelter, DisasterTemperature, DisasterCooking, DisasterPower, DisasterStorage, DisasterInfrastructure}

// DisasterCondition is one native game condition affecting the colony map.
// TicksLeft is the native remaining duration when the condition is timed and
// that read was available; it is nil for a permanent condition or when native
// did not report one. Remaining time is planning evidence only: an episode
// ends when the condition is no longer observed, never when the count runs out.
type DisasterCondition struct {
	ID, Definition string
	Permanent      bool   `json:",omitempty"`
	TicksLeft      *int64 `json:",omitempty"`
}

// RemainingTicks reports the observed remaining duration when known.
func (c DisasterCondition) RemainingTicks() domain.Fact[int64] {
	if c.TicksLeft == nil {
		return domain.Unknown[int64]()
	}
	return domain.Known(*c.TicksLeft)
}

type DisasterEvidence struct {
	Service DisasterService
	Need    domain.NeedState
}

// History is observed disruption evidence. It grants neither orders nor recovery
// from elapsed time, disappeared buildings or an expired game condition.
type DisasterHistory struct {
	WorkKnown         bool
	Work              []RecoveryWork
	Started, Observed domain.Tick
	Phase             DisasterPhase
	Conditions        []DisasterCondition
	Services          []DisasterEvidence
	Affected          []DisasterService
	Damaged           []string
}

type RecoveryBuilding struct {
	ID                                        string
	UsesHitPoints, Broken, Forbidden, Burning domain.Fact[bool]
	HitPoints, MaxHitPoints                   domain.Fact[int64]
	Fuel, FuelTarget                          domain.Fact[float64]
	Refuelable                                domain.Fact[bool]
}

type RecoveryMethod string

const (
	RecoveryRefuel    RecoveryMethod = "refuel"
	RecoveryBreakdown RecoveryMethod = "breakdown"
	RecoveryRepair    RecoveryMethod = "repair"
)

type RecoveryWork struct {
	Building string
	Method   RecoveryMethod
}

// RecoveryPending lists measured ordinary work. Native eligibility and resource
// previews remain mandatory at action admission; protected buildings are skipped.
func RecoveryPending(observed domain.Fact[[]RecoveryBuilding]) (domain.Fact[[]RecoveryWork], error) {
	rows, known := observed.Value()
	if !known {
		return domain.Unknown[[]RecoveryWork](), nil
	}
	if len(rows) > 256 {
		return domain.Unknown[[]RecoveryWork](), errors.New("recovery census exceeds bound")
	}
	seen := map[string]bool{}
	work := []RecoveryWork{}
	complete := true
	for _, row := range rows {
		if !foodID(row.ID) || seen[row.ID] {
			return domain.Unknown[[]RecoveryWork](), errors.New("invalid recovery identity")
		}
		seen[row.ID] = true
		for _, f := range []domain.Fact[float64]{row.Fuel, row.FuelTarget} {
			if v, k := f.Value(); k && !foodNumber(v) {
				return domain.Unknown[[]RecoveryWork](), errors.New("invalid recovery fuel")
			}
		}
		for _, f := range []domain.Fact[int64]{row.HitPoints, row.MaxHitPoints} {
			if v, k := f.Value(); k && v < 0 {
				return domain.Unknown[[]RecoveryWork](), errors.New("invalid recovery hit points")
			}
		}
		hp, hk := row.HitPoints.Value()
		maximum, mk := row.MaxHitPoints.Value()
		if hk && mk && (maximum <= 0 || hp > maximum) {
			return domain.Unknown[[]RecoveryWork](), errors.New("inconsistent recovery hit points")
		}
		forbidden, fk := row.Forbidden.Value()
		burning, bk := row.Burning.Value()
		if forbidden || burning {
			continue
		}
		if !fk || !bk {
			complete = false
			continue
		}
		broken, bk := row.Broken.Value()
		uses, uk := row.UsesHitPoints.Value()
		refuel, rk := row.Refuelable.Value()
		complete = complete && bk && uk && rk
		if broken {
			work = append(work, RecoveryWork{row.ID, RecoveryBreakdown})
		}
		if uk && uses {
			if !hk || !mk {
				complete = false
			} else if hp < maximum {
				work = append(work, RecoveryWork{row.ID, RecoveryRepair})
			}
		}
		if rk && refuel {
			fuel, fk := row.Fuel.Value()
			target, tk := row.FuelTarget.Value()
			if !fk || !tk {
				complete = false
			} else if fuel < target*.25 {
				work = append(work, RecoveryWork{row.ID, RecoveryRefuel})
			}
		}
	}
	if !complete {
		return domain.Unknown[[]RecoveryWork](), nil
	}
	sort.SliceStable(work, func(i, j int) bool {
		a, b := work[i], work[j]
		if (a.Method == RecoveryRefuel) != (b.Method == RecoveryRefuel) {
			return a.Method == RecoveryRefuel
		}
		return a.Building < b.Building
	})
	return domain.Known(work), nil
}

func (h *DisasterHistory) Validate() error {
	if h == nil {
		return nil
	}
	if h.Started < 0 || h.Observed < h.Started || len(h.Conditions) > 256 || len(h.Damaged) > 256 || len(h.Services) != len(disasterServices) || len(h.Affected) > len(disasterServices) {
		return errors.New("invalid disaster history bounds")
	}
	switch h.Phase {
	case DisasterDisrupted, DisasterSurvival, DisasterRecovering, DisasterRestored, DisasterUnknown:
	default:
		return errors.New("invalid disaster phase")
	}
	seen := map[string]bool{}
	for _, c := range h.Conditions {
		if !foodID(c.ID) || !foodID(c.Definition) || seen[c.ID] || c.TicksLeft != nil && (c.Permanent || *c.TicksLeft < 0) {
			return errors.New("invalid disaster condition")
		}
		seen[c.ID] = true
	}
	seen = map[string]bool{}
	for _, id := range h.Damaged {
		if !foodID(id) || seen[id] {
			return errors.New("invalid damaged building history")
		}
		seen[id] = true
	}
	for i, e := range h.Services {
		if e.Service != disasterServices[i] || (e.Need != domain.NeedUnknown && e.Need != domain.NeedDeficit && e.Need != domain.NeedRecovered) {
			return errors.New("invalid disaster service evidence")
		}
	}
	services := map[DisasterService]bool{}
	for _, s := range h.Affected {
		if !slices.Contains(disasterServices, s) || services[s] {
			return errors.New("invalid affected service")
		}
		services[s] = true
	}
	deficit, unknown := false, false
	for _, e := range h.Services {
		deficit = deficit || e.Need == domain.NeedDeficit
		unknown = unknown || e.Need == domain.NeedUnknown
		if e.Need == domain.NeedDeficit && !services[e.Service] {
			return errors.New("untracked disaster deficit")
		}
	}
	if !h.WorkKnown && len(h.Work) > 0 || len(h.Work) > 768 {
		return errors.New("invalid recovery work evidence")
	}
	workSeen := map[RecoveryWork]bool{}
	for _, w := range h.Work {
		if !foodID(w.Building) || workSeen[w] || (w.Method != RecoveryRefuel && w.Method != RecoveryBreakdown && w.Method != RecoveryRepair) {
			return errors.New("invalid recovery work method")
		}
		workSeen[w] = true
	}
	if h.Phase != DisasterUnknown && (unknown && !deficit || (h.Phase == DisasterDisrupted || h.Phase == DisasterRecovering) != deficit || (h.Phase == DisasterDisrupted || h.Phase == DisasterSurvival) != (len(h.Conditions) > 0)) {
		return errors.New("disaster phase contradicts evidence")
	}
	return nil
}

func cloneDisaster(h *DisasterHistory) *DisasterHistory {
	if h == nil {
		return nil
	}
	out := *h
	out.Conditions = slices.Clone(h.Conditions)
	out.Services = slices.Clone(h.Services)
	out.Affected = slices.Clone(h.Affected)
	out.Damaged = slices.Clone(h.Damaged)
	out.Work = slices.Clone(h.Work)
	return &out
}

// ReviewDisaster uses the same survival gates as routine planning. The store
// resets history on world replacement or rewind and retains it through Manual.
func ReviewDisaster(conditions domain.Fact[[]DisasterCondition], buildings domain.Fact[[]RecoveryBuilding], gates FootholdGates, previous *DisasterHistory, tick domain.Tick) (*DisasterHistory, error) {
	if err := previous.Validate(); err != nil {
		return nil, err
	}
	if tick < 0 || previous != nil && tick < previous.Observed {
		return nil, errors.New("stale disaster observation")
	}
	work, err := RecoveryPending(buildings)
	if err != nil {
		return nil, err
	}
	current, known := conditions.Value()
	if len(current) > 256 {
		return nil, errors.New("too many environmental conditions")
	}
	seen := map[string]bool{}
	for _, c := range current {
		if !foodID(c.ID) || !foodID(c.Definition) || seen[c.ID] {
			return nil, errors.New("invalid environmental condition")
		}
		if c.TicksLeft != nil && (c.Permanent || *c.TicksLeft < 0) {
			return nil, errors.New("invalid environmental condition duration")
		}
		seen[c.ID] = true
	}
	h := cloneDisaster(previous)
	if !known {
		if h != nil && h.Phase != DisasterRestored {
			h.Phase = DisasterUnknown
			h.Observed = tick
		}
		return h, nil
	}
	if h != nil && h.Phase == DisasterRestored {
		if len(current) == 0 {
			return h, nil
		}
		h = nil
	}
	if h == nil {
		if len(current) == 0 {
			return nil, nil
		}
		h = &DisasterHistory{Started: tick}
	}
	h.Observed = tick
	h.Conditions = slices.Clone(current)
	sort.Slice(h.Conditions, func(i, j int) bool { return h.Conditions[i].ID < h.Conditions[j].ID })
	tracked := map[string]bool{}
	for _, id := range h.Damaged {
		tracked[id] = true
	}
	pending, complete := work.Value()
	h.WorkKnown, h.Work = complete, slices.Clone(pending)
	for _, w := range pending {
		if w.Method != RecoveryRefuel {
			tracked[w.Building] = true
		}
	}
	if len(tracked) > 256 {
		return nil, errors.New("damaged building history exceeds bound")
	}
	h.Damaged = nil
	for id := range tracked {
		h.Damaged = append(h.Damaged, id)
	}
	sort.Strings(h.Damaged)
	infrastructure := domain.Unknown[bool]()
	if complete {
		recovered := len(pending) == 0
		observed := map[string]bool{}
		rows, _ := buildings.Value()
		for _, b := range rows {
			uses, uk := b.UsesHitPoints.Value()
			hp, hk := b.HitPoints.Value()
			maximum, mk := b.MaxHitPoints.Value()
			broken, bk := b.Broken.Value()
			observed[b.ID] = uk && (!uses || hk && mk && hp == maximum) && bk && !broken
		}
		for id := range tracked {
			recovered = recovered && observed[id]
		}
		infrastructure = domain.Known(recovered)
	}
	facts := []domain.Fact[bool]{gates.Food, gates.Production, gates.Sleeping, gates.Shelter, gates.Temperature, gates.Cooking, gates.Power, gates.Storage, infrastructure}
	h.Services = nil
	affected := map[DisasterService]bool{}
	for _, s := range h.Affected {
		affected[s] = true
	}
	deficit, unknown := false, false
	for i, f := range facts {
		value, k := f.Value()
		need := domain.NeedUnknown
		if k {
			need = domain.NeedRecovered
			if !value {
				need = domain.NeedDeficit
				deficit = true
				affected[disasterServices[i]] = true
			}
		} else {
			unknown = true
		}
		h.Services = append(h.Services, DisasterEvidence{disasterServices[i], need})
	}
	h.Affected = nil
	for _, s := range disasterServices {
		if affected[s] {
			h.Affected = append(h.Affected, s)
		}
	}
	switch {
	case len(current) > 0 && deficit:
		h.Phase = DisasterDisrupted
	case deficit:
		h.Phase = DisasterRecovering
	case unknown:
		h.Phase = DisasterUnknown
	case len(current) > 0:
		h.Phase = DisasterSurvival
	default:
		h.Phase = DisasterRestored
	}
	return h, h.Validate()
}

func (h *DisasterHistory) Promote(id GoalID, priority int) int {
	if h == nil || h.Phase != DisasterDisrupted && h.Phase != DisasterRecovering {
		return priority
	}
	for _, e := range h.Services {
		if e.Need != domain.NeedDeficit {
			continue
		}
		goal := map[DisasterService]GoalID{DisasterFood: EnsureFoodSupply, DisasterProduction: EnsureFoodSupply, DisasterSleeping: EnsureInitialShelter, DisasterShelter: EnsureInitialShelter, DisasterTemperature: EnsureTemperatureSafety, DisasterCooking: EnsureCooking, DisasterPower: EnsureBasicPower, DisasterStorage: EnsureFoodStorage, DisasterInfrastructure: RecoverDisasterServices}[e.Service]
		if goal == id || id == MaintainWood && (e.Service == DisasterTemperature || e.Service == DisasterCooking) {
			return min(priority, 2)
		}
	}
	return priority
}

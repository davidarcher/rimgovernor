package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type RecoveryRestriction struct {
	Pawn PawnID
	Area domain.Fact[string]
}
type RecoverySafety struct {
	Restrictions []RecoveryRestriction
}
type RecoveryWorker struct {
	Pawn                                        PawnID
	Dead, Downed, Drafted, Mental, PlayerForced domain.Fact[bool]
}
type RecoveryPlanning struct {
	Safety    domain.Fact[RecoverySafety]
	Workers   domain.Fact[[]RecoveryWorker]
	Buildings domain.Fact[[]RecoveryBuilding]
}

type RecoveryProposalKind string

const RecoveryServiceProposal RecoveryProposalKind = "service_work"

type RecoverySelectionReason string

const (
	RecoveryAdmissionRequired RecoverySelectionReason = "native_admission_required"
	RecoveryFactsUnknown      RecoverySelectionReason = "observations_unknown"
	RecoveryNoWorker          RecoverySelectionReason = "no_available_worker"
	RecoveryMethodsSeen       RecoverySelectionReason = "methods_already_seen"
	RecoveryTrackedMissing    RecoverySelectionReason = "tracked_infrastructure_missing_or_protected"
	RecoveryNoWork            RecoverySelectionReason = "no_pending_work"
)

// These are candidates for native preview, never admitted jobs. PriorArea is
// the pawn's observed restriction; hazard sheltering is PlanSheltering's.
type RecoveryCandidate struct {
	ID        domain.MethodID
	Kind      RecoveryProposalKind
	Pawn      PawnID
	Building  string
	Method    RecoveryMethod
	HitPoints *int64
	Fuel      *float64
	PriorArea *string
}
type RecoverySelection struct {
	Tick       domain.Tick
	Reason     RecoverySelectionReason
	Candidates []RecoveryCandidate
}

func (c RecoveryCandidate) methodID() domain.MethodID {
	c.ID = ""
	data, _ := json.Marshal(c)
	hash := sha256.Sum256(data)
	return domain.MethodID(fmt.Sprintf("recovery-%x", hash[:16]))
}
func recoveryValue[T any](f domain.Fact[T]) *T {
	v, k := f.Value()
	if !k {
		return nil
	}
	return &v
}

func (p RecoveryPlanning) Validate() error {
	if _, err := RecoveryPending(p.Buildings); err != nil {
		return err
	}
	if s, k := p.Safety.Value(); k {
		pawns := map[PawnID]bool{}
		for _, r := range s.Restrictions {
			if !foodID(string(r.Pawn)) || pawns[r.Pawn] {
				return errors.New("invalid recovery restriction pawn")
			}
			pawns[r.Pawn] = true
			if id, k := r.Area.Value(); k && id != "" && !foodID(id) {
				return errors.New("invalid current restriction")
			}
		}
	}
	if workers, k := p.Workers.Value(); k {
		seen := map[PawnID]bool{}
		for _, w := range workers {
			if !foodID(string(w.Pawn)) || seen[w.Pawn] {
				return errors.New("invalid recovery worker")
			}
			seen[w.Pawn] = true
		}
	}
	return nil
}

func RecoveryNeed(h *DisasterHistory) domain.Finding {
	if h.Validate() != nil || h != nil && h.Phase == DisasterUnknown {
		return domain.FindingUnclear
	}
	if h == nil || h.Phase == DisasterRestored {
		return domain.FindingMet
	}
	return h.Services[len(h.Services)-1].Finding
}

// RecoveryWorkContinuing observes the current service job after an intent has
// been applied. Changing hit points or fuel must not create a replacement while
// that work is still running. This is derived from the census, not a second
// commitment ledger. Unknown jobs/targets cannot establish that work stopped.
func RecoveryWorkContinuing(service domain.RecoveryService, buildings domain.Fact[[]RecoveryBuilding], pawns domain.Fact[[]WorkPawn]) domain.Fact[bool] {
	observed, known := buildings.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	var targetBuildings []RecoveryBuilding
	for _, building := range observed {
		if building.ID == service.Thing() {
			targetBuildings = append(targetBuildings, building)
		}
	}
	pending, err := RecoveryPending(domain.Known(targetBuildings))
	work, known := pending.Value()
	if err != nil || !known {
		return domain.Unknown[bool]()
	}
	needed := false
	for _, need := range work {
		if need.Building == service.Thing() && string(need.Method) == string(service.Method()) {
			needed = true
		}
	}
	if !needed {
		return domain.Known(false)
	}
	rows, known := pawns.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, pawn := range rows {
		if domain.PawnID(pawn.ID) != service.Pawn() {
			continue
		}
		job, known := pawn.Job.Value()
		if !known {
			return domain.Unknown[bool]()
		}
		names := map[domain.RecoveryMethod]string{domain.RecoveryServiceRepair: "Repair", domain.RecoveryServiceBreakdown: "FixBrokenDownBuilding", domain.RecoveryServiceRefuel: "Refuel"}
		if job.Def != names[service.Method()] {
			return domain.Known(false)
		}
		target, known := job.Target.Value()
		if !known {
			return domain.Unknown[bool]()
		}
		return domain.Known(target.Thing == service.Thing())
	}
	return domain.Known(false)
}

// SelectRecoveryMethods bounds the next admission batch to eight proposals.
// Seen methods belong to the current shared Episode, including retired plans.
// Merely creating or refreshing a batch does not count as attempting its methods.
func SelectRecoveryMethods(p RecoveryPlanning, h *DisasterHistory, used []domain.MethodID, tick domain.Tick) (RecoverySelection, error) {
	out := RecoverySelection{Tick: tick, Reason: RecoveryFactsUnknown}
	if err := p.Validate(); err != nil {
		return out, err
	}
	if err := h.Validate(); err != nil {
		return out, err
	}
	if tick < 0 || h != nil && (h.Observed > tick || h.Phase != DisasterRestored && h.Observed != tick) {
		return out, errors.New("invalid recovery selection boundary")
	}
	seen := map[domain.MethodID]bool{}
	for _, id := range used {
		if !foodID(string(id)) || seen[id] {
			return out, errors.New("invalid used recovery method")
		}
		seen[id] = true
	}
	if h == nil || h.Phase == DisasterRestored {
		out.Reason = RecoveryNoWork
		return out, nil
	}
	pending, err := RecoveryPending(p.Buildings)
	if err != nil {
		return out, err
	}
	work, wk := pending.Value()
	safety, sk := p.Safety.Value()
	workers, pk := p.Workers.Value()
	if !sk || !pk || h.Phase == DisasterUnknown {
		return out, nil
	}
	// The independent exact-pawn and restriction censuses must agree. Missing
	// identities cannot silently remove a pawn's exposure or player restriction.
	if len(workers) != len(safety.Restrictions) {
		return out, nil
	}
	byPawn := map[PawnID]RecoveryWorker{}
	for _, w := range workers {
		byPawn[w.Pawn] = w
	}
	for _, r := range safety.Restrictions {
		if _, exists := byPawn[r.Pawn]; !exists {
			return out, nil
		}
	}
	available := []RecoveryWorker{}
	unknownWorker := false
	for _, w := range workers {
		blocked, known := false, true
		for _, f := range []domain.Fact[bool]{w.Dead, w.Downed, w.Drafted, w.Mental} {
			v, k := f.Value()
			blocked = blocked || k && v
			known = known && k
		}
		if !blocked && !known {
			unknownWorker = true
		}
		if !blocked && known {
			available = append(available, w)
		}
	}
	sort.Slice(available, func(i, j int) bool {
		a, b := orderedWorkCost(available[i].PlayerForced), orderedWorkCost(available[j].PlayerForced)
		if a != b {
			return a < b
		}
		return available[i].Pawn < available[j].Pawn
	})
	if len(available) == 0 {
		if !unknownWorker {
			out.Reason = RecoveryNoWorker
		}
		return out, nil
	}
	add := func(c RecoveryCandidate) {
		if len(out.Candidates) >= 8 {
			return
		}
		c.ID = c.methodID()
		if !seen[c.ID] {
			out.Candidates = append(out.Candidates, c)
		}
	}
	restrictions := map[PawnID]domain.Fact[string]{}
	for _, r := range safety.Restrictions {
		restrictions[r.Pawn] = r.Area
	}
	eligible := []RecoveryWorker{}
	areaUnknown := false
	for _, w := range available {
		if _, known := restrictions[w.Pawn].Value(); !known {
			areaUnknown = true
			continue
		}
		eligible = append(eligible, w)
	}
	if areaUnknown || unknownWorker || !wk {
		return out, nil
	}
	byBuilding := map[string]RecoveryBuilding{}
	buildings, _ := p.Buildings.Value()
	for _, b := range buildings {
		byBuilding[b.ID] = b
	}
	for _, w := range eligible {
		if len(out.Candidates) >= 8 {
			break
		}
		for _, job := range work {
			if len(out.Candidates) >= 8 {
				break
			}
			b := byBuilding[job.Building]
			prior, _ := restrictions[w.Pawn].Value()
			add(RecoveryCandidate{Kind: RecoveryServiceProposal, Pawn: w.Pawn, Building: job.Building, Method: job.Method, HitPoints: recoveryValue(b.HitPoints), Fuel: recoveryValue(b.Fuel), PriorArea: &prior})
		}
	}
	switch {
	case len(out.Candidates) > 0:
		out.Reason = RecoveryAdmissionRequired
	case len(work) > 0:
		out.Reason = RecoveryMethodsSeen
	case h.Services[len(h.Services)-1].Finding != domain.FindingMet:
		out.Reason = RecoveryTrackedMissing
	default:
		out.Reason = RecoveryNoWork
	}
	return out, nil
}

func (s RecoverySelection) Validate() error {
	if s.Tick < 0 || len(s.Candidates) > 8 {
		return errors.New("invalid recovery selection bounds")
	}
	switch s.Reason {
	case RecoveryAdmissionRequired, RecoveryFactsUnknown, RecoveryNoWorker, RecoveryMethodsSeen, RecoveryTrackedMissing, RecoveryNoWork:
	default:
		return errors.New("invalid recovery selection reason")
	}
	if (len(s.Candidates) > 0) != (s.Reason == RecoveryAdmissionRequired) {
		return errors.New("recovery reason contradicts candidates")
	}
	seen := map[domain.MethodID]bool{}
	for _, c := range s.Candidates {
		if !foodID(string(c.Pawn)) || c.ID != c.methodID() || seen[c.ID] || c.PriorArea == nil || *c.PriorArea != "" && !foodID(*c.PriorArea) {
			return errors.New("invalid recovery candidate identity")
		}
		seen[c.ID] = true
		if c.HitPoints != nil && *c.HitPoints < 0 || c.Fuel != nil && !foodNumber(*c.Fuel) {
			return errors.New("invalid recovery candidate state")
		}
		if c.Kind != RecoveryServiceProposal || !foodID(c.Building) || (c.Method != RecoveryRefuel && c.Method != RecoveryBreakdown && c.Method != RecoveryRepair) || c.Method == RecoveryRefuel && c.Fuel == nil || c.Method == RecoveryRepair && c.HitPoints == nil {
			return errors.New("invalid recovery service proposal")
		}
	}
	return nil
}

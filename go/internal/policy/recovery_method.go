package policy

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type RecoveryRestriction struct {
	Pawn PawnID
	Area domain.Fact[string]
}
type RecoverySafety struct {
	RoofHazard   domain.Fact[bool]
	SafeAreas    []string
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

const (
	RecoveryAreaProposal    RecoveryProposalKind = "roofed_area"
	RecoveryServiceProposal RecoveryProposalKind = "service_work"
)

type RecoverySelectionReason string

const (
	RecoveryAdmissionRequired RecoverySelectionReason = "native_admission_required"
	RecoveryFactsUnknown      RecoverySelectionReason = "observations_unknown"
	RecoveryNoRefuge          RecoverySelectionReason = "no_roofed_refuge"
	RecoveryNoWorker          RecoverySelectionReason = "no_available_worker"
	RecoveryMethodsSeen       RecoverySelectionReason = "methods_already_seen"
	RecoveryTrackedMissing    RecoverySelectionReason = "tracked_infrastructure_missing_or_protected"
	RecoveryNoWork            RecoverySelectionReason = "no_pending_work"
)

// These are candidates for native preview, never admitted jobs or area leases.
// PriorArea is the observed restriction; native admission must prove that a
// proposed refuge is safe/reachable for this exact pawn. The saved restriction
// is current state, not permanent player intent under Auto.
type RecoveryCandidate struct {
	ID             domain.MethodID
	Kind           RecoveryProposalKind
	Pawn           PawnID
	Building, Area string
	Method         RecoveryMethod
	HitPoints      *int64
	Fuel           *float64
	PriorArea      *string
	Window         domain.Tick
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
		if len(s.SafeAreas) > 256 || len(s.Restrictions) > 256 {
			return errors.New("recovery safety census exceeds bound")
		}
		areas := map[string]bool{}
		for _, id := range s.SafeAreas {
			if !foodID(id) || areas[id] {
				return errors.New("invalid recovery refuge identity")
			}
			areas[id] = true
		}
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
		if len(workers) > 256 {
			return errors.New("recovery worker census exceeds bound")
		}
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

func RecoveryNeed(h *DisasterHistory, safety domain.Fact[RecoverySafety]) domain.NeedState {
	if h.Validate() != nil {
		return domain.NeedUnknown
	}
	if h == nil || h.Phase == DisasterRestored {
		return domain.NeedRecovered
	}
	if s, k := safety.Value(); k && positive(s.RoofHazard) {
		return domain.NeedDeficit
	}
	if h.Phase == DisasterUnknown {
		return domain.NeedUnknown
	}
	need := h.Services[len(h.Services)-1].Need
	if s, k := safety.Value(); k {
		if hazard, known := s.RoofHazard.Value(); known {
			if hazard {
				return domain.NeedDeficit
			}
			return need
		}
	}
	if need == domain.NeedRecovered {
		return domain.NeedUnknown
	}
	return need
}

// SelectRecoveryMethods bounds the next admission batch to eight proposals.
// Seen methods belong to the current shared goal epoch, including retired plans.
// Merely creating or refreshing a batch does not count as attempting its methods.
func SelectRecoveryMethods(p RecoveryPlanning, h *DisasterHistory, used []domain.MethodID, tick domain.Tick) (RecoverySelection, error) {
	out := RecoverySelection{Tick: tick, Reason: RecoveryFactsUnknown}
	if err := p.Validate(); err != nil {
		return out, err
	}
	if err := h.Validate(); err != nil {
		return out, err
	}
	if tick < 0 || h != nil && (h.Observed > tick || h.Phase != DisasterRestored && h.Observed != tick) || len(used) > 256 {
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
	hazard, hk := safety.RoofHazard.Value()
	if !sk || !pk || !hk || h.Phase == DisasterUnknown && !hazard {
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
	areas := slices.Clone(safety.SafeAreas)
	sort.Strings(areas)
	if hazard && len(areas) == 0 {
		out.Reason = RecoveryNoRefuge
		return out, nil
	}
	restrictions := map[PawnID]domain.Fact[string]{}
	for _, r := range safety.Restrictions {
		restrictions[r.Pawn] = r.Area
	}
	eligible := []RecoveryWorker{}
	areaNeeded, areaUnknown := false, false
	for _, w := range available {
		area, known := restrictions[w.Pawn].Value()
		if !known {
			areaUnknown = true
			continue
		}
		if hazard && !slices.Contains(areas, area) {
			areaNeeded = true
			for _, target := range areas[:min(2, len(areas))] {
				prior := area
				add(RecoveryCandidate{Kind: RecoveryAreaProposal, Pawn: w.Pawn, Area: target, PriorArea: &prior, Window: tick / 600})
			}
			continue
		}
		eligible = append(eligible, w)
	}
	// Exposure protection precedes repair. Refused/exhausted protection never
	// grants permission to send that pawn into unrelated unroofed service work.
	if areaNeeded {
		out.Reason = RecoveryMethodsSeen
		if len(out.Candidates) > 0 {
			out.Reason = RecoveryAdmissionRequired
		}
		return out, nil
	}
	if areaUnknown || unknownWorker || !wk || h.Phase == DisasterUnknown {
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
	case h.Services[len(h.Services)-1].Need != domain.NeedRecovered:
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
	case RecoveryAdmissionRequired, RecoveryFactsUnknown, RecoveryNoRefuge, RecoveryNoWorker, RecoveryMethodsSeen, RecoveryTrackedMissing, RecoveryNoWork:
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
		switch c.Kind {
		case RecoveryAreaProposal:
			if !foodID(c.Area) || c.Building != "" || c.Method != "" || c.HitPoints != nil || c.Fuel != nil || c.Window != s.Tick/600 || c.Area == *c.PriorArea {
				return errors.New("invalid recovery area proposal")
			}
		case RecoveryServiceProposal:
			if !foodID(c.Building) || c.Area != "" || c.Window != 0 || (c.Method != RecoveryRefuel && c.Method != RecoveryBreakdown && c.Method != RecoveryRepair) || c.Method == RecoveryRefuel && c.Fuel == nil || c.Method == RecoveryRepair && c.HitPoints == nil {
				return errors.New("invalid recovery service proposal")
			}
		default:
			return errors.New("invalid recovery candidate kind")
		}
	}
	return nil
}

package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// staleBills counts, per bill on a bench, the consecutive reviews its owner
// stayed Met (#2411): derived state the Rounder keeps in memory beside
// billAges, so a restart restarts every count and removal is merely delayed.
//
// A bill an owner no longer wants is counted apart (#2433): the planner that
// built the owner's demand notes, once per review, which of the owner's bills
// that demand does not name, whatever the owner's finding.
type staleBills struct {
	mu     sync.Mutex
	streak map[string]int
	// due are the bills at StaleBillReviews or more, for the planners.
	due []policy.StaleBill
	// candidates are the last review's bills, what a planner judges.
	candidates []policy.StaleBill
	// unwanted counts, per bill, the reviews a planner found it outside the
	// owner's demand.
	unwanted map[string]unwantedRun
}

type unwantedRun struct {
	bill     policy.StaleBill
	revision uint64
	reviews  int
}

// stale are the candidates whose count reached policy.StaleBillReviews: the
// bills the next review files their owners Unmet for.
func (s *staleBills) stale(candidates []policy.StaleBill) []policy.StaleBill {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []policy.StaleBill
	for _, c := range candidates {
		if s.streak[c.ID] >= policy.StaleBillReviews {
			out = append(out, c)
		}
	}
	return out
}

// observe advances the counts after a review: a candidate whose owner the
// review found Met (or filed Unmet only for a stale bill) counts one more, any
// other restarts at zero, and a bill no longer a candidate is forgotten.
func (s *staleBills) observe(candidates []policy.StaleBill, assessments []policy.RoundsAssessment) {
	met := map[policy.ConcernID]bool{}
	for _, a := range assessments {
		met[a.ID] = a.Finding == domain.FindingMet || a.StaleBill
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	next := make(map[string]int, len(candidates))
	unwanted := make(map[string]unwantedRun, len(s.unwanted))
	s.due = nil
	for _, c := range candidates {
		if policy.StaleBillOwner(c.Owner) && met[c.Owner] {
			next[c.ID] = s.streak[c.ID] + 1
		}
		if next[c.ID] >= policy.StaleBillReviews {
			s.due = append(s.due, c)
		}
		if run, ok := s.unwanted[c.ID]; ok {
			run.bill = c
			unwanted[c.ID] = run
		}
	}
	s.streak, s.unwanted, s.candidates = next, unwanted, candidates
}

// noteWanted records, for each of concern's bills the last review saw, whether
// the owner's demand names it: judge returns judged=false for a bill this
// planner does not own the demand of (counted neither way), wanted=true for a
// bill the demand names (restarts its count). A bill outside the demand counts
// one more only once per review, however many steps run under it.
func (s *staleBills) noteWanted(revision uint64, concern policy.ConcernID, judge func(policy.StaleBill) (judged, wanted bool)) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, c := range s.candidates {
		if c.Owner != concern {
			continue
		}
		judged, wanted := judge(c)
		if !judged {
			continue
		}
		if wanted {
			delete(s.unwanted, c.ID)
			continue
		}
		if s.unwanted == nil {
			s.unwanted = map[string]unwantedRun{}
		}
		run := s.unwanted[c.ID]
		if run.reviews == 0 || run.revision != revision {
			run.reviews++
			run.revision = revision
		}
		run.bill = c
		s.unwanted[c.ID] = run
	}
}

// first is the first stale bill of owner, if any: one whose owner stayed Met,
// else one the owner's demand stopped naming.
func (s *staleBills) first(owner policy.ConcernID) (policy.StaleBill, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, b := range s.due {
		if b.Owner == owner {
			return b, true
		}
	}
	var found policy.StaleBill
	for id, run := range s.unwanted {
		if run.bill.Owner == owner && run.reviews >= policy.StaleBillReviews && (found.ID == "" || id < found.ID) {
			found = run.bill
		}
	}
	return found, found.ID != ""
}

// removed restarts a bill's count once its removal is committed: native may
// refuse (the bill is being worked), and the next try waits for a fresh run
// of reviews.
func (s *staleBills) removed(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.streak, id)
	delete(s.unwanted, id)
	kept := s.due[:0]
	for _, b := range s.due {
		if b.ID != id {
			kept = append(kept, b)
		}
	}
	s.due = kept
}

// staleBillCandidates are the finite, active bills on the benches that the
// journal shows a stale-bill owner placed (the review binds its standard). A bench census native cannot serve
// leaves none (the counts then restart).
func (r *Rounder) staleBillCandidates(ctx context.Context, snapshot domain.GenerationSnapshot, expected observation.Identity, review store.Rounds) ([]policy.StaleBill, error) {
	native, ok := r.native.(RoundsWorkBenchSource)
	if !ok {
		return nil, nil
	}
	reads, _, err := r.benchSource(native, expected, false).ReadGearBenches(ctx, boundary.Identity(snapshot))
	if err != nil {
		return nil, nil
	}
	var ids []string
	type census struct {
		products []policy.Resource
		worker   string
	}
	seen := map[string]census{}
	for _, read := range reads {
		bills, known := read.Bench.Bills.Value()
		if !known {
			continue
		}
		for _, bill := range bills {
			finite, fk := bill.Finite.Value()
			active, ak := bill.Active.Value()
			if bill.ID != "" && fk && finite && ak && active {
				ids = append(ids, bill.ID)
				worker, _ := bill.Worker.Value()
				seen[bill.ID] = census{products: bill.Products, worker: worker}
			}
		}
	}
	placed, err := r.player.journal.PlacedBills(ctx, snapshot, ids)
	if err != nil {
		return nil, err
	}
	var out []policy.StaleBill
	for id, bill := range placed {
		if concern, bound := review.Need(bill.Standard); bound && bill.Mode == domain.GearBatch && policy.UnwantedBillOwner(concern) {
			out = append(out, policy.StaleBill{Owner: concern, Bench: bill.Bench, ID: id, Products: seen[id].products, Worker: seen[id].worker})
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out, nil
}

// removeUnwantedBill is removeStaleBill after the planner that built concern's
// demand notes which of its bills the demand names (#2433): a bill outside it
// for StaleBillReviews reviews goes whatever the owner's finding.
func (r *Rounder) removeUnwantedBill(call, epoch context.Context, arbiter *stepArbiter, state ControlState, review store.Rounds, owner store.WorkOwner, concern policy.ConcernID, judge func(policy.StaleBill) (judged, wanted bool)) (domain.PlanID, error) {
	r.staleBills.noteWanted(review.Revision, concern, judge)
	return r.removeStaleBill(call, epoch, arbiter, state, owner, concern)
}

// removeStaleBill commits the one-action RemoveProductionBill plan for the
// first stale bill of owner's concern, the planners' Unmet response to a bill
// whose need is gone. The zero plan means none was due, or another planner of
// the step took it. The method id carries an attempt number: native refuses a
// bill a pawn is working and a later run of reviews retries as a new method.
func (r *Rounder) removeStaleBill(call, epoch context.Context, arbiter *stepArbiter, state ControlState, owner store.WorkOwner, concern policy.ConcernID) (domain.PlanID, error) {
	bill, ok := r.staleBills.first(concern)
	if !ok {
		return "", nil
	}
	if arbiter != nil && !arbiter.tryClaim(nil, "stale-bill:"+bill.ID) {
		return "", nil
	}
	tried := map[domain.MethodID]bool{}
	for _, method := range owner.OwnerHistory() {
		tried[method.Method] = true
	}
	var method domain.MethodID
	for attempt := 0; ; attempt++ {
		digest := sha256.Sum256([]byte(fmt.Sprintf("remove-bill/%s/%d", bill.ID, attempt)))
		if method = domain.MethodID(fmt.Sprintf("remove-bill-%x", digest[:16])); !tried[method] {
			break
		}
	}
	removal, err := domain.NewRemoveProductionBill(bill.Bench, bill.ID)
	if err != nil {
		return "", err
	}
	id := domain.MintPlanID()
	action, err := domain.NewRemoveProductionBillAction(domain.ActionID(string(id)+"-0"), removal)
	if err != nil {
		return "", err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return "", err
	}
	p := r.player
	if err = p.current(call, epoch); err != nil {
		return "", err
	}
	if p.session.State() != state {
		return "", fmt.Errorf("%w: removeStaleBill: p.session.State() != state", ErrControl)
	}
	if err = p.journal.CommitOwnerMethod(call, owner, method, "", plan); err != nil {
		return "", err
	}
	r.staleBills.removed(bill.ID)
	return id, nil
}

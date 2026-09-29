package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoutineClearanceSource is the clearance census plus the zone preview the
// chunk dump needs.
type RoutineClearanceSource interface {
	observation.ColonySource
	observation.ClearanceSource
	FieldNative
}

type RoutineClearancePlanner struct {
	reviewer *RoutineReviewer
	native   RoutineClearanceSource
}
type RoutineClearanceResult struct {
	Reason RoutineBuildingReason
	Plan   domain.PlanID
	// NativeWorkTicks asks for game time while ordinary hauling moves
	// designated chunks into their store.
	NativeWorkTicks uint32
}

func NewRoutineClearancePlanner(reviewer *RoutineReviewer, native RoutineClearanceSource) (*RoutineClearancePlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineClearancePlanner: reviewer == nil || native == nil", ErrControl)
	}
	return &RoutineClearancePlanner{reviewer, native}, nil
}
func (r *RoutineClearancePlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineClearanceResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineClearanceResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil {
		return RoutineClearanceResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil", ErrControl)
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineClearanceResult{Reason: BuildingMethodNoReview}, nil
	}
	call, recorded := recordPlannerStep(call, policy.ClearHomeObstructions, state.Snapshot, review.Tick)
	defer recorded()
	goal, workable, err := p.journal.Workable(call, review, policy.ClearHomeObstructions)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	if !workable {
		return RoutineClearanceResult{Reason: BuildingMethodNoDeficit}, nil
	}
	selected := false
	for _, row := range review.Development.Rows {
		selected = selected || row.Goal == policy.ClearHomeObstructions && row.Selected
	}
	if !selected {
		return RoutineClearanceResult{Reason: BuildingMethodRefused}, nil
	}
	for _, method := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, method.Plan)
		if err != nil {
			return RoutineClearanceResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineClearanceResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineClearanceResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	colony, err := r.reviewer.observeColony(call, r.native, expected, nil)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	read, err := observation.ObserveClearanceCensus(call, r.native, expected, true)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	census, known := read.Value()
	if !known {
		return RoutineClearanceResult{Reason: BuildingMethodUsed}, nil
	}
	// The review admitted at most one remote ruin by reach and demand from
	// its complete facts; the planner executes that choice against the fresh
	// census, whose native safety verdict still gates it.
	filtered := append([]policy.ClearanceTarget(nil), census.Targets...)
	for i := range filtered {
		row := &filtered[i]
		safe := false
		if row.Salvage != nil {
			safe, _ = row.Salvage.Safe.Value()
		}
		row.SalvageSelected = !row.InHome && review.SalvageTarget != "" && row.EntityID == review.SalvageTarget && safe
	}
	selection := policy.SelectHomeClearance(filtered, colony.Projection.Center)
	if len(selection.Targets) == 0 {
		return r.dump(call, epoch, state, goal, review.Tick, census, started)
	}
	target := selection.Targets[0]
	prefix := fmt.Sprintf("deconstruct-%s-", target.EntityID)
	attempt := medicalAttemptCount(goal.History, goal.Goal.Epoch, prefix)
	if attempt >= maxMedicalAttemptsPerPatient {
		return RoutineClearanceResult{Reason: BuildingMethodExhausted}, nil
	}
	method := domain.MethodID(fmt.Sprintf("%s%d", prefix, attempt))
	id := domain.MintPlanID()
	value, err := domain.NewDeconstruction(target.EntityID, target.DefName, target.Minimum)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	action, err := domain.NewDeconstructionAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	actions := []domain.Action{action}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineClearanceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineClearanceResult{}, fmt.Errorf("%w: step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineClearanceResult{}, err
	}
	return RoutineClearanceResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

// dump is the chunk half of the clearance goal (#394): chunks are hauls, not
// deconstructions, so when a pending stack (in Home, allowed, unstored, no
// store will take it) remains, one low-priority dumping stockpile allowing
// the pending chunk kinds and steel slag is admitted on the native outdoor
// footprint, outside held building footprints, and ordinary hauling clears
// the stacks. The method is content-addressed by cells and allow list.
func (r *RoutineClearancePlanner) dump(call, epoch context.Context, state ControlState, goal store.GoalState, reviewTick domain.Tick, census policy.ClearanceCensus, started time.Time) (RoutineClearanceResult, error) {
	if len(policy.PendingChunks(census.Chunks)) == 0 {
		return r.haulChunks(call, epoch, state, goal, census, started)
	}
	held, err := r.reviewer.player.journal.BuildingReservations(call, state.Snapshot)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	var protected []domain.Cell
	for _, h := range held {
		protected = append(protected, h.Footprint...)
	}
	snap.NoteChunkDump(call, snap.ChunkDumpCall{Chunks: census.Chunks, DumpSites: census.DumpSites, Protected: protected})
	cells, allow, ok := policy.SelectChunkDump(census.Chunks, census.DumpSites, protected)
	if !ok {
		return RoutineClearanceResult{Reason: BuildingMethodNoSpace}, nil
	}
	value, err := allowListZone(domain.LowPriority, allow, cells)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	hash := sha256.Sum256([]byte(fmt.Sprintf("%v/%v", allow, cells)))
	method := domain.MethodID(fmt.Sprintf("chunk-dump-%x", hash[:16]))
	result, err := admitZoneMethod(r.reviewer, r.native, call, epoch, state, goal, reviewTick, value, method, started)
	return RoutineClearanceResult{Reason: result.Reason, Plan: result.Plan}, err
}

// chunkHaulWorkTicks bounds one clock window spent letting ordinary hauling
// carry designated chunks; the next review re-reads which are stored.
const chunkHaulWorkTicks = 2500

// maxChunkHaulBatch bounds one chunk-haul method; the next review designates
// the rest.
const maxChunkHaulBatch = 8

// haulChunks designates the chunks a store now takes for hauling (#702): an
// unstored chunk is haulable in vanilla only under a Haul designation, so the
// dump alone never moves it. The method is content-addressed by the chunks
// and their cells, so a batch is ordered once; a chunk still standing where
// it was designated is ordinary hauling's to finish.
func (r *RoutineClearancePlanner) haulChunks(call, epoch context.Context, state ControlState, goal store.GoalState, census policy.ClearanceCensus, started time.Time) (RoutineClearanceResult, error) {
	p := r.reviewer.player
	// A chunk an earlier batch of this epoch ordered is ordinary hauling's
	// (#1234): one that never moves must not hold the batch after it.
	ordered := map[string]domain.Cell{}
	for _, m := range goal.History {
		if !strings.HasPrefix(string(m.Method), "chunk-haul-") {
			continue
		}
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineClearanceResult{}, err
		}
		for _, a := range plan.Spec.Actions() {
			if c, ok := a.CoverClearance(); ok {
				ordered[c.Thing()] = c.Cell()
			}
		}
	}
	var chunks []policy.ClearanceChunk
	waiting := false
	for _, chunk := range policy.HaulableChunks(census.Chunks) {
		if cell, ok := ordered[chunk.EntityID]; ok && cell == chunk.Cell {
			waiting = true
			continue
		}
		chunks = append(chunks, chunk)
	}
	if len(chunks) == 0 && waiting {
		return RoutineClearanceResult{Reason: BuildingMethodUsed, NativeWorkTicks: chunkHaulWorkTicks}, nil
	}
	if len(chunks) > maxChunkHaulBatch {
		chunks = chunks[:maxChunkHaulBatch]
	}
	if len(chunks) == 0 {
		return RoutineClearanceResult{Reason: BuildingMethodUsed}, nil
	}
	var key strings.Builder
	for _, chunk := range chunks {
		fmt.Fprintf(&key, "%s@%d,%d;", chunk.EntityID, chunk.Cell.X, chunk.Cell.Z)
	}
	hash := sha256.Sum256([]byte(key.String()))
	method := domain.MethodID(fmt.Sprintf("chunk-haul-%x", hash[:16]))
	// History, not Methods: the ordered batch's plan retires as soon as the
	// designations land, and re-committing the same method is a conflict.
	for _, m := range goal.History {
		if m.Method == method {
			return RoutineClearanceResult{Reason: BuildingMethodUsed, NativeWorkTicks: chunkHaulWorkTicks}, nil
		}
	}
	id := domain.MintPlanID()
	actions := make([]domain.Action, 0, len(chunks))
	for i, chunk := range chunks {
		clearance, err := domain.NewCoverClearance(chunk.EntityID, chunk.DefName, domain.CoverClearanceHaul, chunk.Cell)
		if err != nil {
			return RoutineClearanceResult{}, err
		}
		action, err := domain.NewCoverClearanceAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), clearance)
		if err != nil {
			return RoutineClearanceResult{}, err
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineClearanceResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineClearanceResult{}, fmt.Errorf("%w: haulChunks: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineClearanceResult{}, err
	}
	return RoutineClearanceResult{Reason: BuildingMethodAdmitted, Plan: id}, nil
}

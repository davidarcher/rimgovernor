package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

const (
	// BuildingHospitalUnavailable: no hosted bed can be flagged medical
	// without evicting a healthy colonist and no bed definition is buildable.
	BuildingHospitalUnavailable RoutineBuildingReason = "hospital_bed_unavailable"
	// BuildingHospitalConvert: a hosted bed awaits its medical patch; the
	// hospital planner, not the building ladder, owns that step.
	BuildingHospitalConvert RoutineBuildingReason = "hospital_bed_convert_pending"
)

// RoutineHospitalSource adds the bed medical CAS read and preview the
// hospital planner needs on top of the building source.
type RoutineHospitalSource interface {
	RoutineBuildingSource
	ReadBedMedicalTarget(context.Context, *c.Identity, string) (bridge.BedMedicalTarget, bridge.Result, error)
	PreviewBedMedical(context.Context, *c.Identity, domain.BedMedical) (*op.PreviewReply, bridge.Result, error)
}

// RoutineHospitalPlanner gives MaintainMedicalCare's patients a hosted
// medical bed: it flags an existing bed in a Hospital-hosting room medical
// (a one-shot BedMedical patch), and when no bed can be spared it stages one
// through the same building ladder EnsureComfort walks (furnish a hosting
// room, else a starter shell first), converting it on a later review.
// Tending, rescue and the medicine reserve stay their own families.
type RoutineHospitalPlanner struct {
	reviewer *RoutineReviewer
	native   RoutineHospitalSource
	building *RoutineBuildingPlanner
}

func NewRoutineHospitalPlanner(reviewer *RoutineReviewer, native RoutineBuildingSource) (*RoutineHospitalPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, ErrControl
	}
	source, ok := native.(RoutineHospitalSource)
	if !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, ErrControl
	}
	if _, ok := native.(observation.TemperatureSource); !ok {
		return nil, ErrControl
	}
	building := &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainMedicalCare, definition: "Wall", shelter: true}
	return &RoutineHospitalPlanner{reviewer: reviewer, native: source, building: building}, nil
}

func (r *RoutineHospitalPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func hospitalRequest(facts observation.ColonyProjection) policy.HospitalRequest {
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill})
	}
	return policy.HospitalRequest{Patients: facts.Facts.MedicalPawns, Sleeping: facts.Facts.Sleeping, Rooms: facts.Rooms, Definitions: definitions}
}

// selectHospital resolves the building ladder's definition from the same
// census the hospital planner chose from: only a HospitalBuild choice
// furnishes; every other outcome is reported, never built around.
func (r *RoutineBuildingPlanner) selectHospital(facts observation.ColonyProjection) (*RoutineBuildingPlanner, RoutineBuildingReason, error) {
	choice, err := policy.SelectHospitalBed(hospitalRequest(facts))
	if err != nil {
		return nil, "", err
	}
	switch choice.Method {
	case policy.HospitalUnknown:
		return nil, BuildingMethodUnknown, nil
	case policy.HospitalNoDemand:
		return nil, BuildingMethodNoDeficit, nil
	case policy.HospitalExisting:
		return nil, BuildingExistingFacility, nil
	case policy.HospitalConvert:
		return nil, BuildingHospitalConvert, nil
	case policy.HospitalUnavailable:
		return nil, BuildingHospitalUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleHospital)
	if err != nil {
		return nil, "", err
	}
	resolved := *r
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	for _, d := range facts.Definitions {
		if d.Name == resolved.definition {
			if stuff, known := d.Stuff.Value(); known {
				resolved.stuff = stuff
			}
		}
	}
	return &resolved, "", nil
}

func (r *RoutineHospitalPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBuildingResult{Reason: BuildingMethodDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineBuildingResult{}, ErrControl
	}
	review, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Reason: BuildingMethodNoReview}, nil
	}
	var goal store.GoalState
	found := false
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainMedicalCare {
			goal, err = p.journal.LoadGoal(call, binding.Goal)
			found = true
			break
		}
	}
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !found || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	}
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBuildingResult{Reason: BuildingMethodExistingWork}, nil
		}
	}
	started := r.reviewer.clock.Now()
	identity, _, err := r.native.Identity(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	expected, err := observation.DecodeIdentity(identity)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !routineBuildingBoundary(expected, state.Snapshot, review.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim](), policy.HospitalBedDefinitions...)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	choice, err := policy.SelectHospitalBed(hospitalRequest(facts))
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if clockSchedulerDebug {
		clockSchedulerLog("hospital: choice=%+v", choice)
	}
	switch choice.Method {
	case policy.HospitalUnknown:
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	case policy.HospitalNoDemand:
		return RoutineBuildingResult{Reason: BuildingMethodNoDeficit}, nil
	case policy.HospitalExisting:
		return RoutineBuildingResult{Reason: BuildingExistingFacility}, nil
	case policy.HospitalUnavailable:
		return RoutineBuildingResult{Reason: BuildingHospitalUnavailable}, nil
	case policy.HospitalBuild:
		return r.building.step(call, epoch, arbiter)
	}
	// Convert: one bed, one CAS-gated patch, once per goal epoch. A method
	// that already ran this epoch (the patch was refused, or a player undid
	// it) is not retried; the next epoch reconsiders.
	method := domain.MethodID("hospital-convert-" + choice.Bed)
	if _, err = p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	target, _, err := r.native.ReadBedMedicalTarget(call, boundary.Identity(state.Snapshot), choice.Bed)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoutineBuildingResult{}, ErrControl
	}
	if target.Medical {
		return RoutineBuildingResult{Reason: BuildingExistingFacility}, nil
	}
	patch, err := domain.NewBedMedical(choice.Bed, true, target.Token)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	preview, _, err := r.native.PreviewBedMedical(call, boundary.Identity(state.Snapshot), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil || !evaluated.GetAccepted() {
		return RoutineBuildingResult{Reason: BuildingMethodRefused}, nil
	}
	if _, err = boundary.Context(evaluated.Context, state.Snapshot); err != nil {
		return RoutineBuildingResult{}, ErrControl
	}
	if !arbiter.tryClaim(nil, "bed:"+choice.Bed) {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	id := domain.PlanID(fmt.Sprintf("routine-hospital-%x", digest[:16]))
	action, err := domain.NewBedMedicalAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, err
	}
	elapsed := r.reviewer.clock.Now().Sub(started)
	if p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge {
		return RoutineBuildingResult{}, ErrControl
	}
	latest, err := p.journal.LoadRoutineReview(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if latest.Revision != review.Revision || !latest.Enabled {
		return RoutineBuildingResult{}, ErrControl
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, nil
}

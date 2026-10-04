package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RoutineHospitalSource adds the bed use read the hospital planner needs on
// top of the building source; Actions/Apply validates the patch itself.
type RoutineHospitalSource interface {
	RoutineBuildingSource
	ReadBedUseTarget(context.Context, *c.Identity, string) (bridge.BedUseTarget, bridge.Result, error)
}

// RoutineHospitalPlanner gives MaintainMedicalReserves's patients a hosted
// medical bed: it flags an existing bed in a Hospital-hosting room medical
// (a one-shot BedUse patch), and when no bed can be spared it stages one
// through the same building ladder EnsureComfort walks (furnish a hosting
// room, else a starter shell first), converting it on a later review.
// Tending, rescue and the medicine reserve stay their own families.
type RoutineHospitalPlanner struct {
	reviewer *Rounder
	native   RoutineHospitalSource
	building *RoutineBuildingPlanner
}

func NewRoutineHospitalPlanner(reviewer *Rounder, native RoutineBuildingSource) (*RoutineHospitalPlanner, error) {
	if reviewer == nil || native == nil {
		return nil, fmt.Errorf("%w: NewRoutineHospitalPlanner: reviewer == nil || native == nil", ErrControl)
	}
	source, ok := native.(RoutineHospitalSource)
	if !ok {
		return nil, fmt.Errorf("%w: NewRoutineHospitalPlanner: !ok", ErrControl)
	}
	if _, ok := native.(observation.RoutineSource); !ok {
		return nil, fmt.Errorf("%w: NewRoutineHospitalPlanner: !ok", ErrControl)
	}
	building := &RoutineBuildingPlanner{reviewer: reviewer, native: native, goal: policy.MaintainMedicalReserves, definition: "Wall", shelter: true}
	return &RoutineHospitalPlanner{reviewer: reviewer, native: source, building: building}, nil
}

func hospitalRequest(facts observation.ColonyProjection) policy.HospitalRequest {
	definitions := make([]policy.BenchDefinition, 0, len(facts.Definitions))
	for _, d := range facts.Definitions {
		definitions = append(definitions, policy.BenchDefinition{Name: d.Name, Available: d.Available, NeedsPower: d.NeedsPower, ConstructionSkill: d.ConstructionSkill})
	}
	f := facts.Facts
	doctors := domain.Unknown[int]()
	if census, known := f.Labor.Value(); known {
		doctors = domain.Known(census[policy.WorkDoctor])
	}
	return policy.HospitalRequest{Patients: f.MedicalPawns, Sleeping: f.Sleeping, Rooms: facts.Rooms, Definitions: definitions, Furniture: facts.Shapes.Furniture,
		Colonists: f.Colonists, HousingTarget: f.HousingTarget, BedCapacity: f.BedCapacity, IndoorCapacity: f.IndoorCapacity, Doctors: doctors,
		Surgical: policy.SurgeryBedShortPatients(f.MedicalPawns)}
}

// selectHospital resolves the building ladder's definition from the same
// census the hospital planner chose from: only a HospitalBuild choice
// furnishes; every other outcome is reported, never built around.
func (r *RoutineBuildingPlanner) selectHospital(facts observation.ColonyProjection) (*RoutineBuildingPlanner, Verdict, error) {
	choice, err := policy.SelectHospitalBed(hospitalRequest(facts))
	if err != nil {
		return nil, Verdict{}, err
	}
	switch choice.Method {
	case policy.HospitalUnknown:
		return nil, fieldUnavailable("hospital"), nil
	case policy.HospitalNoDemand:
		return nil, BuildingReasonNoDeficit, nil
	case policy.HospitalExisting:
		return nil, BuildingExistingFacility, nil
	case policy.HospitalConvert:
		return nil, BuildingHospitalConvert, nil
	case policy.HospitalUnavailable:
		return nil, BuildingHospitalUnavailable, nil
	}
	facility, err := policy.Facility(policy.RoomRoleHospital)
	if err != nil {
		return nil, Verdict{}, err
	}
	resolved := *r
	resolved.definition = choice.Definition
	resolved.environment = policy.PlacementIndoors
	resolved.facility = &facility
	resolved.stuff = facts.BuildStuff(resolved.definition)
	return &resolved, Verdict{}, nil
}

func (r *RoutineHospitalPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoutineBuildingResult{Verdict: BuildingReasonDisabled}, nil
	}
	if !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0 {
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0", ErrControl)
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !review.Enabled || !review.Snapshot.Matches(state.Snapshot) {
		return RoutineBuildingResult{Verdict: BuildingReasonNoReview}, nil
	}
	goal, workable, err := p.journal.Workable(call, review, policy.MaintainMedicalReserves)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !workable || review.Latches.Medical != policy.MedicalCare {
		return RoutineBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	for _, m := range goal.Methods {
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		if store.PlanOpen(plan) {
			return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, nil
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
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: !routineBuildingBoundary(expected, state.Snapshot, review.Tick)", ErrControl)
	}
	reading, err := r.reviewer.observeRooms(call, r.native.(observation.RoutineSource), expected, domain.Unknown[[]policy.ConstructionClaim]())
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	facts := reading.Projection
	recordStepRead("hospital", policy.MaintainMedicalReserves, state.Snapshot, facts)
	choice, err := policy.SelectHospitalBed(hospitalRequest(facts))
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if clockDebug() {
		clockSchedulerLog("hospital: choice=%+v", choice)
	}
	switch choice.Method {
	case policy.HospitalUnknown:
		return RoutineBuildingResult{Verdict: fieldUnavailable("hospital")}, nil
	case policy.HospitalNoDemand:
		return RoutineBuildingResult{Verdict: BuildingReasonNoDeficit}, nil
	case policy.HospitalExisting:
		return RoutineBuildingResult{Verdict: BuildingExistingFacility}, nil
	case policy.HospitalUnavailable:
		return RoutineBuildingResult{Verdict: BuildingHospitalUnavailable}, nil
	case policy.HospitalBuild:
		return r.building.step(call, epoch, arbiter)
	}
	// Convert: one bed, one CAS-gated patch, once per Episode. A method
	// that already ran this epoch (the patch was refused, or a player undid
	// it) is not retried; the next epoch reconsiders.
	method := domain.MethodID("hospital-convert-" + choice.Bed)
	if _, err = p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoutineBuildingResult{Verdict: waitFor(WaitMethodUsed, "hospital_convert_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	target, _, err := r.native.ReadBedUseTarget(call, boundary.Identity(state.Snapshot), choice.Bed)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoutineBuildingResult{}, fmt.Errorf("%w: step: err != nil || target.Context.GetTick() < int64(facts.Identity.Tick)", ErrControl)
	}
	if target.Medical {
		return RoutineBuildingResult{Verdict: BuildingExistingFacility}, nil
	}
	patch, err := domain.NewBedMedical(choice.Bed, true)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if !arbiter.tryClaim(nil, "bed:"+choice.Bed) {
		return RoutineBuildingResult{Verdict: waitFor(WaitMethodUsed, "bed_claim")}, nil
	}
	err = p.commitBedPatch(call, epoch, state, goal, method, patch, func() error {
		if elapsed := r.reviewer.clock.Now().Sub(started); elapsed < 0 || elapsed > r.reviewer.maxAge {
			return fmt.Errorf("%w: step: elapsed < 0 || elapsed > r.reviewer.maxAge", ErrControl)
		}
		latest, err := p.journal.LoadRounds(call)
		if err != nil {
			return err
		}
		if latest.Revision != review.Revision || !latest.Enabled {
			return fmt.Errorf("%w: step: latest.Revision != review.Revision || !latest.Enabled", ErrControl)
		}
		return nil
	})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}

// commitBedPatch commits one CAS-gated BedUse patch as the goal's method:
// the single copy of the tail the hospital convert, the vet-room medical
// bed and the jail bed share. guard, when set, runs after the session and
// epoch checks, last before the commit.
func (p *Player) commitBedPatch(call, epoch context.Context, state ControlState, goal store.StandardState, method domain.MethodID, patch domain.BedUse, guard func() error) error {
	id := domain.MintPlanID()
	action, err := domain.NewBedUseAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return err
	}
	if err = p.current(call, epoch); err != nil {
		return err
	}
	if p.session.State() != state {
		return fmt.Errorf("%w: commitBedPatch: p.session.State() != state", ErrControl)
	}
	if guard != nil {
		if err = guard(); err != nil {
			return err
		}
	}
	_, err = p.journal.CommitMethod(call, goal.Standard.ID, goal.Revision, method, plan)
	return err
}

package buildingruntime

import (
	"context"
)

// Step runs one full scheduling decision; see StepWithReason.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	return s.StepWithReason(ctx, StepReason{Cause: StepFull})
}

func (r *RoutineAcquisitionPlanner) Step(ctx context.Context) (RoutineAcquisitionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineAcquisitionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineAnimalContainmentPlanner) Step(ctx context.Context) (RoutineAnimalContainmentResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineAnimalFeedPlanner) Step(ctx context.Context) (RoutineResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineWorkPlanner) Step(ctx context.Context) (RoutineWorkResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineWorkResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineBillPlanner) Step(ctx context.Context) (RoutineBillResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineBillResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineBlightPlanner) Step(ctx context.Context) (RoutineBlightResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineBlightResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineCleanPlanner) Step(ctx context.Context) (RoutineCleanResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineCleanResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineClearancePlanner) Step(ctx context.Context) (RoutineClearanceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineClearanceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineDefensePlanner) Step(ctx context.Context) (RoutineDefenseResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineDefenseResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineDefenseLayoutPlanner) Step(ctx context.Context) (RoutineDefenseLayoutResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineDefenseLayoutResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineDialogPlanner) Step(ctx context.Context) (RoutineDialogResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineDialogResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineEquipPlanner) Step(ctx context.Context) (RoutineEquipResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineEquipResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineFieldPlanner) Step(ctx context.Context) (RoutineFieldResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineFieldResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineFireSafetyPlanner) Step(ctx context.Context) (RoutineFireSafetyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineFireSafetyResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineFoodStorageUpkeepPlanner) Step(ctx context.Context) (RoutineFoodStorageUpkeepResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineFoodStorageUpkeepResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineArmoryPlanner) Step(ctx context.Context) (RoutineArmoryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineArmoryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineGearPlanner) Step(ctx context.Context) (RoutineGearResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineGearResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineHaulPlanner) Step(ctx context.Context) (RoutineHaulResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineHomeCoveragePlanner) Step(ctx context.Context) (RoutineHomeCoverageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineHomeCoverageResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineHospitalPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineHusbandryPlanner) Step(ctx context.Context) (RoutineHusbandryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineHusbandryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineIngredientStoragePlanner) Step(ctx context.Context) (RoutineIngredientStorageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineIngredientStorageResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineMedicalPlanner) Step(ctx context.Context) (RoutineMedicalResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineMedicalResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineMoodReliefPlanner) Step(ctx context.Context) (RoutineMoodReliefResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineMoodReliefResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineNamingPlanner) Step(ctx context.Context) (RoutineNamingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineNamingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutinePopulationCustodyPlanner) Step(ctx context.Context) (RoutinePopulationCustodyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutinePopulationCustodyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutinePopulationJoinerPlanner) Step(ctx context.Context) (RoutinePopulationJoinerResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutinePopulationJoinerResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutinePrisonerInteractionPlanner) Step(ctx context.Context) (RoutinePrisonerInteractionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutinePrisonerInteractionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineRecoveryPlanner) Step(ctx context.Context) (RoutineRecoveryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineRecoveryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineRepairPlanner) Step(ctx context.Context) (RoutineRepairResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineRepairResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineRescuePlanner) Step(ctx context.Context) (RoutineRescueResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineRescueResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineResearchPlanner) Step(ctx context.Context) (RoutineResearchResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineResearchResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineResourcePlanner) Step(ctx context.Context) (RoutineResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineSecureSuppliesPlanner) Step(ctx context.Context) (RoutineSecureSuppliesResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineSecureSuppliesResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineShrinePlanner) Step(ctx context.Context) (RoutineShrineResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineShrineResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineBuildingPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	call, epoch, done, err := p.enter(ctx, "test", false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineSleepingUpkeepPlanner) Step(ctx context.Context) (RoutineBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineStockpilePlanner) Step(ctx context.Context) (RoutineStockpileResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineStockpileResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineStoneShellPlanner) Step(ctx context.Context) (RoutineStoneShellResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineStoneShellResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineStorageShelvesPlanner) Step(ctx context.Context) (RoutineStorageShelvesResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineStorageShelvesResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoutineSupplyPlanner) Step(ctx context.Context) (RoutineSupplyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineSupplyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineTendPlanner) Step(ctx context.Context) (RoutineTendResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineTendResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineTidyPlanner) Step(ctx context.Context) (RoutineTidyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineTidyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineTradePlanner) Step(ctx context.Context) (RoutineTradeResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineTradeResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoutineWastePlanner) Step(ctx context.Context) (RoutineWasteResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineWasteResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// step delivers ordinary (non-decaying) MaintainStorage items to whatever
// legal storage native picks, reusing the exact same item+hauler selection
// SecureSupplies uses for its own (decaying/vulnerable) item list --
// policy.SelectSecureSupplies is generic over []UpkeepItem/hauler facts, not
// coupled to the SecureSupplies goal itself. policy.ReviewUpkeep keeps the
// two goals' own UpkeepItem selections disjoint (Deterioration == 0 here,
// > 0 for SecureSupplies), so the two planners never race over the same
// real-world item.
func (r *RoutineHaulPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineHaulResult, error) {
	result, err := r.propose(call, epoch)
	if err != nil || result.Kind != PlanProposed {
		return RoutineHaulResult{Verdict: result.Verdict}, err
	}
	return commitClaimed(call, arbiter, result.Proposal)
}

// commitClaimed is the first-arrival path a migrated planner keeps for its
// own Step(): claim the proposal's pawns and entities on arbiter and commit
// at once. The clock step never takes it; there the coordinator ranks the
// wave's proposals first (#622).
func commitClaimed(call context.Context, arbiter *stepArbiter, proposal *Proposal) (RoutineHaulResult, error) {
	if !arbiter.tryClaim(proposal.Claims.Pawns, proposal.Claims.Entities...) {
		return RoutineHaulResult{Verdict: BuildingReasonUsed}, nil
	}
	plan, reason, err := proposal.commit(call)
	if err != nil {
		return RoutineHaulResult{}, err
	}
	return RoutineHaulResult{Verdict: reason, Plan: plan}, nil
}

func (r *RoutineSecureSuppliesPlanner) step(call, epoch context.Context, arbiter *stepArbiter) (RoutineSecureSuppliesResult, error) {
	result, err := r.propose(call, epoch)
	if err != nil || result.Kind != PlanProposed {
		return RoutineSecureSuppliesResult{Verdict: result.Verdict}, err
	}
	got, err := commitClaimed(call, arbiter, result.Proposal)
	return RoutineSecureSuppliesResult(got), err
}

// waitingOn reports the wait recorded for name, for tests and the step row.
func (q *plannerQueue) waitingOn(name string) (plannerWait, bool) {
	wait, ok := q.waits[name]
	return wait, ok
}

func (l *clockLatched) len() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.outcomes)
}

// Wait runs every queued planner and blocks until each returns. It returns
// the step context's error when that is what cut the wave short; otherwise
// the isolated failures are left in Failures and Wait returns nil.
func (g *plannerGroup) Wait() error {
	g.Start()
	<-g.all
	return g.contextError()
}

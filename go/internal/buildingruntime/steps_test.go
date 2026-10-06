package buildingruntime

import (
	"context"
)

// Step runs one full scheduling decision; see StepWithReason.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	return s.StepWithReason(ctx, StepReason{Cause: StepFull})
}

func (r *RoundsAcquisitionPlanner) Step(ctx context.Context) (RoundsAcquisitionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsAcquisitionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsAnimalContainmentPlanner) Step(ctx context.Context) (RoundsAnimalContainmentResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsAnimalFeedPlanner) Step(ctx context.Context) (RoundsResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsWorkPlanner) Step(ctx context.Context) (RoundsWorkResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsWorkResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsBillPlanner) Step(ctx context.Context) (RoundsBillResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsBillResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsBlightPlanner) Step(ctx context.Context) (RoundsBlightResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsBlightResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsCleanPlanner) Step(ctx context.Context) (RoundsCleanResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsCleanResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsClearancePlanner) Step(ctx context.Context) (RoundsClearanceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsClearanceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsDefensePlanner) Step(ctx context.Context) (RoundsDefenseResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsDefenseResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsDefenseLayoutPlanner) Step(ctx context.Context) (RoundsDefenseLayoutResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsDefenseLayoutResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsDialogPlanner) Step(ctx context.Context) (RoundsDialogResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsDialogResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsEquipPlanner) Step(ctx context.Context) (RoundsEquipResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsEquipResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsFieldPlanner) Step(ctx context.Context) (RoundsFieldResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsFieldResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsFireSafetyPlanner) Step(ctx context.Context) (RoundsFireSafetyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsFireSafetyResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoundsFoodStorageUpkeepPlanner) Step(ctx context.Context) (RoundsFoodStorageUpkeepResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsFoodStorageUpkeepResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsArmoryPlanner) Step(ctx context.Context) (RoundsArmoryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsArmoryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsGearPlanner) Step(ctx context.Context) (RoundsGearResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsGearResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsHomeCoveragePlanner) Step(ctx context.Context) (RoundsHomeCoverageResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsHomeCoverageResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsHospitalPlanner) Step(ctx context.Context) (RoundsBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsHusbandryPlanner) Step(ctx context.Context) (RoundsHusbandryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsHusbandryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsMedicalPlanner) Step(ctx context.Context) (RoundsMedicalResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsMedicalResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsMoodReliefPlanner) Step(ctx context.Context) (RoundsMoodReliefResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsMoodReliefResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsNamingPlanner) Step(ctx context.Context) (RoundsNamingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsNamingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsPopulationCustodyPlanner) Step(ctx context.Context) (RoundsPopulationCustodyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsPopulationCustodyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsPopulationJoinerPlanner) Step(ctx context.Context) (RoundsPopulationJoinerResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsPopulationJoinerResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsPrisonerInteractionPlanner) Step(ctx context.Context) (RoundsPrisonerInteractionResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsPrisonerInteractionResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsRecoveryPlanner) Step(ctx context.Context) (RoundsRecoveryResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsRecoveryResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsRepairPlanner) Step(ctx context.Context) (RoundsRepairResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsRepairResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsRescuePlanner) Step(ctx context.Context) (RoundsRescueResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsRescueResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsResearchPlanner) Step(ctx context.Context) (RoundsResearchResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsResearchResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsResourcePlanner) Step(ctx context.Context) (RoundsResourceResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsResourceResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsShrinePlanner) Step(ctx context.Context) (RoundsShrineResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsShrineResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsBuildingPlanner) Step(ctx context.Context) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	call, epoch, done, err := p.enter(ctx, "test", false)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsSleepingUpkeepPlanner) Step(ctx context.Context) (RoundsBuildingResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsStockpilePlanner) Step(ctx context.Context) (RoundsStockpileResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsStockpileResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsStoneShellPlanner) Step(ctx context.Context) (RoundsStoneShellResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsStoneShellResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsStorageShelvesPlanner) Step(ctx context.Context) (RoundsStorageShelvesResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsStorageShelvesResult{}, err
	}
	defer done()
	return r.step(call, epoch)
}

func (r *RoundsSupplyPlanner) Step(ctx context.Context) (RoundsSupplyResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsSupplyResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsTendPlanner) Step(ctx context.Context) (RoundsTendResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsTendResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

func (r *RoundsTradePlanner) Step(ctx context.Context) (RoundsTradeResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoundsTradeResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
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

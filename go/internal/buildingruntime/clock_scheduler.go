package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type ClockWindowNative interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
	ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
}
type ClockSchedulerConfig struct {
	CookingBills, PreservationBills, ButcherBills *RoutineBillPlanner
	Butcher                                       *RoutineBuildingPlanner
	Fields                                        *RoutineFieldPlanner
	FoodStorage                                   *RoutineFoodStoragePlanner
	Profile                                       string
	Start                                         bridge.ClockStart
	MaxAge                                        time.Duration
	// CombatMaxTicks bounds each window admitted while the ActiveCombat goal
	// holds an admitted plan and hostiles are alive; zero keeps Start.MaxTicks.
	// Short windows let the raid be re-planned between them.
	CombatMaxTicks uint32
	// Routine is reviewed only after owned clock obligations have drained.
	Routine                          *RoutineReviewer
	FoodAcquisition, WoodAcquisition *RoutineAcquisitionPlanner
	Work                             *RoutineWorkPlanner
	Supplies                         *RoutineSupplyPlanner
	Sleeping                         *RoutineBuildingPlanner
	Cooking                          *RoutineBuildingPlanner
	Comfort                          *RoutineBuildingPlanner
	Workshop                         *RoutineBuildingPlanner
	Expansion                        *RoutineBuildingPlanner
	Power                            *RoutineBuildingPlanner
	Temperature                      *RoutineBuildingPlanner
	Refrigeration                    *RoutineBuildingPlanner
	Lighting                         *RoutineBuildingPlanner
	Flooring                         *RoutineBuildingPlanner
	Defense                          *RoutineDefensePlanner
	Tend                             *RoutineTendPlanner
	Rescue                           *RoutineRescuePlanner
	Equip                            *RoutineEquipPlanner
	SecureSupplies                   *RoutineSecureSuppliesPlanner
	Repair                           *RoutineRepairPlanner
	FireSafety                       *RoutineFireSafetyPlanner
	Clean                            *RoutineCleanPlanner
	Haul                             *RoutineHaulPlanner
	Gear                             *RoutineGearPlanner
	Medical                          *RoutineMedicalPlanner
	FoodStorageUpkeep                *RoutineFoodStorageUpkeepPlanner
	AnimalContainment                *RoutineAnimalContainmentPlanner
	Recovery                         *RoutineRecoveryPlanner
	Husbandry                        *RoutineHusbandryPlanner
	PrisonerInteraction              *RoutinePrisonerInteractionPlanner
	PopulationCustody                *RoutinePopulationCustodyPlanner
	Research                         *RoutineResearchPlanner
	Resource                         *RoutineResourcePlanner
	AnimalFeed                       *RoutineAnimalFeedPlanner
	ProductionPolicy                 *RoutineProductionPolicyPlanner
	CaravanJourney                   *CaravanJourneyTracker
	HomeCoverage                     *RoutineHomeCoveragePlanner
	StoneShell                       *RoutineStoneShellPlanner
	DefenseLayout                    *RoutineDefenseLayoutPlanner
	Waste                            *RoutineWastePlanner
	MoodRelief                       *RoutineMoodReliefPlanner
	Naming                           *RoutineNamingPlanner
	RoutineMethods                   bool
}
type ClockSchedulerResult struct {
	CookingBills, PreservationBills, ButcherBills *RoutineBillResult
	Butcher                                       *RoutineBuildingResult
	Fields                                        *RoutineFieldResult
	FoodStorage                                   *RoutineFoodStorageResult
	Attempt                                       *store.ClockAttempt
	Decision                                      policy.ClockWindowDecision
	Routine                                       *store.RoutineReviewResult
	FoodAcquisition, WoodAcquisition              *RoutineAcquisitionResult
	Work                                          *RoutineWorkResult
	Supplies                                      *RoutineSupplyResult
	Sleeping                                      *RoutineBuildingResult
	Cooking                                       *RoutineBuildingResult
	Comfort                                       *RoutineBuildingResult
	Workshop                                      *RoutineBuildingResult
	Expansion                                     *RoutineBuildingResult
	Power                                         *RoutineBuildingResult
	Temperature                                   *RoutineBuildingResult
	Refrigeration                                 *RoutineBuildingResult
	Lighting                                      *RoutineBuildingResult
	Flooring                                      *RoutineBuildingResult
	Defense                                       *RoutineDefenseResult
	Tend                                          *RoutineTendResult
	Rescue                                        *RoutineRescueResult
	Equip                                         *RoutineEquipResult
	SecureSupplies                                *RoutineSecureSuppliesResult
	Repair                                        *RoutineRepairResult
	FireSafety                                    *RoutineFireSafetyResult
	Clean                                         *RoutineCleanResult
	Haul                                          *RoutineHaulResult
	Gear                                          *RoutineGearResult
	Medical                                       *RoutineMedicalResult
	FoodStorageUpkeep                             *RoutineFoodStorageUpkeepResult
	AnimalContainment                             *RoutineAnimalContainmentResult
	Recovery                                      *RoutineRecoveryResult
	Husbandry                                     *RoutineHusbandryResult
	PrisonerInteraction                           *RoutinePrisonerInteractionResult
	PopulationCustody                             *RoutinePopulationCustodyResult
	Research                                      *RoutineResearchResult
	Resource                                      *RoutineResourceResult
	AnimalFeed                                    *RoutineResourceResult
	ProductionPolicy                              *RoutineProductionPolicyResult
	CaravanJourney                                *CaravanJourneyResult
	HomeCoverage                                  *RoutineHomeCoverageResult
	StoneShell                                    *RoutineStoneShellResult
	DefenseLayout                                 *RoutineDefenseLayoutResult
	Waste                                         *RoutineWasteResult
	MoodRelief                                    *RoutineMoodReliefResult
	Naming                                        *RoutineNamingResult
	Running, Reconciled, Cleaned                  bool
	// Combat is set while a combat watch window was admitted or is running,
	// so the worker keeps its short poll instead of backing off.
	Combat bool
	// PlannerFailures holds the errors of planners that failed this step, each
	// wrapped with the planner's name. A failed planner does not abort the
	// step: its peers still run and the clock window is still evaluated (#62).
	PlannerFailures []error
}
type ClockScheduler struct {
	player              *Player
	session             *Session
	native              ClockWindowNative
	config              ClockSchedulerConfig
	clock               executor.Clock
	pollGate, renewGate chan struct{}

	// tickTrace is a TEMPORARY diagnostic aid (RIMGOVERNOR_CLOCK_DEBUG=1),
	// read and written only from Step() which the ClockWorker's stepLoop
	// calls serially -- see clockSchedulerLog. It lets a live run directly
	// answer G01.07b's follow-up open question (issue tracking the
	// clock-restart-cadence gap): whether native ticks actually advance
	// during a "running" burst (would show a healthy per-step tick delta)
	// or the clock is running in name only (delta stays ~0 across many
	// real seconds), versus the cumulative-across-the-run total showing
	// whether restart cadence alone, not lost ticks, is the bottleneck.
	tickTraceValid bool
	tickTraceTick  int64
	tickTraceAt    time.Time
	tickTraceStart int64
}

func NewClockScheduler(player *Player, session *Session, native ClockWindowNative, config ClockSchedulerConfig, clock executor.Clock) (*ClockScheduler, error) {
	if player == nil || session == nil || player.session != session || player.journal != session.journal || session.clock == nil || native == nil || clock == nil || config.MaxAge <= 0 || config.MaxAge > time.Minute {
		return nil, ErrControl
	}
	if config.Start.Policy == nil {
		return nil, ErrControl
	}
	if config.Routine != nil && config.Routine.player != player {
		return nil, ErrControl
	}
	if config.CaravanJourney != nil && config.CaravanJourney.player != player {
		return nil, ErrControl
	}
	for _, planner := range []*RoutineBillPlanner{config.CookingBills, config.PreservationBills, config.ButcherBills} {
		if planner != nil && (config.Routine == nil || planner.reviewer != config.Routine) {
			return nil, ErrControl
		}
	}
	if config.Butcher != nil && (config.Routine == nil || config.Butcher.reviewer != config.Routine || config.Butcher.goal != policy.EnsureFoodSupply) {
		return nil, ErrControl
	}
	if config.Fields != nil && (config.Routine == nil || config.Fields.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.FoodStorage != nil && (config.Routine == nil || config.FoodStorage.reviewer != config.Routine) {
		return nil, ErrControl
	}
	for _, planner := range []*RoutineAcquisitionPlanner{config.FoodAcquisition, config.WoodAcquisition} {
		if planner != nil && (config.Routine == nil || planner.reviewer != config.Routine) {
			return nil, ErrControl
		}
	}
	if config.Work != nil && (config.Routine == nil || config.Work.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Supplies != nil && (config.Routine == nil || config.Supplies.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Sleeping != nil && (config.Routine == nil || config.Sleeping.reviewer != config.Routine || config.Sleeping.goal != policy.EnsureInitialShelter) {
		return nil, ErrControl
	}
	if config.Cooking != nil && (config.Routine == nil || config.Cooking.reviewer != config.Routine || config.Cooking.goal != policy.EnsureCooking) {
		return nil, ErrControl
	}
	if config.Comfort != nil && (config.Routine == nil || config.Comfort.reviewer != config.Routine || config.Comfort.goal != policy.EnsureComfort) {
		return nil, ErrControl
	}
	if config.Workshop != nil && (config.Routine == nil || config.Workshop.reviewer != config.Routine || config.Workshop.goal != policy.MaintainResource) {
		return nil, ErrControl
	}
	if config.Expansion != nil && (config.Routine == nil || config.Expansion.reviewer != config.Routine || config.Expansion.goal != policy.EnsureExpansion) {
		return nil, ErrControl
	}
	if config.Power != nil && (config.Routine == nil || config.Power.reviewer != config.Routine || config.Power.goal != policy.EnsureBasicPower) {
		return nil, ErrControl
	}
	if config.Temperature != nil && (config.Routine == nil || config.Temperature.reviewer != config.Routine || config.Temperature.goal != policy.EnsureTemperatureSafety) {
		return nil, ErrControl
	}
	if config.Refrigeration != nil && (config.Routine == nil || config.Refrigeration.reviewer != config.Routine || config.Refrigeration.goal != policy.MaintainRefrigeration) {
		return nil, ErrControl
	}
	if config.Lighting != nil && (config.Routine == nil || config.Lighting.reviewer != config.Routine || config.Lighting.goal != policy.MaintainLighting) {
		return nil, ErrControl
	}
	if config.Flooring != nil && (config.Routine == nil || config.Flooring.reviewer != config.Routine || config.Flooring.goal != policy.MaintainFlooring) {
		return nil, ErrControl
	}
	if config.Defense != nil && (config.Routine == nil || config.Defense.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Tend != nil && (config.Routine == nil || config.Tend.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Rescue != nil && (config.Routine == nil || config.Rescue.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Equip != nil && (config.Routine == nil || config.Equip.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.SecureSupplies != nil && (config.Routine == nil || config.SecureSupplies.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Repair != nil && (config.Routine == nil || config.Repair.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.FireSafety != nil && (config.Routine == nil || config.FireSafety.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Clean != nil && (config.Routine == nil || config.Clean.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Waste != nil && (config.Routine == nil || config.Waste.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.MoodRelief != nil && (config.Routine == nil || config.MoodRelief.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Haul != nil && (config.Routine == nil || config.Haul.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.AnimalContainment != nil && (config.Routine == nil || config.AnimalContainment.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Gear != nil && (config.Routine == nil || config.Gear.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Medical != nil && (config.Routine == nil || config.Medical.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.FoodStorageUpkeep != nil && (config.Routine == nil || config.FoodStorageUpkeep.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Recovery != nil && (config.Routine == nil || config.Recovery.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Husbandry != nil && (config.Routine == nil || config.Husbandry.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.PrisonerInteraction != nil && (config.Routine == nil || config.PrisonerInteraction.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.PopulationCustody != nil && (config.Routine == nil || config.PopulationCustody.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Research != nil && (config.Routine == nil || config.Research.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Naming != nil && (config.Routine == nil || config.Naming.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Resource != nil && (config.Routine == nil || config.Resource.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.AnimalFeed != nil && (config.Routine == nil || config.AnimalFeed.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.HomeCoverage != nil && (config.Routine == nil || config.HomeCoverage.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.StoneShell != nil && (config.Routine == nil || config.StoneShell.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.DefenseLayout != nil && (config.Routine == nil || config.DefenseLayout.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.ProductionPolicy != nil && (config.Routine == nil || config.ProductionPolicy.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.RoutineMethods && (config.Routine == nil || !session.routineMethods) {
		return nil, ErrControl
	}
	config.Start.Policy = proto.Clone(config.Start.Policy).(*k.WatchPolicy)
	p := config.Start.Policy
	if p.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(p.AcknowledgedHostileIds)+len(p.AcknowledgedDownedColonistIds)+len(p.AcknowledgedInjuredColonistIds)+len(p.SurgicalRecoveryIds)+len(p.MedicalRestIds) != 0 || p.GetInjuryStopCooldownMs() != 0 {
		return nil, ErrControl
	}
	if config.CombatMaxTicks > config.Start.MaxTicks {
		return nil, ErrControl
	}
	// Validate command arguments through the canonical bridge validator; these
	// fixed validation identities carry no runtime permission.
	err := bridge.ValidateClockExpectation(bridge.ClockExpectation{Identity: &c.Identity{ColonyId: proto.String("validation"), LoadToken: proto.String("validation"), MapId: proto.Int32(0)}, Attempt: &c.AttemptKey{ControllerSessionId: proto.String("validation"), ActionId: proto.String("validation"), AttemptId: proto.Uint64(1)}, NativeGeneration: 1, Command: bridge.ClockCommand{Start: &config.Start}})
	if err != nil {
		return nil, err
	}
	profile, err := os.Stat(config.Profile)
	if err != nil {
		return nil, err
	}
	ownedProfile, err := os.Stat(session.control.config.ProfileDirectory)
	if err != nil {
		return nil, err
	}
	if !profile.IsDir() || !ownedProfile.IsDir() || !os.SameFile(profile, ownedProfile) {
		return nil, ErrControl
	}
	ctx, cancel := context.WithTimeout(player.lifetime, player.config.JournalTimeout)
	defer cancel()
	inbox, err := player.journal.BindClockInbox(ctx, config.Profile)
	if err != nil {
		return nil, err
	}
	config.Profile = inbox.Profile
	return &ClockScheduler{player: player, session: session, native: native, config: config, clock: clock, pollGate: make(chan struct{}, 1), renewGate: make(chan struct{}, 1)}, nil
}

var clockSchedulerDebug = os.Getenv("RIMGOVERNOR_CLOCK_DEBUG") != ""

// clockSchedulerLog is a TEMPORARY diagnostic aid (RIMGOVERNOR_CLOCK_DEBUG=1)
// for tracing which Step() branch is taken; added while root-causing the
// clock-restart-cadence gap surfaced by G01.07b's RoutineHaulPlanner
// acceptance work (see follow-up issue for MaintainStorage haul completion).
func clockSchedulerLog(format string, args ...any) {
	if clockSchedulerDebug {
		fmt.Fprintf(os.Stderr, "[clock-scheduler] "+format+"\n", args...)
	}
}

// Step performs at most one scheduling decision. It never acquires authority,
// renews an epoch, acknowledges events, or starts a background loop.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	var out ClockSchedulerResult
	entered := time.Now()
	call, epoch, done, err := s.player.enter(ctx, false)
	if err != nil {
		return out, err
	}
	defer done()
	if wait := time.Since(entered); wait > 50*time.Millisecond {
		clockSchedulerLog("step waited %s for the player gate", wait.Round(time.Millisecond))
	}
	// Every native observation this step issues -- the identity and status
	// reads below, the routine census and each planner's own reads -- goes
	// through one cache that lives exactly as long as the step, so the
	// facts the planners share are read from native once per tick. A write
	// within the step discards it; see bridge.StepReadCache.
	cache := bridge.NewStepReadCache()
	call = bridge.WithStepReadCache(call, cache)
	// The round trips that still cross the bridge (cache misses, the
	// uncacheable reads, writes) are tallied by tool so the cost of the
	// composition is visible per step: on stderr under
	// RIMGOVERNOR_CLOCK_DEBUG=1 beside the cache's hit/miss counts, and as a
	// clock_step row in the flight recorder, which `rimgovernor phases`
	// reports as reads/step.
	call, reads := bridge.WithReadTally(call)
	stepBegan := time.Now()
	defer func() {
		elapsed := time.Since(stepBegan)
		stats := cache.Stats()
		clockSchedulerLog("step reads: %s cache hits=%d misses=%d coalesced=%d invalidations=%d running=%v elapsed=%s", reads, stats.Hits, stats.Misses, stats.Coalesced, stats.Invalidations, out.Running, elapsed.Round(time.Millisecond))
		reads.Publish(call, map[string]any{"cache_hits": stats.Hits, "running": out.Running, "elapsed_ms": float64(elapsed) / float64(time.Millisecond)})
	}()
	attempts, err := s.player.journal.LoadClockAttempts(call, 4096)
	if err != nil {
		return out, err
	}
	started := s.clock.Now()
	identity, _, err := s.native.Identity(call)
	if err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	loaded := identity.GetLoaded()
	if loaded == nil {
		return out, errors.Join(executor.ErrHeld, s.session.Disable())
	}
	if err = bridge.ValidateContext(loaded.Context); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	state := s.session.State()
	world := domain.GenerationSnapshot{Colony: domain.ColonyID(loaded.Context.Identity.GetColonyId()), Load: domain.LoadID(loaded.Context.Identity.GetLoadToken()), Map: domain.MapID(loaded.Context.Identity.GetMapId())}
	epochs, err := s.player.journal.LoadClockEpochs(call, 4096)
	if err != nil {
		return out, err
	}
	obligations := false
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			obligations = true
		}
	}
	for _, v := range attempts {
		if v.SupersededAt == nil && v.Intent.Command.Start != nil && (v.Phase == store.ClockDispatched || v.Phase == store.ClockUncertain) {
			if !state.Enabled || !boundary.World(v.Intent.Snapshot, world) {
				out.Cleaned = true
				return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
			}
			recovered, e := s.session.ReconcileClock(call, v.Intent.RequestID)
			out.Attempt = &recovered
			out.Reconciled = true
			return out, e
		}
	}
	if obligations && (!state.Enabled || !state.ObservationKnown || !boundary.World(state.Snapshot, world) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native)) {
		out.Cleaned = true
		return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
	}
	if !state.ObservationKnown || !boundary.World(state.Snapshot, domain.GenerationSnapshot{Colony: domain.ColonyID(loaded.Context.Identity.GetColonyId()), Load: domain.LoadID(loaded.Context.Identity.GetLoadToken()), Map: domain.MapID(loaded.Context.Identity.GetMapId())}) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native) {
		return out, errors.Join(executor.ErrAuthority, s.session.Disable())
	}
	statusReply, _, err := s.native.ReadClockStatus(call, loaded.Context.Identity)
	if err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	status := statusReply.GetStatus()
	if err = bridge.ValidateClockStatus(status, loaded.Context.Identity); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	if _, err = boundary.Context(status.Context, state.Snapshot); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	if status.Context.GetTick() < loaded.Context.GetTick() {
		return out, errors.Join(executor.ErrEvidence, s.session.Disable())
	}
	if clockSchedulerDebug {
		now := s.clock.Now()
		tick := status.Context.GetTick()
		if !s.tickTraceValid {
			s.tickTraceStart = tick
		}
		var deltaTick int64
		var deltaMs int64
		if s.tickTraceValid {
			deltaTick = tick - s.tickTraceTick
			deltaMs = now.Sub(s.tickTraceAt).Milliseconds()
		}
		clockSchedulerLog("status: running=%v stopping=%v stopped=%v neverStarted=%v stopReason=%v tick=%d deltaTick=%d deltaMs=%d cumulativeTick=%d",
			status.GetRunning() != nil, status.GetStopping() != nil, status.GetStopped() != nil, status.GetNeverStarted() != nil,
			status.GetStopped().GetReason(), tick, deltaTick, deltaMs, tick-s.tickTraceStart)
		s.tickTraceTick, s.tickTraceAt, s.tickTraceValid = tick, now, true
	}
	if status.GetRunning() != nil || status.GetStopping() != nil {
		var actual *k.Epoch
		if status.GetRunning() != nil {
			actual = status.GetRunning().Epoch
		} else {
			actual = status.GetStopping().Epoch
		}
		ownedCurrent := false
		for _, owned := range epochs {
			if !clockCoordinatorTerminal(owned.Stage) && clockCoordinatorSameEpoch(owned.Epoch, actual) {
				for _, attempt := range attempts {
					if attempt.Intent.RequestID == owned.StartRequestID && attempt.Intent.Snapshot == state.Snapshot {
						ownedCurrent = true
						out.Combat = attempt.Intent.Command.Start != nil && attempt.Intent.Command.Start.Policy.GetMode() == k.WatchMode_WATCH_MODE_COMBAT
					}
				}
			}
		}
		if (status.GetStopping() != nil || !ownedCurrent) && obligations {
			out.Cleaned = true
			return out, s.session.CleanupClock(call)
		}
		if !state.Enabled || !ownedCurrent {
			out.Combat = false
			return out, executor.ErrHeld
		}
		out.Running = true
		clockSchedulerLog("clock already running under our own epoch -> skip planners this tick")
		return out, nil
	}
	if obligations {
		out.Cleaned = true
		clockSchedulerLog("obligations present, not running -> cleanup")
		return out, s.session.CleanupClock(call)
	}
	if !state.Enabled {
		return out, executor.ErrAuthority
	}
	if err = s.player.current(call, epoch); err != nil {
		return out, err
	}
	clockSchedulerLog("reached stepPlanners")
	arbiter := newStepArbiter()
	g := newPlannerGroup(call, plannerWidth)
	gctx := call
	if err = s.stepPlanners(call, epoch, &out, g, arbiter); err != nil {
		return out, err
	}
	if s.config.SecureSupplies != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.SecureSupplies.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("secureSupplies: %w", err)
			}
			out.SecureSupplies = &method
			return nil
		})
	}
	if s.config.Repair != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Repair.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("repair: %w", err)
			}
			out.Repair = &method
			return nil
		})
	}
	if s.config.FireSafety != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.FireSafety.step(gctx, epoch)
			if err != nil {
				return fmt.Errorf("fireSafety: %w", err)
			}
			clockSchedulerLog("FireSafety.step result: reason=%v outcome=%v nativeWorkTicks=%d", method.Reason, method.Outcome, method.NativeWorkTicks)
			out.FireSafety = &method
			return nil
		})
	}
	if s.config.Clean != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Clean.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("clean: %w", err)
			}
			out.Clean = &method
			return nil
		})
	}
	if s.config.Waste != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Waste.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("waste: %w", err)
			}
			out.Waste = &method
			return nil
		})
	}
	if s.config.MoodRelief != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.MoodRelief.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("moodRelief: %w", err)
			}
			out.MoodRelief = &method
			return nil
		})
	}
	if s.config.Haul != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Haul.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("haul: %w", err)
			}
			clockSchedulerLog("Haul.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.Haul = &method
			return nil
		})
	}
	if s.config.Gear != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Gear.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("gear: %w", err)
			}
			out.Gear = &method
			return nil
		})
	}
	if s.config.Medical != nil {
		g.Go(plannerCritical, func() error {
			method, err := s.config.Medical.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("medical: %w", err)
			}
			clockSchedulerLog("Medical.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.Medical = &method
			return nil
		})
	}
	if s.config.FoodStorageUpkeep != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.FoodStorageUpkeep.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("foodStorageUpkeep: %w", err)
			}
			out.FoodStorageUpkeep = &method
			return nil
		})
	}
	if s.config.AnimalContainment != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.AnimalContainment.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("animalContainment: %w", err)
			}
			out.AnimalContainment = &method
			return nil
		})
	}
	if s.config.Recovery != nil {
		g.Go(plannerCritical, func() error {
			method, err := s.config.Recovery.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("recovery: %w", err)
			}
			out.Recovery = &method
			return nil
		})
	}
	if s.config.Husbandry != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Husbandry.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("husbandry: %w", err)
			}
			out.Husbandry = &method
			return nil
		})
	}
	if s.config.PrisonerInteraction != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.PrisonerInteraction.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("prisonerInteraction: %w", err)
			}
			out.PrisonerInteraction = &method
			return nil
		})
	}
	if s.config.PopulationCustody != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.PopulationCustody.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("populationCustody: %w", err)
			}
			out.PopulationCustody = &method
			return nil
		})
	}
	if s.config.Research != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Research.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("research: %w", err)
			}
			out.Research = &method
			return nil
		})
	}
	if s.config.Naming != nil {
		g.Go(plannerPreempt, func() error {
			method, err := s.config.Naming.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("naming: %w", err)
			}
			out.Naming = &method
			return nil
		})
	}
	if s.config.Resource != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Resource.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("resource: %w", err)
			}
			out.Resource = &method
			return nil
		})
	}
	if s.config.AnimalFeed != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.AnimalFeed.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("animalFeed: %w", err)
			}
			clockSchedulerLog("AnimalFeed.step result: reason=%v plan=%s", method.Reason, method.Plan)
			out.AnimalFeed = &method
			return nil
		})
	}
	if s.config.ProductionPolicy != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.ProductionPolicy.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("productionPolicy: %w", err)
			}
			out.ProductionPolicy = &method
			return nil
		})
	}
	if s.config.CaravanJourney != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.CaravanJourney.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("caravanJourney: %w", err)
			}
			out.CaravanJourney = &method
			return nil
		})
	}
	if s.config.HomeCoverage != nil {
		g.Go(plannerComfort, func() error {
			method, err := s.config.HomeCoverage.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("homeCoverage: %w", err)
			}
			out.HomeCoverage = &method
			return nil
		})
	}
	if s.config.StoneShell != nil {
		g.Go(plannerComfort, func() error {
			method, err := s.config.StoneShell.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("stoneShell: %w", err)
			}
			out.StoneShell = &method
			return nil
		})
	}
	if s.config.DefenseLayout != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.DefenseLayout.step(gctx, epoch)
			if err != nil {
				return fmt.Errorf("defenseLayout: %w", err)
			}
			clockSchedulerLog("defense-layout.step: reason=%s tier=%s plan=%s", method.Reason, method.Tier, method.Plan)
			out.DefenseLayout = &method
			return nil
		})
	}
	if err = g.Wait(); err != nil {
		return out, err
	}
	// A failed planner is reported, not fatal: the step still evaluates the
	// clock window on what the other planners committed, and the failed
	// planner retries next step (#62).
	out.PlannerFailures = g.Failures()
	for _, failure := range out.PlannerFailures {
		clockSchedulerLog("planner failed (isolated): %v", failure)
	}
	emergency, _, err := s.native.ReadEmergency(call, loaded.Context.Identity)
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, state.Snapshot); err != nil {
		return out, err
	}
	emergencyFacts, err := policy.NewEmergencySnapshot(state.Snapshot, domain.Tick(emergency.Context.GetTick()), emergency.Facts)
	if err != nil {
		return out, err
	}
	review, err := s.player.journal.ReadClockReview(call, s.config.Profile)
	if err != nil {
		return out, err
	}
	plan, err := s.player.journal.LoadPlan(call, state.Snapshot.Plan)
	if err != nil {
		return out, err
	}
	work, fingerprint, err := clockSchedulerWork(plan, state.Snapshot)
	if err != nil {
		return out, err
	}
	{
		plans, err := s.player.journal.LoadPlans(call, 256)
		if err != nil {
			return out, err
		}
		for _, method := range plans {
			if method.Spec.ID() == state.Snapshot.Plan {
				continue
			}
			target := state.Snapshot
			target.Plan, target.Revision = method.Spec.ID(), method.Spec.Revision()
			if err := (planAuthorizer{s.player.journal, s.config.RoutineMethods}).AuthorizeRoutinePlan(call, state.Snapshot, target); err != nil {
				continue
			}
			remaining, items, err := clockSchedulerWork(method, target)
			if err != nil {
				return out, err
			}
			work = work || remaining
			fingerprint = append(fingerprint, items...)
		}
	}
	combatPlan, err := clockSchedulerCombatPlan(call, s.player.journal, state.Snapshot)
	if err != nil {
		return out, err
	}
	clockState := policy.ClockWindowState("")
	start := s.config.Start
	var nativeWorkTicks uint32
	if out.Fields != nil {
		nativeWorkTicks = out.Fields.NativeWorkTicks
	}
	for _, result := range []*RoutineBuildingResult{out.Sleeping, out.Comfort, out.Workshop, out.Expansion, out.Power, out.Temperature, out.Refrigeration, out.Lighting, out.Flooring} {
		if result != nil {
			nativeWorkTicks = max(nativeWorkTicks, result.NativeWorkTicks)
		}
	}
	// A bounded home fire is fought by native firefighters, never by an
	// order: the planner's only method is a short clock window.
	if out.FireSafety != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.FireSafety.NativeWorkTicks)
	}
	if !work && s.config.RoutineMethods && nativeWorkTicks > 0 {
		work = true
		start.MaxTicks = min(start.MaxTicks, nativeWorkTicks)
	}
	if status.GetNeverStarted() != nil {
		clockState = policy.ClockNeverStarted
	}
	if status.GetStopped() != nil {
		clockState = policy.ClockStopped
	}
	facts := policy.ClockWindowFacts{Current: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), StartedAt: started, ObservedAt: s.clock.Now(), Emergency: emergencyFacts, Review: policy.ClockWindowReview{Revision: review.Revision, Captured: review.InboxCursor, Reviewed: review.ReviewedCursor, Acknowledged: review.AcknowledgedCursor, HasHolds: domain.Known(len(review.Holds) > 0)}, Status: policy.ClockWindowStatus{Snapshot: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), State: clockState, ActualPaused: boundary.FactBool(status.ActualPaused), NativeTickBoundary: boundary.FactBool(status.NativeTickBoundary), DurableEvents: boundary.FactBool(status.DurableEvents)}, Obligations: policy.ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(work), CombatPlan: domain.Known(combatPlan)}
	if status.NewestCursor != nil {
		facts.Status.NewestCursor = domain.Known(status.GetNewestCursor())
	}
	combatMaxTicks := min(s.config.CombatMaxTicks, start.MaxTicks)
	out.Decision = policy.EvaluateClockWindow(facts, policy.ClockWindowLimits{Now: s.clock.Now(), MaxAge: s.config.MaxAge, MaxTicks: start.MaxTicks, CombatMaxTicks: combatMaxTicks})
	clockSchedulerLog("EvaluateClockWindow: work=%v combatPlan=%v admitted=%v mode=%s hostiles=%v refused=%v", work, combatPlan, out.Decision.Admitted, out.Decision.Mode, out.Decision.Hostiles, out.Decision.Refused)
	if !out.Decision.Admitted {
		return out, executor.ErrHeld
	}
	start.Policy = proto.Clone(start.Policy).(*k.WatchPolicy)
	if out.Decision.Mode == policy.ClockWindowCombat {
		// The native watcher stops on any unacknowledged hostile within
		// HostileWithin; a combat window acknowledges exactly the live
		// hostiles the policy admitted and runs under the combat budget.
		out.Combat = true
		start.Policy.Mode = k.WatchMode_WATCH_MODE_COMBAT.Enum()
		start.Policy.AcknowledgedHostileIds = make([]string, 0, len(out.Decision.Hostiles))
		for _, id := range out.Decision.Hostiles {
			start.Policy.AcknowledgedHostileIds = append(start.Policy.AcknowledgedHostileIds, string(id))
		}
		start.MaxTicks = out.Decision.MaxTicks
	}
	admission := &store.ClockWindowAdmission{Profile: s.config.Profile, Snapshot: state.Snapshot, Tick: facts.Tick, ReviewRevision: review.Revision, CapturedCursor: review.InboxCursor, MaxTicks: start.MaxTicks}
	key, err := clockSchedulerKey(admission, fingerprint, start)
	if err != nil {
		return out, err
	}
	intent := store.ClockIntent{Key: key, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Start: &start}, Window: admission}
	// The store returns sequence order. Retain the latest exact logical window,
	// including terminal refusals, rather than allocating another native attempt.
	for i := len(attempts) - 1; i >= 0; i-- {
		old := attempts[i].Intent
		if old.Key != key {
			continue
		}
		if old.Snapshot != intent.Snapshot || old.Window == nil || *old.Window != *admission || old.Command.Start == nil || old.Command.Renew != nil || old.Command.Speed != nil || old.Command.Start.Speed != start.Speed || old.Command.Start.LeaseMS != start.LeaseMS || old.Command.Start.MaxTicks != start.MaxTicks || !proto.Equal(old.Command.Start.Policy, start.Policy) {
			return out, executor.ErrEvidence
		}
		intent.RequestID = old.RequestID
		break
	}
	if intent.RequestID == "" {
		sequence, e := s.player.journal.ReadClockSequence(call)
		if e != nil {
			return out, e
		}
		intent.RequestID, err = sequence.NextRequestID()
		if err != nil {
			return out, err
		}
	}
	if err = s.player.current(call, epoch); err != nil {
		return out, err
	}
	if current := s.session.State(); !current.Enabled || !current.ObservationKnown || current.Snapshot != state.Snapshot {
		return out, executor.ErrAuthority
	}
	attempt, err := s.session.CommandClockWindow(call, ClockWindowRequest{Intent: intent, Facts: facts, MaxAge: s.config.MaxAge, CombatMaxTicks: combatMaxTicks})
	out.Attempt = &attempt
	if errors.Is(err, store.ErrConflict) {
		return out, errors.Join(executor.ErrHeld, err)
	}
	return out, err
}

// stepPlanners runs the routine reviewer synchronously first (every other
// planner's dispatch depends on being able to load the review it commits),
// then queues every other configured routine planner onto g as a concurrent
// goroutine sharing arbiter, returning immediately without waiting for them.
// The caller (ClockScheduler.Step) queues its own remaining inline planners
// onto the same g before calling g.Wait(), so the whole non-Routine batch —
// this function's planners and Step's own — runs as one concurrent wave. A
// planner left nil in config is simply skipped, matching how Step selected
// planners inline before this was extracted. Routine's own error aborts
// before anything is queued;
// an error from a queued planner is isolated by g and surfaces later, from
// g.Failures(), without stopping the step.
func (s *ClockScheduler) stepPlanners(call, epoch context.Context, out *ClockSchedulerResult, g *plannerGroup, arbiter *stepArbiter) error {
	gctx := call
	if s.config.Routine != nil {
		review, err := s.config.Routine.step(call, epoch, arbiter)
		if err != nil {
			return fmt.Errorf("routine: %w", err)
		}
		out.Routine = &review
		// A mental break is not a hold: it only ends with ticks, so refusing
		// every window while one is observed stopped the clock for good in
		// autonomous play. The break stays visible through the pawn's mood
		// goal and the native hazard supervisor keeps its authority.
	}
	if s.config.Work != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Work.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("work: %w", err)
			}
			out.Work = &method
			return nil
		})
	}
	if s.config.Fields != nil {
		g.Go(plannerFoothold, func() error {
			started := time.Now()
			method, err := s.config.Fields.step(gctx, epoch, arbiter)
			if err != nil {
				clockSchedulerLog("Fields.step failed after %s: %v", time.Since(started), err)
				return fmt.Errorf("fields: %w", err)
			}
			clockSchedulerLog("Fields.step result: reason=%v plan=%s wait=%d", method.Reason, method.Plan, method.NativeWorkTicks)
			out.Fields = &method
			return nil
		})
	}
	if s.config.FoodStorage != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.FoodStorage.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("foodStorage: %w", err)
			}
			out.FoodStorage = &method
			return nil
		})
	}
	if s.config.FoodAcquisition != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.FoodAcquisition.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("foodAcquisition: %w", err)
			}
			out.FoodAcquisition = &method
			return nil
		})
	}
	if s.config.WoodAcquisition != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.WoodAcquisition.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("woodAcquisition: %w", err)
			}
			out.WoodAcquisition = &method
			return nil
		})
	}
	if s.config.Supplies != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Supplies.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("supplies: %w", err)
			}
			out.Supplies = &method
			return nil
		})
	}
	if s.config.Sleeping != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Sleeping.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("sleeping: %w", err)
			}
			clockSchedulerLog("Sleeping.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Sleeping = &method
			return nil
		})
	}
	if s.config.Power != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Power.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("power: %w", err)
			}
			clockSchedulerLog("Power.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Power = &method
			return nil
		})
	}
	if s.config.Temperature != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Temperature.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("temperature: %w", err)
			}
			clockSchedulerLog("Temperature.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Temperature = &method
			return nil
		})
	}
	if s.config.Refrigeration != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Refrigeration.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("refrigeration: %w", err)
			}
			clockSchedulerLog("Refrigeration.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Refrigeration = &method
			return nil
		})
	}
	if s.config.Lighting != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Lighting.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("lighting: %w", err)
			}
			clockSchedulerLog("Lighting.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Lighting = &method
			return nil
		})
	}
	if s.config.Flooring != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Flooring.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("flooring: %w", err)
			}
			clockSchedulerLog("Flooring.step result: reason=%v decision=%+v nativeWorkTicks=%d", method.Reason, method.Decision, method.NativeWorkTicks)
			out.Flooring = &method
			return nil
		})
	}
	if s.config.Cooking != nil {
		g.Go(plannerFoothold, func() error {
			method, err := s.config.Cooking.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("cooking: %w", err)
			}
			out.Cooking = &method
			return nil
		})
	}
	if s.config.Butcher != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Butcher.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("butcher: %w", err)
			}
			out.Butcher = &method
			return nil
		})
	}
	for _, entry := range []struct {
		name    string
		planner *RoutineBillPlanner
		result  **RoutineBillResult
	}{{"cookingBills", s.config.CookingBills, &out.CookingBills}, {"preservationBills", s.config.PreservationBills, &out.PreservationBills}, {"butcherBills", s.config.ButcherBills, &out.ButcherBills}} {
		if entry.planner != nil {
			entry := entry
			priority := plannerFoothold
			if entry.name == "butcherBills" {
				priority = plannerMaintenance
			}
			g.Go(priority, func() error {
				method, err := entry.planner.step(gctx, epoch, arbiter)
				if err != nil {
					return fmt.Errorf("%s: %w", entry.name, err)
				}
				*entry.result = &method
				return nil
			})
		}
	}
	if s.config.Comfort != nil {
		g.Go(plannerComfort, func() error {
			method, err := s.config.Comfort.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("comfort: %w", err)
			}
			out.Comfort = &method
			return nil
		})
	}
	if s.config.Workshop != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Workshop.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("workshop: %w", err)
			}
			clockSchedulerLog("Workshop.step result: reason=%v admitted=%v refused=%v", method.Reason, method.Decision.Admitted, method.Decision.Refused)
			out.Workshop = &method
			return nil
		})
	}
	if s.config.Expansion != nil {
		g.Go(plannerComfort, func() error {
			method, err := s.config.Expansion.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("expansion: %w", err)
			}
			out.Expansion = &method
			return nil
		})
	}
	if s.config.Defense != nil {
		g.Go(plannerPreempt, func() error {
			method, err := s.config.Defense.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("defense: %w", err)
			}
			out.Defense = &method
			return nil
		})
	}
	if s.config.Tend != nil {
		g.Go(plannerCritical, func() error {
			method, err := s.config.Tend.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("tend: %w", err)
			}
			out.Tend = &method
			return nil
		})
	}
	if s.config.Rescue != nil {
		g.Go(plannerCritical, func() error {
			method, err := s.config.Rescue.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("rescue: %w", err)
			}
			out.Rescue = &method
			return nil
		})
	}
	if s.config.Equip != nil {
		g.Go(plannerMaintenance, func() error {
			method, err := s.config.Equip.step(gctx, epoch, arbiter)
			if err != nil {
				return fmt.Errorf("equip: %w", err)
			}
			out.Equip = &method
			return nil
		})
	}
	return nil
}

type clockWorkItem struct {
	Action     domain.ActionID
	Kind       domain.ActionKind
	Stage      domain.Stage
	Attempt    domain.AttemptID
	Unresolved bool
}

func clockSchedulerWork(plan store.PlanState, current domain.GenerationSnapshot) (bool, []clockWorkItem, error) {
	if plan.Spec.ID() != current.Plan || plan.Spec.Revision() != current.Revision || len(plan.Progress) != len(plan.Spec.Actions()) {
		return false, nil, executor.ErrEvidence
	}
	work := false
	items := make([]clockWorkItem, 0, len(plan.Progress))
	for _, p := range plan.Progress {
		v := p.View()
		items = append(items, clockWorkItem{v.Action, p.Action().Kind(), v.Stage, v.Attempt, v.Unresolved})
		if v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful || !v.Unresolved && v.Stage == domain.Completed {
			continue
		}
		// Allow, work settings, zones and a building's temperature target are
		// immediate designations and need no simulation window.
		if p.Action().Kind() == domain.SupplyAllowAction || p.Action().Kind() == domain.WorkAssignmentAction || p.Action().Kind() == domain.ZoneCreateAction || p.Action().Kind() == domain.BuildingTemperatureAction {
			continue
		}
		// Construction, native plant labor, and the routine-dispatched action
		// families (defense, medical, haul, equip — see routineExecutableKind)
		// all use the healthy-colony clock window; anything else is unsupported.
		if _, ok := p.Action().Building(); !ok {
			switch p.Action().Kind() {
			case domain.AcquisitionAction, domain.ProductionBillAction, domain.OwnedDraftAction,
				domain.MeleeAttackAction, domain.RangedAttackAction, domain.TendAction, domain.RescueAction, domain.CaptureAction,
				domain.HaulAction, domain.EquipAction, domain.GearReplaceAction, domain.RecoveryServiceAction,
				domain.MovementAction, domain.HusbandryAction, domain.PrisonerInteractionAction,
				domain.RepairAction, domain.CleanAction, domain.WasteAction, domain.MineAcquisitionAction, domain.ProductionPolicyAction, domain.MoodReliefAction, domain.ExcavationAction:
			default:
				return false, nil, executor.ErrHeld
			}
		}
		work = true
	}
	return work, items, nil
}

// clockSchedulerCombatPlan reports whether the current routine review binds an
// active ActiveCombat goal whose admitted plan still has open work: the only
// evidence under which live hostiles are watched rather than refused.
func clockSchedulerCombatPlan(ctx context.Context, journal *store.Store, current domain.GenerationSnapshot) (bool, error) {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return false, err
	}
	if !review.Enabled || review.Snapshot != current {
		return false, nil
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.ActiveCombat {
			continue
		}
		goal, err := journal.LoadGoal(ctx, binding.Goal)
		if errors.Is(err, store.ErrNotFound) {
			return false, nil
		}
		if err != nil {
			return false, err
		}
		if goal.Retired || goal.Goal.Status != domain.GoalActive || goal.Goal.Need != domain.NeedDeficit {
			return false, nil
		}
		for _, method := range goal.Methods {
			plan, err := journal.LoadPlan(ctx, method.Plan)
			if err != nil {
				return false, err
			}
			if domain.GoalWorkOpen(plan.Progress) {
				return true, nil
			}
		}
		return false, nil
	}
	return false, nil
}
func clockSchedulerKey(admission *store.ClockWindowAdmission, work []clockWorkItem, start bridge.ClockStart) (string, error) {
	policyBytes, err := (proto.MarshalOptions{Deterministic: true}).Marshal(start.Policy)
	if err != nil {
		return "", err
	}
	payload := struct {
		Version   int
		Admission *store.ClockWindowAdmission
		Work      []clockWorkItem
		Speed     k.Speed
		LeaseMS   uint32
		Policy    []byte
	}{1, admission, work, start.Speed, start.LeaseMS, policyBytes}
	data, err := json.Marshal(payload)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return "clock-window-" + hex.EncodeToString(sum[:]), nil
}

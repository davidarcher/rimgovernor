package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ClockWindowNative is the scheduler's native read side: the bundle is the
// one read a step, an event poll or a renewal opens with (scope, clock
// status, emergency, events in a single round trip); the plain clock status
// read serves the renewal's post-write re-check.
type ClockWindowNative interface {
	ReadBundle(context.Context, *o.BundleRequest) (*o.BundleReply, bridge.Result, error)
	ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
}
type ClockSchedulerConfig struct {
	CookingBills, PreservationBills, ButcherBills, CookAheadBills *RoutineBillPlanner
	Butcher                                                       *RoutineBuildingPlanner
	Fields                                                        *RoutineFieldPlanner
	FoodStorage                                                   *RoutineFoodStoragePlanner
	Profile                                                       string
	Start                                                         bridge.ClockStart
	MaxAge                                                        time.Duration
	// Worker is set when a routine Worker reconciles and dispatches beside
	// this scheduler: a review then defers admission while the Worker owes
	// a latched outcome's reconcile or a successor's dispatch (issue #162).
	// Without one nothing would ever settle them, so the review proceeds.
	Worker bool
	// CombatMaxTicks bounds each window admitted while the ActiveCombat goal
	// holds an admitted plan and hostiles are alive; zero keeps Start.MaxTicks.
	// Short windows let the raid be re-planned between them.
	CombatMaxTicks uint32
	// FullStepEvery bounds how long timer steps without a tick advance may
	// skip the planners; zero means DefaultFullStepEvery.
	FullStepEvery time.Duration
	// Facts is the cross-step fact cache the scheduler's steps fill and the
	// worker's writes discard (WorkerConfig.Facts); nil makes a private one.
	Facts *bridge.FactCache
	// Store is the decoded state store the steps fill beside Facts (#354):
	// each review's census sections with the tick they describe, dropped
	// by the same typed events. nil makes a private one; the HTTP API
	// reads the shared one.
	Store *facts.Store
	// Routine is reviewed only after owned clock obligations have drained.
	Routine                          *RoutineReviewer
	FoodAcquisition, WoodAcquisition *RoutineAcquisitionPlanner
	PestAcquisition                  *RoutineAcquisitionPlanner
	Work                             *RoutineWorkPlanner
	Supplies                         *RoutineSupplyPlanner
	Blight                           *RoutineBlightPlanner
	Clearance                        *RoutineClearancePlanner
	Shrine                           *RoutineShrinePlanner
	Sleeping                         *RoutineBuildingPlanner
	Cooking                          *RoutineBuildingPlanner
	Comfort                          *RoutineBuildingPlanner
	BasicComfort                     *RoutineBuildingPlanner
	Workshop                         *RoutineBuildingPlanner
	Hospital                         *RoutineHospitalPlanner
	SleepingUpkeep                   *RoutineSleepingUpkeepPlanner
	Expansion                        *RoutineBuildingPlanner
	Power                            *RoutineBuildingPlanner
	Temperature                      *RoutineBuildingPlanner
	Refrigeration                    *RoutineBuildingPlanner
	Lighting                         *RoutineBuildingPlanner
	Flooring                         *RoutineBuildingPlanner
	Routes                           *RoutineBuildingPlanner
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
	PopulationJoiner                 *RoutinePopulationJoinerPlanner
	Research                         *RoutineResearchPlanner
	IngredientStorage                *RoutineIngredientStoragePlanner
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
	Dialog                           *RoutineDialogPlanner
	Trade                            *RoutineTradePlanner
	RoutineMethods                   bool
}
type ClockSchedulerResult struct {
	CookingBills, PreservationBills, ButcherBills, CookAheadBills *RoutineBillResult
	Butcher                                                       *RoutineBuildingResult
	Fields                                                        *RoutineFieldResult
	FoodStorage                                                   *RoutineFoodStorageResult
	Attempt                                                       *store.ClockAttempt
	Decision                                                      policy.ClockWindowDecision
	// Window is the colony window the admission tail sized (before any
	// native-work or combat bound), zero when the tail did not run.
	Window                           ClockWindowSize
	Routine                          *store.RoutineReviewResult
	FoodAcquisition, WoodAcquisition *RoutineAcquisitionResult
	PestAcquisition                  *RoutineAcquisitionResult
	Work                             *RoutineWorkResult
	Supplies                         *RoutineSupplyResult
	Blight                           *RoutineBlightResult
	Clearance                        *RoutineClearanceResult
	Shrine                           *RoutineShrineResult
	Sleeping                         *RoutineBuildingResult
	Cooking                          *RoutineBuildingResult
	Comfort                          *RoutineBuildingResult
	BasicComfort                     *RoutineBuildingResult
	Workshop                         *RoutineBuildingResult
	Hospital                         *RoutineBuildingResult
	SleepingUpkeep                   *RoutineBuildingResult
	Expansion                        *RoutineBuildingResult
	Power                            *RoutineBuildingResult
	Temperature                      *RoutineBuildingResult
	Refrigeration                    *RoutineBuildingResult
	Lighting                         *RoutineBuildingResult
	Flooring                         *RoutineBuildingResult
	Routes                           *RoutineBuildingResult
	Defense                          *RoutineDefenseResult
	Tend                             *RoutineTendResult
	Rescue                           *RoutineRescueResult
	Equip                            *RoutineEquipResult
	SecureSupplies                   *RoutineSecureSuppliesResult
	Repair                           *RoutineRepairResult
	FireSafety                       *RoutineFireSafetyResult
	Clean                            *RoutineCleanResult
	Haul                             *RoutineHaulResult
	Gear                             *RoutineGearResult
	Medical                          *RoutineMedicalResult
	FoodStorageUpkeep                *RoutineFoodStorageUpkeepResult
	AnimalContainment                *RoutineAnimalContainmentResult
	Recovery                         *RoutineRecoveryResult
	Husbandry                        *RoutineHusbandryResult
	PrisonerInteraction              *RoutinePrisonerInteractionResult
	PopulationCustody                *RoutinePopulationCustodyResult
	PopulationJoiner                 *RoutinePopulationJoinerResult
	Research                         *RoutineResearchResult
	IngredientStorage                *RoutineIngredientStorageResult
	Resource                         *RoutineResourceResult
	AnimalFeed                       *RoutineResourceResult
	ProductionPolicy                 *RoutineProductionPolicyResult
	CaravanJourney                   *CaravanJourneyResult
	HomeCoverage                     *RoutineHomeCoverageResult
	StoneShell                       *RoutineStoneShellResult
	DefenseLayout                    *RoutineDefenseLayoutResult
	Waste                            *RoutineWasteResult
	MoodRelief                       *RoutineMoodReliefResult
	Naming                           *RoutineNamingResult
	Dialog                           *RoutineDialogResult
	Trade                            *RoutineTradeResult
	Running, Reconciled, Cleaned     bool
	// Coupled is set with Cleaned when the step stopped its own running
	// window because a coupled order's prerequisite completed under it
	// (domain.ActionDependency.Coupled, #244): the Worker prepares the
	// order against the stopped map and the next step admits again.
	Coupled bool
	// Unwatched counts the dispatched attempts of a watched kind the
	// running window does not watch: every one under a routine window,
	// which arms no watches (#244), and those dispatched after a combat
	// window was armed (#243). They are left to run and the event poll
	// carries their outcome; no stop is spent on them.
	Unwatched int
	// Deferred is set when the step admitted nothing because the Worker
	// has yet to reconcile an attempt whose terminal outcome the clock
	// latched; the step loop steps again at once (issue #162).
	Deferred bool
	// Combat is set while a combat watch window was admitted or is running,
	// so the worker keeps its short poll instead of backing off.
	Combat bool
	// PlannerFailures holds the errors of planners that failed this step, each
	// wrapped with the planner's name. A failed planner does not abort the
	// step: its peers still run and the clock window is still evaluated (#62).
	PlannerFailures []error
	// Watched counts the attempts the admitted window watches natively:
	// zero for a routine window, whose native work allowances already
	// bound it, so a completed order never stops the clock (#244).
	Watched int
	// Planners names the catalog planners this step queued, in catalog order.
	Planners []string
	// Reason is the step reason applied, with TickAdvanced resolved and a
	// timer promoted to full by FullStepEvery.
	Reason StepReason
}
type ClockScheduler struct {
	player              *Player
	session             *Session
	native              ClockWindowNative
	config              ClockSchedulerConfig
	clock               executor.Clock
	pollGate, renewGate chan struct{}
	// facts carries reviewed observations between steps; see clockFacts.
	facts *clockFacts
	// lastTick is the previous step's status tick, the basis of
	// StepReason.TickAdvanced; plannedTick is the tick the last planner
	// wave observed and lastFull when the last full wave ran. All are
	// touched only under the player gate.
	lastTick, plannedTick           int64
	lastTickKnown, plannedTickKnown bool
	lastFull                        time.Time
	// paceTick and paceAt are the previous step's status tick and the wall
	// time it was read at, the basis of the running window's pace
	// (livePace); touched only under the player gate.
	paceTick  int64
	paceAt    time.Time
	paceKnown bool
	// running is the scheduler's belief that a colony window it admitted
	// is still running: set by the step that dispatched or observed it,
	// cleared by the step or poll that saw it stopped. The poll loop holds
	// its journal read only while it is set (ClockWorkerConfig.PollWait).
	running *atomic.Bool
	// latched holds the attempts whose terminal outcome a committed page
	// reported and the Worker has yet to reconcile; see clockLatched.
	latched *clockLatched
	// trace is the trace of the latest step (telemetry.Trace); the Worker
	// nests its dispatches under it (#298).
	trace atomic.Value
	// grant is the latest AuthorityChanged grant a committed page carried
	// (generation, cursor): a hold or stop before it was answered by that
	// grant and does not disable the authority it holds (#322). Touched
	// only under the poll gate.
	grant clockGrant
	// history is the event cursor watermark the first poll read; events at
	// or before it precede any authority this process holds (#322).
	// Touched only under the poll gate.
	history clockHistory
	// readmitOwed is set by a step that settled its own stopped window and
	// then deferred, so the step that finally admits reports the whole
	// stop-to-readmit pause; touched only under the player gate.
	readmitOwed   bool
	admissionWarm *atomic.Pointer[clockAdmissionWarm]
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
	for _, planner := range []*RoutineBillPlanner{config.CookingBills, config.PreservationBills, config.ButcherBills, config.CookAheadBills} {
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
	for _, planner := range []*RoutineAcquisitionPlanner{config.FoodAcquisition, config.WoodAcquisition, config.PestAcquisition} {
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
	if config.Clearance != nil && (config.Routine == nil || config.Clearance.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Shrine != nil && (config.Routine == nil || config.Shrine.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Blight != nil && (config.Routine == nil || config.Blight.reviewer != config.Routine) {
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
	if config.BasicComfort != nil && (config.Routine == nil || config.BasicComfort.reviewer != config.Routine || config.BasicComfort.goal != policy.EnsureBasicComfort) {
		return nil, ErrControl
	}
	if config.Workshop != nil && (config.Routine == nil || config.Workshop.reviewer != config.Routine || config.Workshop.goal != policy.MaintainResource) {
		return nil, ErrControl
	}
	if config.Hospital != nil && (config.Routine == nil || config.Hospital.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.SleepingUpkeep != nil && (config.Routine == nil || config.SleepingUpkeep.reviewer != config.Routine) {
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
	if config.Routes != nil && (config.Routine == nil || config.Routes.reviewer != config.Routine || config.Routes.goal != policy.MaintainRoutes) {
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
	if config.PopulationJoiner != nil && (config.Routine == nil || config.PopulationJoiner.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Research != nil && (config.Routine == nil || config.Research.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.IngredientStorage != nil && (config.Routine == nil || config.IngredientStorage.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Naming != nil && (config.Routine == nil || config.Naming.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Dialog != nil && (config.Routine == nil || config.Dialog.reviewer != config.Routine) {
		return nil, ErrControl
	}
	if config.Trade != nil && (config.Routine == nil || config.Trade.reviewer != config.Routine) {
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
	scheduler := &ClockScheduler{player: player, session: session, native: native, config: config, clock: clock, pollGate: make(chan struct{}, 1), renewGate: make(chan struct{}, 1), facts: newClockFacts(config.Facts, config.Store), running: new(atomic.Bool), admissionWarm: new(atomic.Pointer[clockAdmissionWarm]), latched: newClockLatched()}
	if config.Routine != nil {
		config.Routine.store = scheduler.facts.store
	}
	return scheduler, nil
}

// clockDebug reports whether the service logger keeps debug records (serve
// --debug): the clock trace is built only then.
func clockDebug() bool {
	return slog.Default().Enabled(context.Background(), slog.LevelDebug)
}

// WindowRunning reports whether the last evidence the scheduler saw had a
// window it admitted still running. It is a hint for the poll cadence, not
// authority: a stale true costs one held poll before the next step or page
// clears it.
func (s *ClockScheduler) WindowRunning() bool { return s.running.Load() }

// clockSchedulerLog is the clock trace (serve --debug): which Step()
// branch was taken, what each planner decided, what a routine refused and
// why. It is a debug record on the service logger, so the stderr line
// carries the same time and tick stamp as every other; it never becomes a
// flight row. Typed events (a step's outcome, an admission
// refusal, a stop, a worker outcome) log through clockEvent instead.
func clockSchedulerLog(format string, args ...any) {
	if clockDebug() {
		slog.Default().Debug(fmt.Sprintf(format, args...), telemetry.ComponentKey, "clock-scheduler")
	}
}

// clockEvent logs one typed service event: an Info record that the
// telemetry handler mirrors into the flight recorder as a row of kind,
// stamped with the last observed tick and the trace ctx carries, with
// attrs as its payload.
func clockEvent(ctx context.Context, component, kind, message string, attrs ...any) {
	slog.Default().InfoContext(ctx, message, append([]any{telemetry.ComponentKey, component, telemetry.KindKey, kind}, attrs...)...)
}

// Trace is the trace of the latest step, empty before the first. The
// Worker's dispatches are spans under it (WorkerConfig.Trace).
func (s *ClockScheduler) Trace() telemetry.Trace {
	t, _ := s.trace.Load().(telemetry.Trace)
	return t
}

// clockReasonNames renders a decision's refusal reasons for an event attr.
func clockReasonNames(reasons []policy.ClockWindowReason) []string {
	out := make([]string, 0, len(reasons))
	for _, r := range reasons {
		out = append(out, string(r))
	}
	return out
}

// StepWithReason performs at most one scheduling decision for reason. It
// never acquires authority, renews an epoch, acknowledges events, or starts
// a background loop. Which planners run is the reason's plannerSelection;
// the admission tail (status, emergency, review and plan reads, then
// EvaluateClockWindow) runs on every step that reaches it.
func (s *ClockScheduler) StepWithReason(ctx context.Context, reason StepReason) (ClockSchedulerResult, error) {
	var out ClockSchedulerResult
	var paused time.Duration
	var readmit bool
	entered := time.Now()
	// The step is a trace root (or runs under the caller's): every flight
	// row it leaves, native or kinded, carries its trace_id (#298).
	ctx, trace := telemetry.EnsureTrace(ctx)
	s.trace.Store(trace)
	// The poll records latched outcomes as it commits them; the reason
	// repeats them for a step driven without the poll loop.
	s.latched.remember(reason.Events)
	call, epoch, done, err := s.player.enter(ctx, false)
	if err != nil {
		return out, err
	}
	defer done()
	if wait := time.Since(entered); wait > 50*time.Millisecond {
		clockSchedulerLog("step waited %s for the player gate", wait.Round(time.Millisecond))
	}
	// Every native observation this step issues -- the bundle below, the
	// routine census and each planner's own reads -- goes through one cache
	// that lives exactly as long as the step, so the facts the planners
	// share are read from native once per tick. A write within the step
	// discards it; see bridge.StepReadCache. Its parent outlives the step:
	// once the bundle has fixed this step's scope, facts an earlier step
	// read under the same load, generation and tick (or a tick-independent
	// family) are served from it, and the typed events PollEvents ingests
	// drop what they make stale.
	cache := bridge.NewChildReadCache(s.facts.cache)
	call = bridge.WithStepReadCache(call, cache)
	// The round trips that still cross the bridge (cache misses, the
	// uncacheable reads, writes) are tallied by tool so the cost of the
	// composition is visible per step: as a debug record beside the cache's
	// hit/miss counts, and as a clock_step row in the flight recorder,
	// which `rimgovernor phases` reports as reads/step.
	call, reads := bridge.WithReadTally(call)
	// The planning window's refresher (#356): its step scope, tick and
	// whether this step reviews are fixed once the bundle below is read,
	// before any planning read asks it.
	var window *planningWindow
	var zones *zoneRefresher
	if native, ok := s.native.(observation.ZonesNative); ok {
		zones = &zoneRefresher{native: native, store: s.facts.store, refreshes: &s.facts.zoneRefreshes}
		call = observation.WithZones(call, zones)
	}
	if native, ok := s.native.(PlanningWindowNative); ok {
		window = &planningWindow{native: native, store: s.facts.store, refreshes: &s.facts.windowRefreshes}
		call = observation.WithPlanningWindow(call, window)
	}
	stepBegan := time.Now()
	defer func() {
		elapsed := time.Since(stepBegan)
		stats := cache.Stats()
		clockSchedulerLog("step reads: %s cache hits=%d misses=%d coalesced=%d parent_hits=%d invalidations=%d running=%v elapsed=%s", reads, stats.Hits, stats.Misses, stats.Coalesced, stats.ParentHits, stats.Invalidations, out.Running, elapsed.Round(time.Millisecond))
		// The reason the step acted on (out.Reason once the status read
		// fixed it, else the caller's) and, for a step woken by a clock
		// stop, the latency from the native stop stamp to the step
		// (issue #112); `rimgovernor phases` reports both.
		cause := out.Reason.Cause
		if cause == "" {
			cause = reason.Cause
		}
		extra := map[string]any{"cache_hits": stats.Hits, "parent_hits": stats.ParentHits, "running": out.Running, "elapsed_ms": float64(elapsed) / float64(time.Millisecond), "reason": string(cause)}
		if reason.Stopped {
			extra["stop"] = true
			if !reason.StopAt.IsZero() {
				extra["stop_latency_ms"] = float64(stepBegan.Sub(reason.StopAt)) / float64(time.Millisecond)
			}
		}
		if out.Window.Ticks != 0 {
			extra["window_ticks"] = out.Window.Ticks
		}
		if out.Unwatched != 0 {
			extra["unwatched"] = out.Unwatched
		}
		// The stop-to-readmit pause this step closed: the wall time from
		// the stop of a window this scheduler owed to the admission it
		// dispatched (issue #162). Absent when the step admitted nothing or
		// the stop was not one of its own windows (a caller's pause).
		if out.Deferred {
			s.readmitOwed = s.readmitOwed || readmit
		} else {
			s.latched.released()
			if out.Attempt != nil {
				readmit, s.readmitOwed = readmit || s.readmitOwed, false
			}
		}
		if paused > 0 && readmit && out.Attempt != nil && out.Attempt.Phase != store.ClockRefused {
			extra["stop_pause_s"] = paused.Seconds()
		}
		reads.Publish(call, extra)
	}()
	attempts, err := s.player.journal.LoadClockAttempts(call, 4096)
	if err != nil {
		return out, err
	}
	// The step's one native read: the current scope, the owned clock status
	// and the emergency census of the same tick (issue #127). Its tick and
	// emergency sections are seeded into the step cache, so the routine
	// census and the planners read them without another round trip; a step
	// about to review asks for the census families too (issue #180).
	started := s.clock.Now()
	bundle, err := s.readStepBundle(call, s.bundleRequest(reason))
	if err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	loaded := bundle.GetObserved()
	if loaded == nil {
		return out, errors.Join(executor.ErrHeld, s.session.Disable())
	}
	if err = bridge.ValidateContext(loaded.Context); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	if window != nil {
		window.scope, window.tick, window.review = factsScope(loaded.Context), loaded.Context.GetTick(), s.stepReviews(reason)
	}
	if zones != nil {
		zones.scope, zones.tick = factsScope(loaded.Context), loaded.Context.GetTick()
	}
	// The remaining entity sections refresh once per
	// full review step, delta reads over what the store holds.
	if native, ok := s.native.(EntityNative); ok && s.stepReviews(reason) {
		refreshEntitySections(call, native, s.facts, loaded.Context.Identity, factsScope(loaded.Context), loaded.Context.GetTick())
	}
	state := s.session.State()
	world := domain.GenerationSnapshot{Colony: domain.ColonyID(loaded.Context.Identity.GetColonyId()), Load: domain.LoadID(loaded.Context.Identity.GetLoadToken()), Map: domain.MapID(loaded.Context.Identity.GetMapId())}
	epochs, err := s.player.journal.LoadClockEpochs(call, 4096)
	if err != nil {
		clockSchedulerLog("step exit: LoadClockEpochs %v", err)
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
				clockSchedulerLog("step exit: start %s dispatched under another world or without authority -> disable and cleanup", v.Intent.RequestID)
				out.Cleaned = true
				return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
			}
			recovered, e := s.session.ReconcileClock(call, v.Intent.RequestID)
			out.Attempt = &recovered
			out.Reconciled = true
			clockSchedulerLog("step exit: reconciled start %s -> phase=%s err=%v", v.Intent.RequestID, recovered.Phase, e)
			return out, e
		}
	}
	if obligations && (!state.Enabled || !state.ObservationKnown || !boundary.World(state.Snapshot, world) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native)) {
		clockSchedulerLog("step exit: owed epoch under enabled=%v known=%v snapshot=%+v observed generation=%d -> disable and cleanup", state.Enabled, state.ObservationKnown, state.Snapshot, loaded.Context.GetNativeGeneration())
		out.Cleaned = true
		return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
	}
	if !state.ObservationKnown || !boundary.World(state.Snapshot, world) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native) {
		clockSchedulerLog("step exit: observed scope %v generation=%d does not match known=%v snapshot=%+v -> disable", world, loaded.Context.GetNativeGeneration(), state.ObservationKnown, state.Snapshot)
		return out, errors.Join(executor.ErrAuthority, s.session.Disable())
	}
	status, err := s.bundleClockStatus(loaded, state.Snapshot)
	if err != nil {
		clockSchedulerLog("step exit: bundle clock status %v -> disable", err)
		return out, errors.Join(err, s.session.Disable())
	}
	if status.Context.GetTick() < loaded.Context.GetTick() {
		clockSchedulerLog("step exit: clock status tick %d behind scope tick %d -> disable", status.Context.GetTick(), loaded.Context.GetTick())
		return out, errors.Join(executor.ErrEvidence, s.session.Disable())
	}
	s.running.Store(false)
	reason.TickAdvanced = !s.lastTickKnown || status.Context.GetTick() != s.lastTick
	s.lastTick, s.lastTickKnown = status.Context.GetTick(), true
	s.livePace(status, started)
	telemetry.ObserveTick(status.Context.GetTick())
	if reason.Cause == StepTimer && !reason.TickAdvanced && s.fullStepDue() {
		reason.Cause = StepFull
	}
	out.Reason = reason
	// The timeline page's stderr parser reads the tick from this line.
	clockSchedulerLog("status: running=%v stopping=%v stopped=%v neverStarted=%v stopReason=%v tick=%d tickAdvanced=%v",
		status.GetRunning() != nil, status.GetStopping() != nil, status.GetStopped() != nil, status.GetNeverStarted() != nil,
		status.GetStopped().GetReason(), status.Context.GetTick(), reason.TickAdvanced)
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
			clockSchedulerLog("step exit: epoch stopping=%v ownedCurrent=%v with obligations -> cleanup", status.GetStopping() != nil, ownedCurrent)
			out.Cleaned = true
			return out, s.session.CleanupClock(call)
		}
		if !state.Enabled || !ownedCurrent {
			out.Combat = false
			return out, executor.ErrHeld
		}
		if status.GetRunning() != nil {
			var coupled []domain.ActionID
			if out.Unwatched, coupled, err = s.runningWork(call, state.Snapshot, status, actual); err != nil {
				return out, err
			}
			if len(coupled) > 0 {
				// A coupled order is written against what its prerequisite
				// produced, so it is prepared against a frozen read of that
				// result: stop the window at the completion (#244). The
				// Worker wakes on the stop and the next step admits again.
				clockSchedulerLog("coupled orders %v ready under the running window -> stopping", coupled)
				out.Cleaned = true
				out.Coupled = true
				return out, s.session.CleanupClockObserved(call, status)
			}
		}
		out.Running = true
		s.running.Store(status.GetRunning() != nil)
		if status.GetStopping() != nil || !s.livePlanningDue(reason) {
			clockSchedulerLog("clock already running under our own epoch -> no planners this step")
			return out, nil
		}
		// The window runs on; plan against the bundle's snapshot (one
		// main-thread hop, so its sections describe one tick) and let the
		// Worker dispatch live. Nothing is admitted: the window is already
		// running, and the stop that ends it reviews and admits as before.
		if err = s.player.current(call, epoch); err != nil {
			clockSchedulerLog("step exit: player epoch replaced before the live review: %v", err)
			return out, err
		}
		_, pick := plannerSelection(reason, s.facts.kindOf)
		reason.Cause = StepLive
		out.Reason = reason
		clockSchedulerLog("step reason: %s", reason)
		if out.Planners, err = s.runPlanners(call, epoch, &out, pick); err != nil {
			return out, err
		}
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if pick == nil {
			s.lastFull = s.clock.Now()
		}
		return out, nil
	}
	if obligations {
		// The window this step still owes is already stopped: settle it
		// from the status just read and, once nothing is owed, review in
		// this same step rather than leave the game paused for another
		// bundle read and a second pass (issue #162).
		clockSchedulerLog("obligations present, not running -> cleanup")
		readmit = true
		if err = s.session.CleanupClockObserved(call, status); err != nil {
			clockSchedulerLog("step exit: cleanup %v", err)
			out.Cleaned = true
			return out, err
		}
		if epochs, err = s.player.journal.LoadClockEpochs(call, 4096); err != nil {
			out.Cleaned = true
			return out, err
		}
		for _, owned := range epochs {
			if !clockCoordinatorTerminal(owned.Stage) {
				clockSchedulerLog("step exit: epoch %s still %s after cleanup", owned.StartRequestID, owned.Stage)
				out.Cleaned = true
				return out, nil
			}
		}
		clockSchedulerLog("cleanup settled every owed epoch -> reviewing in the same step")
	}
	if !state.Enabled {
		clockSchedulerLog("step exit: authority disabled before the review")
		return out, executor.ErrAuthority
	}
	if err = s.player.current(call, epoch); err != nil {
		clockSchedulerLog("step exit: player epoch replaced before the review: %v", err)
		return out, err
	}
	// The tick the planner facts describe: this step's, when it plans, else
	// the last planning step's. Admission holds when it predates the
	// admitted tick by more than the planning tolerance.
	factsTick := domain.Unknown[domain.Tick]()
	if s.plannedTickKnown {
		factsTick = domain.Known(domain.Tick(s.plannedTick))
	}
	planners, pick := plannerSelection(reason, s.facts.kindOf)
	clockSchedulerLog("step reason: %s planners=%v", reason, planners)
	if planners {
		if out.Planners, err = s.runPlanners(call, epoch, &out, pick); err != nil {
			return out, err
		}
		factsTick = domain.Known(domain.Tick(status.Context.GetTick()))
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if pick == nil {
			s.lastFull = s.clock.Now()
		}
		// The planners ran between the bundle and admission; MaxAge bounds
		// the admission reads alone, so read the status and the emergency
		// census again here, in one round trip.
		if out.Routine != nil || len(out.Planners) > 0 {
			started = s.clock.Now()
			if loaded, err = s.readBundle(call, loaded.Context.Identity, state.Snapshot); err != nil {
				return out, err
			}
			if status, err = s.bundleClockStatus(loaded, state.Snapshot); err != nil {
				return out, err
			}
		}
	}
	emergency, err := bridge.BundleEmergency(loaded)
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
	// The admission's emergency census is the freshest the step holds:
	// file it so the store's section is the one the window was admitted
	// against, not the review's earlier read of the same bundle.
	colonistsComplete, colonistsKnown := emergency.Facts.ColonistsComplete.Value()
	facts.Put(s.facts.store, factsScope(loaded.Context), facts.Emergency, facts.Held[policy.EmergencyFacts]{Value: emergency.Facts, AsOf: emergency.Context.GetTick(), Complete: colonistsKnown && colonistsComplete, Source: "rimgovernor/observations_read_bundle"})
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
	if pending := s.latched.pending(fingerprint); s.config.Worker && len(pending) > 0 {
		// The window just stopped on a latched outcome the Worker has not
		// reconciled: a window admitted now would watch that attempt again
		// (the native clock never re-latches a settled one) and run out
		// its budget before the successor it unblocks is dispatched.
		clockSchedulerLog("latched outcomes await the worker %v -> deferring admission", pending)
		out.Deferred = true
		return out, nil
	}
	if waiting := s.latched.undispatched(fingerprint); s.config.Worker && len(waiting) > 0 {
		// Likewise while the successor is queued but not yet dispatched.
		clockSchedulerLog("undispatched work awaits the worker %v -> deferring admission", waiting)
		out.Deferred = true
		return out, nil
	}
	combatPlan, err := clockSchedulerCombatPlan(call, s.player.journal, state.Snapshot)
	if err != nil {
		return out, err
	}
	// The defense planner's verdict at this stop: only a reported
	// no-squad answer lets a hostile building be watched instead of held
	// (#326); any other outcome, or no planner, keeps the hold.
	squadUnanswered := domain.Unknown[bool]()
	if out.Defense != nil && out.Defense.Reason != "" {
		squadUnanswered = domain.Known(out.Defense.Reason == BuildingMethodNoSquad)
	}
	clockState := policy.ClockWindowState("")
	start := s.config.Start
	// A routine window runs the whole budget (#244); a native-work or
	// combat bound may narrow it below.
	paused = clockStopSpan(status, s.clock.Now())
	out.Window = ClockWindowSize{Ticks: start.MaxTicks}
	clockSchedulerLog("colony window: %d ticks", out.Window.Ticks)
	var nativeWorkTicks uint32
	if out.Shrine != nil {
		nativeWorkTicks = out.Shrine.NativeWorkTicks
	}
	if out.Fields != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Fields.NativeWorkTicks)
	}
	for _, result := range []*RoutineBuildingResult{out.Sleeping, out.Cooking, out.Butcher, out.Comfort, out.BasicComfort, out.Workshop, out.Hospital, out.SleepingUpkeep, out.Expansion, out.Power, out.Temperature, out.Refrigeration, out.Lighting, out.Flooring, out.Routes} {
		if result != nil {
			nativeWorkTicks = max(nativeWorkTicks, result.NativeWorkTicks)
		}
	}
	// A bounded home fire is fought by native firefighters, never by an
	// order: the planner's only method is a short clock window.
	if out.FireSafety != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.FireSafety.NativeWorkTicks)
	}
	// A layout tier waiting on the game's own rebuild blueprint (a sprung
	// trap's auto-rearm) needs ticks, not a plan (#72).
	if out.DefenseLayout != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.DefenseLayout.NativeWorkTicks)
	}
	// A selected research project finishes on native ticks alone (#4 M4).
	if out.Research != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Research.NativeWorkTicks)
	}
	// A caravan walks to its trade spot on native ticks alone (#234).
	if out.Trade != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Trade.NativeWorkTicks)
	}
	// A standing production bill past its first iteration needs game time,
	// not another method (RoutineResourceResult.NativeWorkTicks).
	for _, result := range []*RoutineResourceResult{out.Resource, out.AnimalFeed} {
		if result != nil {
			nativeWorkTicks = max(nativeWorkTicks, result.NativeWorkTicks)
		}
	}
	// A planner that failed on a native refusal produced neither work nor a
	// wait, and the same read is refused again next step while the game
	// stands still; one window lets the world move under it (#219).
	if wait := plannerRefusalWait(out.PlannerFailures); wait > 0 {
		clockSchedulerLog("planner failed on a native refusal -> lending %d ticks", wait)
		nativeWorkTicks = max(nativeWorkTicks, wait)
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
	facts := policy.ClockWindowFacts{Current: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), FactsTick: factsTick, FactsTolerance: domain.Tick(bridge.PlanningTickTolerance()), StartedAt: started, ObservedAt: s.clock.Now(), Emergency: emergencyFacts, Review: policy.ClockWindowReview{Revision: review.Revision, Captured: review.InboxCursor, Reviewed: review.ReviewedCursor, Acknowledged: review.AcknowledgedCursor, HasHolds: domain.Known(len(review.Holds) > 0)}, Status: policy.ClockWindowStatus{Snapshot: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), State: clockState, ActualPaused: boundary.FactBool(status.ActualPaused), NativeTickBoundary: boundary.FactBool(status.NativeTickBoundary), DurableEvents: boundary.FactBool(status.DurableEvents)}, Obligations: policy.ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(work), CombatPlan: domain.Known(combatPlan), SquadUnanswered: squadUnanswered}
	if status.NewestCursor != nil {
		facts.Status.NewestCursor = domain.Known(status.GetNewestCursor())
	}
	combatMaxTicks := min(s.config.CombatMaxTicks, start.MaxTicks)
	out.Decision = policy.EvaluateClockWindow(facts, policy.ClockWindowLimits{Now: s.clock.Now(), MaxAge: s.config.MaxAge, MaxTicks: start.MaxTicks, CombatMaxTicks: combatMaxTicks})
	clockSchedulerLog("EvaluateClockWindow: work=%v combatPlan=%v admitted=%v mode=%s hostiles=%v refused=%v", work, combatPlan, out.Decision.Admitted, out.Decision.Mode, out.Decision.Hostiles, out.Decision.Refused)
	if !out.Decision.Admitted {
		clockEvent(call, "clock-scheduler", "admission_refused", "window not admitted", "refused", clockReasonNames(out.Decision.Refused), "mode", string(out.Decision.Mode), "work", work, "combat_plan", combatPlan, "hostiles", len(out.Decision.Hostiles), "clock_state", string(clockState), "window_ticks", out.Window.Ticks)
		return out, executor.ErrHeld
	}
	start.Policy = proto.Clone(start.Policy).(*k.WatchPolicy)
	namespace, err := s.player.journal.Identity(call)
	if err != nil {
		return out, err
	}
	// A routine window watches nothing: a completed order is not a reason
	// to stop the clock, the event poll carries its outcome to the Worker
	// under the running window, and the routine review runs there too
	// (#243, #244). A combat window watches its dispatched orders so the
	// fight's next step starts at the outcome tick (#207).
	start.Policy.WatchedAttempts = nil
	s.facts.remember(fingerprint)
	// A colonist already known downed is acknowledged in either mode: the
	// native watcher otherwise stops every window at zero ticks on the same
	// casualty and the rescue never gets the ticks it needs (#213).
	start.Policy.AcknowledgedDownedColonistIds = make([]string, 0, len(out.Decision.Downed))
	for _, id := range out.Decision.Downed {
		start.Policy.AcknowledgedDownedColonistIds = append(start.Policy.AcknowledgedDownedColonistIds, string(id))
	}
	if out.Decision.Mode == policy.ClockWindowCombat {
		// The native watcher stops on any unacknowledged hostile within
		// HostileWithin; a combat window acknowledges exactly the live
		// hostiles the policy admitted and runs under the combat budget.
		out.Combat = true
		start.Policy.Mode = k.WatchMode_WATCH_MODE_COMBAT.Enum()
		start.Policy.WatchedAttempts = clockSchedulerWatches(fingerprint, string(namespace))
		out.Watched = len(start.Policy.WatchedAttempts)
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
	s.running.Store(err == nil)
	return out, err
}

// runPlanners runs the routine reviewer and the selected planner wave
// (stepPlanners) and waits for it, reporting the isolated failures on out.
func (s *ClockScheduler) runPlanners(call, epoch context.Context, out *ClockSchedulerResult, pick func(plannerEntry) bool) ([]string, error) {
	arbiter := newStepArbiter()
	g := newPlannerGroup(call, plannerWidth)
	planners, err := s.stepPlanners(call, epoch, out, g, arbiter, pick)
	if err != nil {
		return nil, err
	}
	if err = g.Wait(); err != nil {
		return nil, err
	}
	// A failed planner is reported, not fatal: the step still evaluates the
	// clock window on what the other planners committed, and the failed
	// planner retries next step (#62).
	out.PlannerFailures = g.Failures()
	for _, failure := range out.PlannerFailures {
		clockSchedulerLog("planner failed (isolated): %v", failure)
	}
	return planners, nil
}

// livePace measures the running window's pace from the previous step's
// status tick and sets domain.SetLiveDrift to the ticks that pace covers
// in MaxAge, the wall time a step's reads already have (#345): a live
// step and the Worker's dispatches under it then read one plan however
// fast the game runs. A stopped, stopping or never-started clock, or a
// pace not yet measured, clears the drift so a paused step keeps the
// tick-exact bound.
func (s *ClockScheduler) livePace(status *k.Status, readAt time.Time) {
	tick := status.Context.GetTick()
	drift := domain.Tick(0)
	if status.GetRunning() != nil && s.paceKnown && tick > s.paceTick && readAt.After(s.paceAt) {
		perSecond := float64(tick-s.paceTick) / readAt.Sub(s.paceAt).Seconds()
		drift = domain.Tick(perSecond * s.config.MaxAge.Seconds())
	}
	if drift != domain.LiveDrift() {
		clockSchedulerLog("live drift %d -> %d ticks (tick %d, %d ticks since the previous step)", domain.LiveDrift(), drift, tick, tick-s.paceTick)
	}
	domain.SetLiveDrift(drift)
	s.paceTick, s.paceAt, s.paceKnown = tick, readAt, true
}

// livePlanningDue reports whether a step that found its own window running
// plans under it (#243): a wake or full step at once, a timer step when
// the full-step safety net is due, so a running window costs one planner
// wave per FullStepEvery rather than one per step.
func (s *ClockScheduler) livePlanningDue(reason StepReason) bool {
	if s.config.Routine == nil {
		return false
	}
	if reason.Cause == StepTimer {
		return s.fullStepDue()
	}
	planners, _ := plannerSelection(reason, s.facts.kindOf)
	return planners
}

// bundleRequest is the step's first bundle: the clock status and the
// emergency census always, plus the routine census's families (colony
// facts, population, research, the colonists' pawn detail) when the step
// is expected to review, so the review costs no census round trips. The
// expectation is the planner selection the step will make once the status
// is read: a timer step reviews only when the full-step safety net is due
// (a tick that moved under a stopped clock, or a stop the timer catches
// before the poll, is the exception, read natively as before); any other
// cause reviews, under a stopped clock or live under a running window
// (livePlanningDue). A continuous family the facts store still holds fresh
// under its cadence (bundleFamilies, #360) is left out: the review serves
// it from the store. A wrong guess costs a heavier bundle or the dedicated
// reads, never a wrong fact.
func (s *ClockScheduler) bundleRequest(reason StepReason) *o.BundleRequest {
	request := &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true)}
	if s.stepReviews(reason) {
		request.ColonyFacts = proto.Bool(true)
		population, research, pawns := bundleFamilies(s.facts.store, s.lastTick+int64(domain.LiveDrift()), s.lastTickKnown)
		request.Population, request.Research, request.ColonistPawns = proto.Bool(population), proto.Bool(research), proto.Bool(pawns)
		request.ColonistPawnFields, request.PopulationFields, request.ResearchFields = bundleMasks()
	}
	return request
}

// bundleMasks is the review bundle's field mask per continuous family
// (#360): the sub-blocks the routine review decodes. The mask is the same whatever
// planners the step selects, because the review's DetectRoutine consumes
// every decoded block on every review; a planner subset never narrows
// what is read. Each mask is an empty message: present, so native drops
// the blocks the controller never reads (gear detail, inventory,
// capacities, surgery bills, backstory, traits, relations; owned beds,
// nutrition, supported interactions; research unlocks, costs, facilities),
// with no include flag set. A native that predates the masks returns the
// whole family; the decoders read the same fields either way.
func bundleMasks() (*o.PawnFields, *o.PopulationFields, *o.ResearchFields) {
	return &o.PawnFields{}, &o.PopulationFields{}, &o.ResearchFields{}
}

// bundleFamilies decides which continuous families ride a review step's
// bundle: a family whose section the store holds fresh at tick, the tick
// the step is expected to observe (the previous status tick plus the
// running window's drift, so the guess errs on the later side), is served
// by the review from the store and stays out; every family rides when the
// tick is unknown (the first step) or the section is stale or absent.
func bundleFamilies(store *facts.Store, tick int64, known bool) (population, research, pawns bool) {
	if !known {
		return true, true, true
	}
	return !store.Fresh(facts.Population, tick), !store.Fresh(facts.Research, tick), !store.Fresh(facts.Pawns, tick)
}

// stepReviews is whether a step taken for reason is expected to run the
// routine review: never without a reviewer, on a timer step only when the
// full-step safety net is due, and for any other cause as plannerSelection
// decides.
func (s *ClockScheduler) stepReviews(reason StepReason) bool {
	if s.config.Routine == nil {
		return false
	}
	if reason.Cause == StepTimer {
		return s.fullStepDue()
	}
	review, _ := plannerSelection(reason, s.facts.kindOf)
	return review
}

// factsScope is the store scope an observation context establishes: the
// load token and native generation FactCache keys its rows by.
func factsScope(context *c.ObservationContext) facts.Scope {
	return facts.Scope{Load: context.GetIdentity().GetLoadToken(), Generation: context.GetNativeGeneration()}
}

// plannerRefusalWait is stockWaitTicks when any isolated planner failure of
// the step is a native refusal (bridge.ErrRefused) and zero otherwise. A
// transport or control failure is not resolved by letting time pass; a
// refused preview or read describes the world at this tick and may be.
func plannerRefusalWait(failures []error) uint32 {
	for _, failure := range failures {
		if errors.Is(failure, bridge.ErrRefused) {
			return stockWaitTicks
		}
	}
	return 0
}

// readBundle re-reads the step's bundle (clock status and emergency census)
// under the identity the step observed, validated against the enabled
// snapshot.
func (s *ClockScheduler) readBundle(call context.Context, identity *c.Identity, snapshot domain.GenerationSnapshot) (*o.BundleSnapshot, error) {
	reply, _, err := s.native.ReadBundle(call, &o.BundleRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, ClockStatus: proto.Bool(true), Emergency: proto.Bool(true)})
	if err != nil {
		return nil, err
	}
	loaded := reply.GetObserved()
	if loaded == nil {
		return nil, executor.ErrHeld
	}
	if _, err = boundary.Context(loaded.Context, snapshot); err != nil {
		return nil, err
	}
	return loaded, nil
}

// bundleClockStatus validates a bundle's clock status section under the
// enabled snapshot.
func (s *ClockScheduler) bundleClockStatus(loaded *o.BundleSnapshot, snapshot domain.GenerationSnapshot) (*k.Status, error) {
	status := loaded.GetClockStatus()
	if err := bridge.ValidateClockStatus(status, loaded.Context.GetIdentity()); err != nil {
		return nil, err
	}
	if _, err := boundary.Context(status.Context, snapshot); err != nil {
		return nil, err
	}
	return status, nil
}

// fullStepDue reports whether the FullStepEvery safety net promotes a timer
// step that would otherwise skip the planners.
func (s *ClockScheduler) fullStepDue() bool {
	every := s.config.FullStepEvery
	if every <= 0 {
		every = DefaultFullStepEvery
	}
	return s.lastFull.IsZero() || s.clock.Now().Sub(s.lastFull) >= every
}

// stepPlanners runs the routine reviewer synchronously first (every other
// planner's dispatch depends on being able to load the review it commits),
// then queues the configured catalog planners pick selects (nil: all) onto
// g as one concurrent wave sharing arbiter, returning the queued names
// without waiting. Routine's own error aborts before anything is queued;
// an error from a queued planner is isolated by g and surfaces later, from
// g.Failures(), without stopping the step.
func (s *ClockScheduler) stepPlanners(call, epoch context.Context, out *ClockSchedulerResult, g *plannerGroup, arbiter *stepArbiter, pick func(plannerEntry) bool) ([]string, error) {
	if s.config.Routine != nil {
		review, err := s.config.Routine.step(call, epoch, arbiter, pick != nil)
		if err != nil {
			return nil, fmt.Errorf("routine: %w", err)
		}
		if !review.Review.Enabled {
			// Authority lapsed between this step's state read and the
			// review (a poll hold, a resume in flight): the reviewer
			// recorded a disabled review that ranks nothing. Admitting a
			// window on it refuses no_work at every step until something
			// else re-reviews (#331); fail the step instead, so the next
			// step reviews with authority or exits on its absence.
			return nil, fmt.Errorf("routine: %w", executor.ErrAuthority)
		}
		out.Routine = &review
		// A mental break is not a hold: it only ends with ticks, so refusing
		// every window while one is observed stopped the clock for good in
		// autonomous play. The break stays visible through the pawn's mood
		// goal and the native hazard supervisor keeps its authority.
	}
	return s.queuePlanners(call, epoch, out, g, arbiter, pick), nil
}

type clockWorkItem struct {
	Action     domain.ActionID
	Kind       domain.ActionKind
	Stage      domain.Stage
	Attempt    domain.AttemptID
	Unresolved bool
}

// clockSchedulerWatches names the dispatched attempts the native clock
// watches for a combat window, in catalog order and bounded. A latched
// terminal outcome stops the window at once instead of running out the tick
// budget. Only families with a native operation record the clock can
// observe are armed: construction and haul (#108). Immediate designations
// (allow, zones, work settings) settle within their own write and have
// nothing to watch. Routine windows arm none (#244).
func clockSchedulerWatches(items []clockWorkItem, namespace string) []*c.AttemptKey {
	var watched []*c.AttemptKey
	for _, item := range items {
		if !clockWatchedKind(item.Kind) || item.Attempt == 0 || item.Stage != domain.Dispatched && item.Stage != domain.AwaitingObservation {
			continue
		}
		if len(watched) == bridge.ClockWatchedAttemptsMax {
			break
		}
		watched = append(watched, &c.AttemptKey{ControllerSessionId: proto.String(namespace), ActionId: proto.String(string(item.Action)), AttemptId: proto.Uint64(uint64(item.Attempt))})
	}
	return watched
}

// runningWork reads the current plan under a running epoch and reports two
// things about it. The count is the dispatched attempts of a watched kind
// the epoch does not watch: every one under a routine window (#244) and the
// work the Worker dispatched after a combat window was armed; the window
// runs on and the event poll carries their outcome (#243), so the count is
// step evidence only, and it skips the plan when the watch list is already
// at the native bound (more attempts than that go unwatched by design). The
// names are the coupled orders whose prerequisite has completed at the
// epoch's current tick (domain.PlanSpec.CoupledPending), the one routine
// reason a running window stops. Both are empty when the plan no longer
// matches the admitted snapshot (the admission tail reports that as
// evidence on its own).
func (s *ClockScheduler) runningWork(call context.Context, snapshot domain.GenerationSnapshot, status *k.Status, epoch *k.Epoch) (int, []domain.ActionID, error) {
	plan, err := s.player.journal.LoadPlan(call, snapshot.Plan)
	if err != nil {
		return 0, nil, err
	}
	_, items, err := clockSchedulerWork(plan, snapshot)
	if err != nil {
		return 0, nil, nil
	}
	coupled := plan.Spec.CoupledPending(plan.Progress, snapshot, domain.Tick(status.GetContext().GetTick()))
	watched := epoch.GetPolicy().GetWatchedAttempts()
	if len(watched) >= bridge.ClockWatchedAttemptsMax {
		return 0, coupled, nil
	}
	armed := map[string]bool{}
	for _, key := range watched {
		armed[fmt.Sprintf("%s/%d", key.GetActionId(), key.GetAttemptId())] = true
	}
	live := 0
	for _, item := range items {
		if !clockWatchedKind(item.Kind) || item.Attempt == 0 || item.Stage != domain.Dispatched && item.Stage != domain.AwaitingObservation {
			continue
		}
		if !armed[fmt.Sprintf("%s/%d", item.Action, item.Attempt)] {
			live++
		}
	}
	return live, coupled, nil
}

// clockWatchedKind reports whether the native clock keeps an operation
// record for kind that a watch can observe (NativeClockWatch.cs).
func clockWatchedKind(kind domain.ActionKind) bool {
	return kind == domain.BuildingAction || kind == domain.HaulAction
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
		// Allow, work settings, zones, a building's temperature target, a
		// bed's medical flag, a bed's owner, a grower's crop and a claim are immediate
		// designations and need no simulation window.
		if p.Action().Kind() == domain.SupplyAllowAction || p.Action().Kind() == domain.SupplyForbidAction || p.Action().Kind() == domain.WorkAssignmentAction || p.Action().Kind() == domain.ZoneCreateAction || p.Action().Kind() == domain.BuildingTemperatureAction || p.Action().Kind() == domain.BedMedicalAction || p.Action().Kind() == domain.BedAssignAction || p.Action().Kind() == domain.GrowerCropAction || p.Action().Kind() == domain.ClaimBuildingAction {
			continue
		}
		// Construction, native plant labor, and the routine-dispatched action
		// families (defense, medical, haul, equip — see routineExecutableKind)
		// all use the healthy-colony clock window; anything else is unsupported.
		if _, ok := p.Action().Building(); !ok {
			switch p.Action().Kind() {
			case domain.AcquisitionAction, domain.ProductionBillAction, domain.OwnedDraftAction,
				domain.MeleeAttackAction, domain.RangedAttackAction, domain.TendAction, domain.RescueAction, domain.CaptureAction,
				domain.HaulAction, domain.EquipAction, domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction,
				domain.MovementAction, domain.HusbandryAction, domain.PrisonerInteractionAction,
				domain.RepairAction, domain.CleanAction, domain.WasteAction, domain.MineAcquisitionAction, domain.DeconstructionAction, domain.CutPlantAction, domain.ProductionPolicyAction, domain.MoodReliefAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.TradeAction, domain.QuestAcceptAction, domain.WallRemovalAction, domain.OpenCasketAction, domain.CaravanDepartureAction:
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
		Version          int
		Admission        *store.ClockWindowAdmission
		Work             []clockWorkItem
		Speed            k.Speed
		TestAcceleration bool
		LeaseMS          uint32
		Policy           []byte
	}{2, admission, work, start.Speed, start.TestAcceleration, start.LeaseMS, policyBytes}
	data, err := json.Marshal(payload)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return "clock-window-" + hex.EncodeToString(sum[:]), nil
}

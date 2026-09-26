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
	"slices"
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
	// PaceHorizonTicks is the safe horizon player acceleration's backoff
	// keeps the critical evidence inside (issue #627); zero is
	// DefaultPaceHorizonTicks. Unused unless Start.PlayerAccelerated.
	PaceHorizonTicks domain.Tick
	MaxAge           time.Duration
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
	// LivePlanningTicks bounds the live planner wave by pace (#598): when
	// the previous live step's wall time covers more ticks than this at the
	// running window's measured pace, a running-window step admits,
	// reconciles and leaves the Worker's dispatch, and the wave waits for
	// the stop. Zero means DefaultLivePlanningTicks.
	LivePlanningTicks domain.Tick
	// PlayerQuiet is how long after the last Manual authority bump (the
	// player pressing a speed key) a step waits before it re-takes a clock
	// the player runs by hand under a stopped epoch (#601). Zero means
	// DefaultPlayerQuiet.
	PlayerQuiet time.Duration
	// Facts is the cross-step fact cache the scheduler's steps fill and the
	// worker's writes discard (WorkerConfig.Facts); nil makes a private one.
	Facts *bridge.FactCache
	// Budget bounds the step's planner waves (#623); see StepBudget.
	Budget StepBudget
	// Faults are the acceptance harness's injected failures (#633); the
	// zero value injects none.
	Faults Faults
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
	Tidy                             *RoutineTidyPlanner
	DefenseLayout                    *RoutineDefenseLayoutPlanner
	Waste                            *RoutineWastePlanner
	MoodRelief                       *RoutineMoodReliefPlanner
	Naming                           *RoutineNamingPlanner
	Dialog                           *RoutineDialogPlanner
	Trade                            *RoutineTradePlanner
	RoutineMethods                   bool
}
type ClockSchedulerResult struct {
	// Pacing is what the step's clock status said of the pace (#627).
	Pacing                                                        StepPacing
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
	Tidy                             *RoutineTidyResult
	DefenseLayout                    *RoutineDefenseLayoutResult
	Waste                            *RoutineWasteResult
	MoodRelief                       *RoutineMoodReliefResult
	Naming                           *RoutineNamingResult
	Dialog                           *RoutineDialogResult
	Trade                            *RoutineTradeResult
	Running, Reconciled, Cleaned     bool
	// Coupled is set when a coupled order's prerequisite completed under
	// the step's own running window (domain.ActionDependency.Coupled): the
	// step plans live for it at once and the window runs on (#584); the
	// order's native CAS evidence refuses a read the world has left behind.
	Coupled bool
	// CoupledOrders is how many such orders were ready, the count the
	// stop-reason breakdown reads from the step row (#584).
	CoupledOrders int
	// Unwatched counts the dispatched attempts of a watched kind the
	// running window does not watch: every one under a routine window,
	// which arms no watches (#244), and those dispatched after a combat
	// window was armed (#243). They are left to run and the event poll
	// carries their outcome; no stop is spent on them.
	Unwatched int
	// Deferred is set when the step admitted nothing because the Worker
	// has yet to reconcile an attempt whose terminal outcome the clock
	// latched; the step loop steps again at once (issue #162). A step that
	// found the player running the game under a stopped clock and is
	// waiting out PlayerQuiet before it re-takes it defers too (#601).
	Deferred bool
	// Journal is the wall time the step's own obligation reads spent in
	// the journal: the attempt and epoch catalogs, the review, the current
	// plan and the active catalog. It is the clock_step row's journal_ms
	// (#634); a save's retired history must not grow it.
	Journal time.Duration
	// Retaken is set when the step found the player running the game under
	// a stopped clock, paused it natively and reviewed from the paused tick
	// in the same step (#601).
	Retaken bool
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
	// Waiting names the planners the selection skipped because each still
	// waits on the open work it reported (plannerQueue.waits, #625); the
	// clock_step row reports it as waiting.
	Waiting []string
	// Sections names the census sections a subset step read at cadence,
	// the union its planners declare (sectionsWanted); nil when every
	// planner ran and every section was read. The clock_step row reports
	// it as sections.
	Sections []string
	// Proposals are the migrated planners' proposals in the coordinator's
	// rank order, each admitted with its plan, waiting on the claim a
	// higher-ranked proposal holds (#622) or expired against this step's
	// snapshot (#623).
	Proposals []ProposalOutcome
	// MissedCutoff names the optional planners still evaluating when the
	// admission cycle moved on (#623): their results are discarded, a
	// proposal among them is carried to the next step's coordinator, and
	// they run again next step. The clock_step row lists them.
	MissedCutoff []string
	// HeldBy names the critical planners still evaluating when the wall
	// budget ran out (#623): the step admitted no window and the stop
	// reason names them.
	HeldBy []string
	// CriticalWave is the wall time of the admission cycle's wave: the
	// routine review and the critical planners.
	CriticalWave time.Duration
	// NativeWorkTicks is the native-work window the planners asked for,
	// the largest of their NativeWorkTicks, before the budget bounds it.
	NativeWorkTicks uint32
	// LivePlanning is LivePlanningSkippedPace when a running-window step
	// that would have planned live left the wave to the stop (#598); empty
	// otherwise. The scheduler_step row reports it as live_planning.
	LivePlanning string
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
	// pace is player acceleration's backoff (clock_pace_backoff.go), nil
	// under fixed pacing.
	pace *paceBackoff
	// paceEpoch is the running epoch the backoff's requests change.
	paceEpoch *atomic.Pointer[k.Epoch]
	// facts carries reviewed observations between steps; see clockFacts.
	facts *clockFacts
	// lastTick is the previous step's status tick, the basis of
	// StepReason.TickAdvanced; plannedTick is the tick the last planner
	// wave observed and lastFull when the last full wave ran. All are
	// touched only under the player gate.
	lastTick, plannedTick           int64
	lastTickKnown, plannedTickKnown bool
	lastFull                        time.Time
	// starved names the optional planners that missed the previous step's
	// cutoff; the next step joins them for the whole wall budget, so a
	// planner slower than the grace still returns once instead of being
	// cancelled every step while the clock waits on its work. Touched
	// only under the player gate.
	starved map[string]bool
	// noWork is set when the last window decision refused no_work; see
	// selectPlanners. Touched only under the player gate.
	noWork bool
	// paceTick and paceAt are the previous step's status tick and the wall
	// time it was read at, the basis of the running window's pace
	// (livePace); touched only under the player gate.
	paceTick  int64
	paceAt    time.Time
	paceKnown bool
	// pacePerSecond is the last pace livePace measured under a running
	// window, kept across stops so the next window starts with a drift
	// (seedLiveDrift) instead of the stopped clock's zero.
	pacePerSecond float64
	// livePaceTicks is the pace the running window widens the step's
	// bounds by (domain.ReadValidity.Pace): pacePerSecond while a window
	// runs, zero under a stopped clock. Touched only under the player gate.
	livePaceTicks float64
	// validity is the read validity of the latest step whose scope was
	// fixed (#624): what the Worker's dispatches under that step's window
	// judge their reads by (Validity).
	validity *atomic.Pointer[domain.ReadValidity]
	// liveStepWall is the wall time of the last live step (StepLive): what
	// a live planner wave costs under this process, measured against the
	// pace (livePlanningPaced, #598). Touched only under the player gate.
	liveStepWall time.Duration
	// manualAt is the wall time (unix nanoseconds, zero for none) of the
	// last Manual authority change a committed page carried: the player
	// pressing a speed key. Written under the poll gate, read under the
	// player gate by the re-take decision (#601).
	manualAt *atomic.Int64
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
	// queue is the planners' due queue (#625): what a step selects
	// between full steps, and the waits it skips. Touched only under the
	// player gate.
	queue *plannerQueue
	// lastLive is when the last timer-driven live wave ran under a running
	// window (DefaultLiveWaveEvery); touched only under the player gate.
	lastLive time.Time
	// late carries proposals that reached a step's arbiter after its
	// cutoff to the next step's coordinator (#623).
	late *lateProposals
	// catalog is the planner table the steps queue from: plannerCatalog,
	// or a table a test substitutes.
	catalog []plannerEntry
}

func NewClockScheduler(player *Player, session *Session, native ClockWindowNative, config ClockSchedulerConfig, clock executor.Clock) (*ClockScheduler, error) {
	if player == nil || session == nil || player.session != session || player.journal != session.journal || session.clock == nil || native == nil || clock == nil || config.MaxAge <= 0 || config.MaxAge > time.Minute {
		return nil, ErrControl
	}
	if config.Start.Policy == nil {
		return nil, ErrControl
	}
	// The shim drift is process-wide state this scheduler owns (livePace,
	// seedLiveDrift): a new scheduler starts from the stopped clock's bound.
	domain.SetLiveDrift(0)
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
	if config.Tidy != nil && (config.Routine == nil || config.Tidy.reviewer != config.Routine) {
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
	scheduler := &ClockScheduler{player: player, session: session, native: native, config: config, clock: clock, pollGate: make(chan struct{}, 1), renewGate: make(chan struct{}, 1), facts: newClockFacts(config.Facts, config.Store), queue: newPlannerQueue(), running: new(atomic.Bool), manualAt: new(atomic.Int64), admissionWarm: new(atomic.Pointer[clockAdmissionWarm]), validity: new(atomic.Pointer[domain.ReadValidity]), latched: newClockLatched(), late: &lateProposals{}, catalog: plannerCatalog}
	if config.Start.PlayerAccelerated {
		scheduler.paceEpoch = new(atomic.Pointer[k.Epoch])
		scheduler.pace = newPaceBackoff(config.PaceHorizonTicks, clock.Now, scheduler.requestCeiling)
	}
	scheduler.queue.catalog = func() []plannerEntry { return scheduler.catalog }
	scheduler.queue.configured = func(entry plannerEntry) bool { return entry.configured(&scheduler.config) }
	if config.Routine != nil {
		config.Routine.store = scheduler.facts.store
	}
	player.extentFacts = scheduler.facts.store
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

// Validity is the read validity of the latest step whose scope was fixed
// (#624): the scope and tick its facts describe, the pace its bounds
// widen by and the fact store's section versions then. The Worker carries
// it on each dispatch's context; false before any step fixed one.
func (s *ClockScheduler) Validity() (domain.ReadValidity, bool) {
	v := s.validity.Load()
	if v == nil {
		return domain.ReadValidity{}, false
	}
	return *v, true
}

// readValidity fixes the step's validity once its scope, tick and pace
// are known (#624) and publishes it for the Worker. The shim drift for
// un-migrated callers follows the inventory bound of the same validity.
func (s *ClockScheduler) readValidity(snapshot domain.GenerationSnapshot, tick int64) domain.ReadValidity {
	v := domain.ValidityOf(snapshot, domain.Tick(tick))
	v.Pace, v.Wall, v.Versions = s.livePaceTicks, s.config.MaxAge, s.facts.store.Versions()
	s.validity.Store(&v)
	s.setShimDrift(v.Drift(domain.AgeInventory))
	return v
}

// drift is the ticks the running window's pace covers in the step's wall
// (the inventory bound's widening): what a step expects the tick to have
// moved by since its predecessor.
func (s *ClockScheduler) drift() domain.Tick {
	return domain.ReadValidity{Pace: s.livePaceTicks, Wall: s.config.MaxAge}.Drift(domain.AgeInventory)
}

// setShimDrift keeps domain.SetLiveDrift, the compatibility shim for
// callers not yet carrying a validity, in step with the scheduler's pace.
func (s *ClockScheduler) setShimDrift(drift domain.Tick) {
	if drift != domain.LiveDrift() {
		clockSchedulerLog("live drift %d -> %d ticks (pace %.0f ticks/s)", domain.LiveDrift(), drift, s.livePaceTicks)
	}
	domain.SetLiveDrift(drift)
}

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
func (s *ClockScheduler) StepWithReason(ctx context.Context, reason StepReason) (out ClockSchedulerResult, err error) {
	var paused time.Duration
	var readmit bool
	entered := time.Now()
	var gateWait time.Duration
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
	if gateWait = time.Since(entered); gateWait > 50*time.Millisecond {
		clockSchedulerLog("step waited %s for the player gate", gateWait.Round(time.Millisecond))
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
	// A wake's evidence lands on the due queue once, here, so it outlives
	// a step that runs no planners (a paced live wave, a stopping window)
	// and selects the same planners on the next (#625).
	if reason.Cause == StepWake {
		s.queue.wake(reason, s.facts.kindOf)
	}
	cache := bridge.NewChildReadCache(s.facts.cache)
	call = bridge.WithStepReadCache(call, cache)
	call = observation.WithDefinitionPool(call, s.facts.definitions)
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
	reviews := s.stepReviews(reason)
	if native, ok := s.native.(observation.ZonesNative); ok {
		zones = &zoneRefresher{native: native, store: s.facts.store, refreshes: &s.facts.zoneRefreshes}
		call = observation.WithZones(call, zones)
	}
	if native, ok := s.native.(PlanningWindowNative); ok {
		window = &planningWindow{native: native, store: s.facts.store, refreshes: &s.facts.windowRefreshes}
		call = observation.WithPlanningWindow(call, window)
	}
	stepBegan := time.Now()
	journal := &journalTimer{}
	defer func() {
		elapsed := time.Since(stepBegan)
		out.Journal = journal.total
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
		// The wait for the player gate before the step began: the
		// Worker's dispatch step, or manual control, holding it (#593).
		if gateWait > 0 {
			extra["gate_wait_ms"] = float64(gateWait) / float64(time.Millisecond)
		}
		extra["journal_ms"] = float64(journal.total) / float64(time.Millisecond)
		if cause == StepLive {
			s.liveStepWall = elapsed
		}
		if out.LivePlanning != "" {
			extra["live_planning"] = out.LivePlanning
		}
		if len(out.Waiting) > 0 {
			extra["waiting"] = out.Waiting
		}
		if out.Sections != nil {
			extra["sections"] = out.Sections
		}
		// The step's budgets (#623) beside what it used: reads against the
		// read budget, the planner waves against the wall budget, the
		// native-work window against its bound.
		budget := map[string]any{"wall_ms": float64(s.config.Budget.wall()) / float64(time.Millisecond), "native_ticks": s.nativeWorkBudget()}
		if s.config.Budget.Reads > 0 {
			budget["reads"] = s.config.Budget.Reads
			if reads.Total() > s.config.Budget.Reads {
				extra["reads_over_budget"] = true
			}
		}
		extra["budget"] = budget
		if out.CriticalWave > 0 {
			extra["critical_wave_ms"] = float64(out.CriticalWave) / float64(time.Millisecond)
		}
		if out.NativeWorkTicks > 0 {
			extra["native_work_ticks"] = out.NativeWorkTicks
		}
		if len(out.MissedCutoff) > 0 {
			extra["missed_cutoff"] = out.MissedCutoff
		}
		if len(out.HeldBy) > 0 {
			extra["held_by"] = out.HeldBy
		}
		if reason.Stopped {
			extra["stop"] = true
			if !reason.StopAt.IsZero() {
				extra["stop_latency_ms"] = float64(stepBegan.Sub(reason.StopAt)) / float64(time.Millisecond)
			}
		}
		if out.Window.Ticks != 0 {
			extra["window_ticks"] = out.Window.Ticks
		}
		out.Pacing.publish(extra)
		if out.Unwatched != 0 {
			extra["unwatched"] = out.Unwatched
		}
		// The coupled orders ready under this step's window, and whether
		// the step ended the window for them (#584): the stop-reason
		// breakdown counts coupled stops from these fields, since the stop
		// native journals for one is the controller's own cleanup.
		if out.CoupledOrders > 0 {
			extra["coupled_orders"] = out.CoupledOrders
			if out.Coupled && out.Cleaned {
				extra["coupled_stop"] = true
			}
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
		// What this review's planners asked through the cache is what the
		// next review's bundle carries (#593); a step that ran no review
		// says nothing about it.
		if reviews {
			s.facts.asks = cache.StepAsks()
		}
	}()
	attempts, err := journalTimed(journal, func() ([]store.ClockAttempt, error) { return s.player.journal.LoadClockAttempts(call, 4096) })
	if err != nil {
		return out, err
	}
	// The step's one native read: the current scope, the owned clock status
	// and the emergency census of the same tick (issue #127). Its tick and
	// emergency sections are seeded into the step cache, so the routine
	// census and the planners read them without another round trip; a step
	// about to review asks for the census families too (issue #180).
	started := s.clock.Now()
	stepRequest := s.bundleRequest(reason)
	bundle, err := s.readStepBundle(call, stepRequest)
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
	epochs, err := journalTimed(journal, func() ([]store.ClockEpochObligation, error) { return s.player.journal.LoadClockEpochs(call, 4096) })
	if err != nil {
		clockSchedulerLog("step exit: LoadClockEpochs %v", err)
		return out, err
	}
	// The player runs the game by hand under a stopped clock (#601): every
	// fact read while it runs is stale before the review commits, and no
	// window can be admitted against a running game. Re-take the clock
	// here, before the facts below are filled, once the player has let go
	// of the speed keys: pause natively and read the bundle again at the
	// paused tick, so the same step reviews and admits from it.
	if s.playerDriven(loaded, epochs) {
		if since, quiet := s.playerQuiet(); !quiet {
			clockSchedulerLog("stopped clock advanced to tick %d under the player: last Manual bump %s ago, waiting for %s of quiet before re-taking", loaded.Context.GetTick(), since.Round(time.Millisecond), s.playerQuietFor())
			out.Deferred = true
			return out, nil
		}
		status, e := s.bundleClockStatus(loaded, s.session.State().Snapshot)
		if e != nil {
			clockSchedulerLog("step exit: bundle clock status %v -> disable", e)
			return out, errors.Join(e, s.session.Disable())
		}
		s.livePace(status, started)
		clockSchedulerLog("stopped clock advanced to tick %d under the player (%.0f ticks/s, stopReason=%v) -> re-taking the clock: pausing natively", status.Context.GetTick(), s.pacePerSecond, status.GetStopped().GetReason())
		repaused, e := s.session.RepauseClock(call, status)
		if e != nil {
			clockSchedulerLog("step exit: native re-pause %v", e)
			return out, e
		}
		clockEvent(call, "clock-scheduler", "clock_retaken", "clock re-taken from the player", "tick", repaused.Context.GetTick(), "paused", repaused.GetActualPaused(), "pace", s.pacePerSecond, "stop_reason", status.GetStopped().GetReason().String())
		out.Retaken = true
		// The re-read below is judged against the paused tick: the families
		// the store holds fresh at the previous step's tick are not fresh
		// at this one, and a step that re-took the clock reviews in full.
		s.lastTick, s.lastTickKnown = repaused.Context.GetTick(), true
		reason.TickAdvanced = true
		reviews = s.stepReviews(reason)
		started = s.clock.Now()
		stepRequest = s.bundleRequest(reason)
		if bundle, err = s.readStepBundle(call, stepRequest); err != nil {
			return out, errors.Join(err, s.session.Disable())
		}
		if loaded = bundle.GetObserved(); loaded == nil {
			return out, errors.Join(executor.ErrHeld, s.session.Disable())
		}
		if err = bridge.ValidateContext(loaded.Context); err != nil {
			return out, errors.Join(err, s.session.Disable())
		}
	}
	if window != nil {
		window.scope, window.tick, window.review = factsScope(loaded.Context), loaded.Context.GetTick(), reviews
		window.view = decodePlanningWindowView(stepRequest, loaded)
	}
	if zones != nil {
		zones.scope, zones.tick, zones.carried = factsScope(loaded.Context), loaded.Context.GetTick(), loaded.Zones != nil
	}
	// The remaining entity sections refresh once per full review step,
	// delta reads over what the store holds, or the full section the
	// bundle carried (#593).
	if native, ok := s.native.(EntityNative); ok && reviews {
		wanted := s.sectionsWanted(s.previewSelection(reason).pick)
		refreshEntitySections(call, native, s.facts, loaded.Context.Identity, factsScope(loaded.Context), loaded.Context.GetTick(), entitySectionsCarried{zones: loaded.Zones != nil, buildings: loaded.Buildings != nil, bills: loaded.Bills != nil, wanted: wanted})
	}
	state := s.session.State()
	world := domain.GenerationSnapshot{Colony: domain.ColonyID(loaded.Context.Identity.GetColonyId()), Load: domain.LoadID(loaded.Context.Identity.GetLoadToken()), Map: domain.MapID(loaded.Context.Identity.GetMapId())}
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
	reason.TickAdvanced = out.Retaken || !s.lastTickKnown || status.Context.GetTick() != s.lastTick
	s.lastTick, s.lastTickKnown = status.Context.GetTick(), true
	s.livePace(status, started)
	out.Pacing = stepPacing(status)
	// The step's scope, tick and pace are fixed: every read and decision
	// below, and the Worker's dispatches under the window this step may
	// admit, judge their facts against this validity (#624).
	call = domain.WithReadValidity(call, s.readValidity(state.Snapshot, status.Context.GetTick()))
	telemetry.ObserveTick(status.Context.GetTick())
	if reason.Cause == StepTimer && s.fullStepDue() {
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
				// produced, so it was prepared at a stop, against a frozen
				// read of that result (#244). It no longer stops the epoch
				// (#584): the prerequisite's own OperationOutcome row is
				// the wake, this step plans live at once, and the native
				// CAS evidence the dependent's admission carries is what
				// keeps the order honest against a world that moved since
				// the read -- a stale read is refused, not obeyed.
				clockSchedulerLog("coupled orders %v ready under the running window -> live review", coupled)
				out.Coupled = true
				out.CoupledOrders = len(coupled)
			}
		}
		out.Running = true
		s.running.Store(status.GetRunning() != nil)
		var sel plannerSelectionResult
		if status.GetStopping() == nil {
			sel, err = s.selectPlanners(call, reason, status.Context.GetTick())
			if err != nil {
				return out, err
			}
		}
		// A coupled order ready under the window is planned live whatever
		// the wave's own cadence says (#584): it is the work the stop used
		// to be spent on, and it waits for no timer and no pace.
		coupledDue := out.Coupled && sel.planners
		if status.GetStopping() != nil || !coupledDue && !s.livePlanningDue(reason, sel) {
			clockSchedulerLog("clock already running under our own epoch -> no planners this step")
			out.Waiting = sel.waiting
			return out, nil
		}
		if ticks, paced := s.livePlanningPaced(); paced && !coupledDue {
			// The game outruns the wave: the facts it would plan on go
			// stale by a fraction of a day before its planners commit,
			// and the player gate it holds meanwhile keeps the Worker
			// from dispatching what the last stop admitted (#598). The
			// wave waits for the stop, one window away at most.
			out.LivePlanning = LivePlanningSkippedPace
			// One window away is a game day under a colony window: when
			// the window has nothing left to simulate (its last dispatched
			// work settled), end it now so the stop reviews at once,
			// rather than run the day out with every planner parked (#690).
			if done, err := s.windowWorkDone(call, state.Snapshot); err != nil {
				return out, err
			} else if done && status.GetRunning() != nil {
				clockSchedulerLog("clock running at %.0f ticks/s with no window work left -> ending the window for the planner wave", s.pacePerSecond)
				out.Cleaned = true
				return out, s.session.CleanupClock(call)
			}
			clockSchedulerLog("clock running at %.0f ticks/s: the last live step (%s) covers %d ticks, over %d -> planner wave waits for the stop", s.pacePerSecond, s.liveStepWall.Round(time.Millisecond), ticks, s.livePlanningTicks())
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
		if reason.Cause == StepTimer {
			s.lastLive = s.clock.Now()
		}
		reason.Cause = StepLive
		out.Reason = reason
		out.Waiting = sel.waiting
		clockSchedulerLog("step reason: %s", reason)
		if out.Planners, err = s.runPlanners(call, epoch, &out, sel, status); err != nil {
			return out, err
		}
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if sel.pick == nil {
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
		if epochs, err = journalTimed(journal, func() ([]store.ClockEpochObligation, error) { return s.player.journal.LoadClockEpochs(call, 4096) }); err != nil {
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
	sel, err := s.selectPlanners(call, reason, status.Context.GetTick())
	if err != nil {
		return out, err
	}
	out.Waiting = sel.waiting
	clockSchedulerLog("step reason: %s planners=%v waiting=%v", reason, sel.planners, sel.waiting)
	if sel.planners {
		if out.Planners, err = s.runPlanners(call, epoch, &out, sel, status); err != nil {
			return out, err
		}
		if len(out.HeldBy) > 0 {
			// A critical planner is still evaluating past the wall budget:
			// the window decision would read a verdict it does not have.
			// Hold, naming the planners; the next step evaluates again.
			clockSchedulerLog("critical planners %v past the wall budget %s -> holding admission", out.HeldBy, s.config.Budget.wall())
			clockEvent(call, "clock-scheduler", "admission_refused", "window not admitted", "refused", []string{"critical_wave_budget"}, "held_by", out.HeldBy, "wall_budget_ms", float64(s.config.Budget.wall())/float64(time.Millisecond))
			return out, executor.ErrHeld
		}
		factsTick = domain.Known(domain.Tick(status.Context.GetTick()))
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if sel.pick == nil {
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
	review, err := journalTimed(journal, func() (store.ClockReviewState, error) {
		return s.player.journal.ReadClockReview(call, s.config.Profile)
	})
	if err != nil {
		return out, err
	}
	plan, err := journalTimed(journal, func() (store.PlanState, error) { return s.player.journal.LoadPlan(call, state.Snapshot.Plan) })
	if err != nil {
		return out, err
	}
	work, fingerprint, err := clockSchedulerWork(plan, state.Snapshot)
	if err != nil {
		return out, err
	}
	{
		plans, err := journalTimed(journal, func() ([]store.PlanState, error) { return s.player.journal.LoadPlans(call, 256) })
		if err != nil {
			return out, err
		}
		remaining, items, err := s.routineWork(call, state.Snapshot, plans)
		if err != nil {
			return out, err
		}
		work = work || remaining
		fingerprint = append(fingerprint, items...)
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
		clockSchedulerLog("shrine: reason=%s shrine=%s hold=%s native_work_ticks=%d", out.Shrine.Reason, out.Shrine.Shrine, out.Shrine.Hold, out.Shrine.NativeWorkTicks)
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
	// Milk and eggs are gathered by native jobs alone.
	if out.Husbandry != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Husbandry.NativeWorkTicks)
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
	// A standing CriticalMedical deficit with no tend or rescue method to run
	// freezes development on "not selected: emergency" while contributing no
	// plan of its own; the clock then parks on no_work for as long as the
	// emergency stands, which is what a walking bleeding patient did for an
	// hour in #636. The emergency clears on game time, not on another method,
	// so the planner's lent window carries the step (cf. the deliberate
	// non-refusal for EmergencyCriticalMedical in EvaluateClockWindow).
	if out.Tend != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Tend.NativeWorkTicks)
	}
	if out.Rescue != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Rescue.NativeWorkTicks)
	}
	// A planner that failed on a native refusal produced neither work nor a
	// wait, and the same read is refused again next step while the game
	// stands still; one window lets the world move under it (#219).
	if wait := plannerRefusalWait(out.PlannerFailures); wait > 0 {
		clockSchedulerLog("planner failed on a native refusal -> lending %d ticks", wait)
		nativeWorkTicks = max(nativeWorkTicks, wait)
	}
	out.NativeWorkTicks = nativeWorkTicks
	if !work && s.config.RoutineMethods && nativeWorkTicks > 0 {
		work = true
		start.MaxTicks = min(start.MaxTicks, nativeWorkTicks, s.nativeWorkBudget())
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
	s.noWork = !out.Decision.Admitted && slices.Contains(out.Decision.Refused, policy.ClockWindowNoWork)
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
	if s.pace != nil {
		// The window resumes the rate the last critical waves earned.
		if ceiling := s.pace.Ceiling(); ceiling != 0 && (start.MaxTicksPerSecond == 0 || ceiling < start.MaxTicksPerSecond) {
			start.MaxTicksPerSecond = ceiling
		}
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
	if err == nil {
		s.seedLiveDrift(status.Context.GetTick())
	}
	return out, err
}

// seedLiveDrift sets the drift for the window this step just started from
// the last pace livePace measured in this process, and restarts the pace
// measurement at the start tick: the step's own status read predates the
// start, and a pace measured from it would count the stop as running time.
// Before this every window began under the stopped clock's zero drift and
// the Worker's dispatches under it, whose cached emergency read predates
// the inspection's first read by one round trip, held on the tick-exact
// bound until the next step measured the pace -- at boosted Ultrafast every
// dispatch of the window's first seconds (#410). The first window of a
// process, with no pace measured yet, still starts at zero.
func (s *ClockScheduler) seedLiveDrift(startTick int64) {
	s.paceTick, s.paceAt, s.paceKnown = startTick, s.clock.Now(), true
	if s.pacePerSecond <= 0 {
		return
	}
	s.livePaceTicks = s.pacePerSecond
	if v := s.validity.Load(); v != nil {
		started := *v
		started.Pace = s.livePaceTicks
		s.validity.Store(&started)
	}
	clockSchedulerLog("window started: pace %.0f ticks/s widens the step's bounds", s.pacePerSecond)
	s.setShimDrift(s.drift())
}

// nativeWorkBudget is the most ticks a step lends as a native-work window
// (#623): the configured bound, else the window's own MaxTicks.
func (s *ClockScheduler) nativeWorkBudget() uint32 {
	if s.config.Budget.NativeWork > 0 {
		return s.config.Budget.NativeWork
	}
	return s.config.Start.MaxTicks
}

// optionalWaveGrace is how long the optional wave is joined after the
// critical wave returned: the critical wave's own duration floored by
// OptionalGrace, or the whole wall budget when a pending planner missed the
// previous step's cutoff (starved), so a planner slower than the grace is
// not cancelled every step.
func (s *ClockScheduler) optionalWaveGrace(critical time.Duration, pending []string) time.Duration {
	for _, name := range pending {
		if s.starved[name] {
			return s.config.Budget.wall()
		}
	}
	return max(critical, s.config.Budget.optionalGrace())
}

// runPlanners runs the routine reviewer and the selected planner wave as an
// admission cycle and an optional wave (#623). The cycle joins the routine
// review and the critical planners under the wall budget: past it, the
// critical planners still pending are named on out.HeldBy and the step
// admits nothing. The optional planners run on the same snapshot and are
// joined for one critical-wave duration more (floored by OptionalGrace,
// never past the wall budget); those still evaluating at that cutoff are
// cancelled, recorded on out.MissedCutoff, and their results discarded. The
// coordinator then arbitrates the proposals that made the cutoff, and any
// carried from an earlier step, against this step's scope. The due queue
// records the planners that returned (#625): each is due again at its
// cadence, one that found the work of its kinds still open waits on it,
// and one that missed the cutoff keeps its marks for the next step.
func (s *ClockScheduler) runPlanners(call, epoch context.Context, out *ClockSchedulerResult, sel plannerSelectionResult, status *k.Status) ([]string, error) {
	arbiter := newStepArbiter()
	arbiter.late = s.late
	wave := newPlannerWave(call)
	defer wave.cancelOptional()
	defer s.recordWave(call, sel, wave, status.Context.GetTick())
	began := time.Now()
	wall := after(s.config.Budget.wall())
	wanted := s.sectionsWanted(sel.pick)
	out.Sections = sectionNames(wanted)
	// Under player acceleration the critical wave is the evidence the
	// backoff keeps inside the horizon, aged from the step's status read.
	watched := func(time.Duration, bool) {}
	readAt := began
	if s.paceKnown {
		readAt = s.paceAt
	}
	if s.pace != nil && status.GetRunning() != nil {
		s.paceEpoch.Store(proto.Clone(status.GetRunning().GetEpoch()).(*k.Epoch))
		watched = s.pace.Watch(call, readAt)
	}
	planners, err := s.stepPlanners(call, epoch, out, wave, arbiter, wanted, sel.pick)
	if err != nil {
		watched(0, false)
		return nil, err
	}
	if err = wave.group.WaitCritical(wall); err != nil {
		watched(0, false)
		if !errors.Is(err, errCutoff) {
			return nil, err
		}
		pending := wave.close()
		arbiter.close()
		out.CriticalWave = time.Since(began)
		for _, name := range pending {
			if wave.queuedCritical(name) {
				out.HeldBy = append(out.HeldBy, name)
			} else {
				out.MissedCutoff = append(out.MissedCutoff, name)
			}
		}
		return planners, nil
	}
	out.CriticalWave = time.Since(began)
	watched(s.clock.Now().Sub(readAt), true)
	grace := s.optionalWaveGrace(out.CriticalWave, wave.group.Pending())
	cutoff := after(min(grace, s.config.Budget.wall()-out.CriticalWave))
	select {
	case <-wall:
		cutoff = wall
	default:
	}
	if pending := wave.group.WaitUntil(cutoff); len(pending) > 0 {
		out.MissedCutoff = pending
		clockSchedulerLog("optional planners %v still evaluating %s after the critical wave (%s) -> missed the cutoff", pending, grace.Round(time.Millisecond), out.CriticalWave.Round(time.Millisecond))
	}
	s.starved = map[string]bool{}
	for _, name := range out.MissedCutoff {
		s.starved[name] = true
	}
	wave.close()
	arbiter.close()
	// The migrated planners proposed instead of committing: rank their
	// proposals by (priority, urgency, ID) against the claims the wave
	// left on the arbiter, the stock the review observed and what the
	// admitted plans hold of it, revalidate each against this step and
	// commit the winners (#622, #623, #628). The admission path beneath
	// each commit still checks stock itself.
	budget, err := s.stepBudget(call, out, arbiter)
	if err != nil {
		return nil, err
	}
	scope, _ := domain.ReadValidityFrom(call)
	var commitFailures []error
	out.Proposals, commitFailures = arbiter.coordinate(call, budget, scope)
	for _, outcome := range out.Proposals {
		switch {
		case outcome.Admitted && len(outcome.Preempted) != 0:
			clockSchedulerLog("proposal %s admitted plan %s after preempting %v", outcome.Proposal, outcome.Plan, outcome.Preempted)
		case outcome.Admitted:
			clockSchedulerLog("proposal %s admitted plan %s", outcome.Proposal, outcome.Plan)
		case outcome.Stale != "":
			clockSchedulerLog("proposal %s %s: %s", outcome.Proposal, outcome.Reason, outcome.Stale)
		case outcome.Reason == BuildingMethodDemand:
			clockSchedulerLog("proposal %s %s: %s (demand %v)", outcome.Proposal, outcome.Reason, outcome.Waiting, outcome.Demand)
		default:
			clockSchedulerLog("proposal %s %s: %s", outcome.Proposal, outcome.Reason, outcome.Waiting)
		}
		wave.decided(outcome.Planner, outcome.Reason)
	}
	wave.merge(out)
	// A failed planner is reported, not fatal: the step still evaluates the
	// clock window on what the other planners committed, and the failed
	// planner retries next step (#62).
	out.PlannerFailures = append(wave.group.Failures(), commitFailures...)
	for _, failure := range out.PlannerFailures {
		clockSchedulerLog("planner failed (isolated): %v", failure)
	}
	return planners, nil
}

// recordWave files a wave on the due queue (plannerQueue.ran, #625):
// the planners that returned before the cutoff are due again at their
// cadence, and one that reported existing work of its kinds waits on the
// open attempts of those kinds. The plans are read once, only when some
// planner reported existing work.
func (s *ClockScheduler) recordWave(call context.Context, sel plannerSelectionResult, wave *plannerWave, tick int64) {
	var plans []store.PlanState
	s.queue.ran(sel, wave.finishedNames(), wave.reason, tick, func(kinds []domain.ActionKind) []domain.ActionID {
		if plans == nil {
			loaded, err := s.player.journal.LoadPlans(call, 256)
			if err != nil {
				clockSchedulerLog("planner waits: LoadPlans %v", err)
				return nil
			}
			plans = loaded
		}
		return openWorkOfKinds(plans, kinds)
	})
}

// commitmentHorizon is how long an undispatched hold outlives the tick of
// its latest evidence before the coordinator stops counting it against the
// step's claims: one in-game day. The plan keeps its reservation in the
// journal; only the step's view releases it, and the admission path beneath
// the next dispatch checks stock again.
const commitmentHorizon domain.Tick = 60000

// stepBudget is the coordinator's quantity budget for this step (#628): the
// stock the routine review's resource runways observed, the journal's
// ActivePlanCommitments view at the review tick and the preempt path
// (store.PreemptGoalMethod). Nothing is read when no proposal claims a
// quantity, and a step without a review carries no stock, so every
// quantity is unbounded here and checked beneath the commit.
func (s *ClockScheduler) stepBudget(call context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (stepBudget, error) {
	if out.Routine == nil || !arbiter.claimsQuantities() {
		return stepBudget{}, nil
	}
	review := out.Routine.Review
	stock := map[policy.Resource]int64{}
	for _, runway := range review.ResourceRunways {
		if runway.Stock != nil && runway.Tick == review.Tick {
			stock[runway.Resource] = *runway.Stock
		}
	}
	if len(stock) == 0 {
		return stepBudget{}, nil
	}
	journal := s.player.journal
	commitments, err := journal.LoadPlanCommitments(call, review.Snapshot, review.Tick, commitmentHorizon)
	if err != nil {
		return stepBudget{}, fmt.Errorf("plan commitments: %w", err)
	}
	if demand := commitments.DemandTotals(); len(demand) != 0 {
		clockSchedulerLog("plan commitments at tick %d: held %v, demand %v", review.Tick, commitments.CommittedTotals(), demand)
	}
	preempt := func(ctx context.Context, held store.PlanCommitment) error {
		_, err := journal.PreemptGoalMethod(ctx, held.Goal, held.Revision, held.Plan)
		return err
	}
	return stepBudget{Stock: stock, Commitments: commitments, Preempt: preempt}, nil
}

// livePace measures the running window's pace from the previous step's
// status tick: the pace the step's read validity widens its bounds by
// (#345, #624), so a live step and the Worker's dispatches under it read
// one plan however fast the game runs. A stopped, stopping or
// never-started clock, or a pace not yet measured, leaves the pace at
// zero so a paused step keeps the tick-exact bound.
func (s *ClockScheduler) livePace(status *k.Status, readAt time.Time) {
	tick := status.Context.GetTick()
	pace := 0.0
	// The pace is measured under a running window and, the same way, under
	// a stopped clock the player runs by hand (#601); only the window
	// widens the bounds.
	running := status.GetRunning() != nil
	if (running || clockPlayerRunning(status)) && s.paceKnown && tick > s.paceTick && readAt.After(s.paceAt) {
		s.pacePerSecond = float64(tick-s.paceTick) / readAt.Sub(s.paceAt).Seconds()
		if running {
			pace = s.pacePerSecond
		}
	}
	s.livePaceTicks = pace
	s.setShimDrift(s.drift())
	s.paceTick, s.paceAt, s.paceKnown = tick, readAt, true
}

// livePlanningDue reports whether a step that found its own window running
// plans under it (#243): a wake or full step at once, a timer step when
// the full-step safety net is due, so a running window costs one planner
// wave per FullStepEvery rather than one per step. Whether the pace lets
// the wave run is livePlanningPaced.
func (s *ClockScheduler) livePlanningDue(reason StepReason, sel plannerSelectionResult) bool {
	if s.config.Routine == nil || !sel.planners {
		return false
	}
	if reason.Cause == StepTimer {
		return s.liveWaveDue()
	}
	return true
}

// liveWaveDue spaces the timer-driven live waves (DefaultLiveWaveEvery).
func (s *ClockScheduler) liveWaveDue() bool {
	return s.lastLive.IsZero() || s.clock.Now().Sub(s.lastLive) >= DefaultLiveWaveEvery
}

// selectPlanners is the step's selection over the due queue at tick
// (plannerQueue.selection): a wake's evidence was folded when the step
// began. The waits are checked against the journal only while any is
// held, so a dependency that settled without an outcome event (an
// immediate designation) releases its planner at the next step.
func (s *ClockScheduler) selectPlanners(call context.Context, reason StepReason, tick int64) (plannerSelectionResult, error) {
	if s.noWork && !reason.TickAdvanced {
		s.queue.stalled()
	}
	var stillOpen func(domain.ActionID) bool
	if len(s.queue.waits) > 0 {
		plans, err := s.player.journal.LoadPlans(call, 256)
		if err != nil {
			return plannerSelectionResult{}, err
		}
		stillOpen = openWorkIndex(plans)
	}
	switch reason.Cause {
	case StepSettled:
		return s.queue.selection(tick, false, true, stillOpen), nil
	case StepTimer, StepWake, StepLive:
		return s.queue.selection(tick, false, false, stillOpen), nil
	}
	return s.queue.selection(tick, true, false, stillOpen), nil
}

// previewSelection is the selection a step for reason is expected to make
// once its status is read, judged at the tick the step expects to observe
// (the previous status tick plus the running window's drift) over a copy
// of the queue: what bundleRequest sizes the bundle by. A wrong guess costs
// a heavier bundle or a dedicated read, never a wrong fact.
func (s *ClockScheduler) previewSelection(reason StepReason) plannerSelectionResult {
	if reason.Cause == StepTimer && s.fullStepDue() {
		reason.Cause = StepFull
	}
	return plannerSelection(reason, s.facts.kindOf, s.queue, s.lastTick+int64(s.drift()))
}

// livePlanningPaced reports whether the running window outruns a live
// planner wave (#598): the ticks the measured pace covers in the previous
// live step's wall time, and whether they exceed LivePlanningTicks. It
// keys on that ratio, not the speed, so a capped Ultrafast (900 ticks/s
// over a 5 s step is 4.5k ticks) plans live as before, while an uncapped
// game at 1000+ ticks/s under a 10 s wave does not. Unmeasured (no pace,
// no live step yet) never skips.
func (s *ClockScheduler) livePlanningPaced() (domain.Tick, bool) {
	if s.pacePerSecond <= 0 || s.liveStepWall <= 0 {
		return 0, false
	}
	ticks := domain.Tick(s.pacePerSecond * s.liveStepWall.Seconds())
	return ticks, ticks > s.livePlanningTicks()
}

func (s *ClockScheduler) livePlanningTicks() domain.Tick {
	if s.config.LivePlanningTicks > 0 {
		return s.config.LivePlanningTicks
	}
	return DefaultLivePlanningTicks
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
//
// A review step's bundle also carries the step families (#593): the
// entity sections the refreshers would otherwise read after the bundle
// (zones, buildings, bills, each in full when the store's row is absent
// or stale under the expected tick), the held planning window's delta
// since its as-of tick, and what the previous review's planners asked
// for through the read cache (the built census, traders, world
// progression, resource sources), so those reads are cache hits.
//
// A step expected to run a planner subset reads at cadence only the
// sections its planners declare (sectionsWanted, #625): a section none of
// them consumes rides only while the store holds nothing usable for it
// (absent, or dropped or marked by an invalidation), and is served held
// otherwise, however old. The colony facts always ride: they are the
// review's identity anchor.
func (s *ClockScheduler) bundleRequest(reason StepReason) *o.BundleRequest {
	request := &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true)}
	if s.stepReviews(reason) {
		request.ColonyFacts = proto.Bool(true)
		tick := s.lastTick + int64(s.drift())
		wanted := s.sectionsWanted(s.previewSelection(reason).pick)
		population, research, pawns := bundleFamilies(s.facts.store, tick, s.lastTickKnown, wanted)
		request.Population, request.Research, request.ColonistPawns = proto.Bool(population), proto.Bool(research), proto.Bool(pawns)
		request.ColonistPawnFields, request.PopulationFields, request.ResearchFields = bundleMasks()
		s.bundleStepFamilies(request, tick, wanted)
	}
	return request
}

// sectionRides reports whether a review step at tick reads section: when
// the tick is unknown, when the store holds nothing usable for it, or
// when a planner this step runs consumes it (wanted, nil for all) and
// the held value is past its cadence.
func sectionRides(store *facts.Store, section facts.Section, tick int64, known bool, wanted map[facts.Section]bool) bool {
	if !known || !store.Held(section, tick) {
		return true
	}
	return (wanted == nil || wanted[section]) && !store.Fresh(section, tick)
}

// bundleStepFamilies adds the step families to a review bundle request
// (bundleRequest, #593).
func (s *ClockScheduler) bundleStepFamilies(request *o.BundleRequest, tick int64, wanted map[facts.Section]bool) {
	store := s.facts.store
	stale := func(section facts.Section) bool { return sectionRides(store, section, tick, s.lastTickKnown, wanted) }
	_, entities := s.native.(EntityNative)
	_, zones := s.native.(observation.ZonesNative)
	if entities {
		request.Buildings, request.Bills = proto.Bool(stale(facts.Buildings)), proto.Bool(stale(facts.Bills))
	}
	if entities || zones {
		request.Zones = proto.Bool(stale(facts.Zones))
	}
	if _, ok := s.native.(PlanningWindowNative); ok && stale(facts.PlanningCells) {
		if view := s.planningWindowView(); view != nil {
			request.PlanningWindowView = view
		} else {
			request.PlanningWindow = s.legacyPlanningWindow()
		}
	}
	asks := s.facts.asks
	if !s.lastTickKnown && asks.Empty() {
		// No review has asked yet: the first carries the families every
		// review's planners ask for, rather than paying them one hop each
		// once (#593). The resources depend on the colony and wait for
		// the planners to name them.
		asks.BuiltBuildings, asks.Traders, asks.WorldProgression = true, true, true
	}
	request.BuiltBuildings, request.Traders, request.WorldProgression = proto.Bool(asks.BuiltBuildings), proto.Bool(asks.Traders), proto.Bool(asks.WorldProgression)
	request.ResourceSources = append([]string(nil), asks.Resources...)
}

// decodePlanningWindowView decodes the view a step's bundle carried for
// the refresher (#650); nil when none rode. A view that fails to decode
// is logged and left unused, so the refresher reads the window natively,
// once, as it would without one.
func decodePlanningWindowView(request *o.BundleRequest, loaded *o.BundleSnapshot) *bridge.PlanningWindowView {
	if loaded.PlanningWindowView == nil || request.PlanningWindowView == nil {
		return nil
	}
	view, err := bridge.DecodePlanningWindowView(loaded.PlanningWindowView, request.PlanningWindowView)
	if errors.Is(err, bridge.ErrPlanningViewPending) {
		// The native is still capturing (#654): the window is read the
		// usual way when it is due, and the next step asks again.
		clockSchedulerLog("planning window view %v", err)
		return nil
	}
	if err != nil {
		clockSchedulerLog("planning window view refused: %v", err)
		return nil
	}
	return &view
}

// planningWindowView is the opt-in planning window view (#650) for the
// held window's region: nil when no window is held, the region is past
// the view's bound, or the native refused the view once.
func (s *ClockScheduler) planningWindowView() *o.BundlePlanningWindowViewRequest {
	held, ok := facts.Get[observation.PlanningCells](s.facts.store, facts.PlanningCells)
	if !ok || s.facts.viewUnsupported {
		return nil
	}
	return bridge.BundlePlanningWindowViewRequest(held.Value.Region)
}

// legacyPlanningWindow is the same-tick planning window band for the held
// window, a delta since its as-of tick; nil when none is held.
func (s *ClockScheduler) legacyPlanningWindow() *o.BundlePlanningWindowRequest {
	held, ok := facts.Get[observation.PlanningCells](s.facts.store, facts.PlanningCells)
	if !ok {
		return nil
	}
	return bridge.BundlePlanningWindowRequest(&bridge.BundlePlanningWindow{Region: held.Value.Region, Since: held.AsOf})
}

// bundleMasks is the review bundle's field mask per continuous family
// (#360): the sub-blocks the routine review decodes. The mask is the same
// whatever planners the step selects: the review's DetectRoutine consumes
// every decoded block, so a family that rides always rides whole; a
// planner subset narrows which families ride instead (bundleFamilies,
// #625), serving the rest held. Each mask is an empty message: present, so native drops
// the blocks the controller never reads (gear detail, inventory,
// capacities, surgery bills, relations; owned beds, nutrition, supported
// interactions; research unlocks, costs, facilities). Traits and backstory
// (the ages) ride: the work and schedule planners build the pawn profile
// from them (observation.routine_work), and stripped they read as a
// colonist with no traits. A native that predates the masks returns the
// whole family; the decoders read the same fields either way.
func bundleMasks() (*o.PawnFields, *o.PopulationFields, *o.ResearchFields) {
	return bundlePawnMask(), &o.PopulationFields{}, &o.ResearchFields{}
}

// bundlePawnMask is the colonist mask every bundle read carries, the
// admission warm read included, since it seeds the same rows.
func bundlePawnMask() *o.PawnFields {
	return &o.PawnFields{IncludeTraits: proto.Bool(true), IncludeBackstory: proto.Bool(true)}
}

// bundleFamilies decides which continuous families ride a review step's
// bundle: a family whose section the store holds fresh at tick, the tick
// the step is expected to observe (the previous status tick plus the
// running window's drift, so the guess errs on the later side), is served
// by the review from the store and stays out; every family rides when the
// tick is unknown (the first step) or the section is stale or absent. A
// section no selected planner consumes (wanted, nil for all) is served
// held past its cadence too (sectionRides, #625).
func bundleFamilies(store *facts.Store, tick int64, known bool, wanted map[facts.Section]bool) (population, research, pawns bool) {
	return sectionRides(store, facts.Population, tick, known, wanted), sectionRides(store, facts.Research, tick, known, wanted), sectionRides(store, facts.Pawns, tick, known, wanted)
}

// stepReviews is whether a step taken for reason is expected to run the
// routine review: never without a reviewer, on a timer step only when the
// full-step safety net is due, and for any other cause as plannerSelection
// decides.
func (s *ClockScheduler) stepReviews(reason StepReason) bool {
	if s.config.Routine == nil {
		return false
	}
	if s.running.Load() {
		// Under a window believed running the review is the live wave,
		// which the pace may hold for the stop (#598): the bundle then
		// stays the light status read. A window that turns out stopped
		// reviews on dedicated reads, a heavier step, never a wrong fact.
		if _, paced := s.livePlanningPaced(); paced {
			return false
		}
	}
	if reason.Cause == StepTimer && s.running.Load() && !s.liveWaveDue() && !s.fullStepDue() {
		return false
	}
	return s.previewSelection(reason).planners
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
// the wave sharing arbiter, returning the queued names without waiting.
// Routine's own error aborts before anything is queued; an error from a
// queued planner is isolated by the wave and surfaces later, from its
// failures, without stopping the step. wanted names the sections the
// selected planners consume (nil: every section, #625). The colony stage's
// Foothold hold also drops the comfort-class planners and promotes the
// startup planners into the critical cycle for the step (#658).
func (s *ClockScheduler) stepPlanners(call, epoch context.Context, out *ClockSchedulerResult, wave *plannerWave, arbiter *stepArbiter, wanted map[facts.Section]bool, pick func(plannerEntry) bool) ([]string, error) {
	startup := false
	if s.config.Routine != nil {
		review, err := s.config.Routine.step(call, epoch, arbiter, wanted)
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
		if err := s.establishExtent(call, review.Review.Tick); err != nil {
			return nil, fmt.Errorf("colony extent: %w", err)
		}
		// A mental break is not a hold: it only ends with ticks, so refusing
		// every window while one is observed stopped the clock for good in
		// autonomous play. The break stays visible through the pawn's mood
		// goal and the native hazard supervisor keeps its authority.
		if stage := review.Review.Stage; stage != nil && stage.HoldsDevelopment() {
			// The Foothold hold (#630): the comfort-class planners of the
			// optional wave are not eligible while the shelter is unmet,
			// so the wave spends nothing evaluating proposals the ranking
			// would refuse. The same hold makes the shelter's own planner
			// critical for this step (#658): it is what the stage waits for,
			// and its siting reads outlast the optional grace.
			clockSchedulerLog("colony stage %s holds the comfort-class planners and makes the startup planners critical: %s", stage.Stage, stage.Reason)
			inner := pick
			pick = func(entry plannerEntry) bool {
				return entry.priority != plannerComfort && (inner == nil || inner(entry))
			}
			startup = true
		}
	}
	return s.queuePlanners(call, epoch, wave, arbiter, pick, startup), nil
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
// epoch's current tick (domain.PlanSpec.CoupledPending), which the step
// plans live for without ending the window (#584). Both are empty when the plan no longer
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

// routineWork is clockSchedulerWork over the authorized routine plans of
// the catalog (every plan but the root's own).
func (s *ClockScheduler) routineWork(call context.Context, root domain.GenerationSnapshot, plans []store.PlanState) (bool, []clockWorkItem, error) {
	work := false
	var fingerprint []clockWorkItem
	for _, method := range plans {
		if method.Spec.ID() == root.Plan {
			continue
		}
		target := root
		target.Plan, target.Revision = method.Spec.ID(), method.Spec.Revision()
		if err := (planAuthorizer{s.player.journal, s.config.RoutineMethods}).AuthorizeRoutinePlan(call, root, target); err != nil {
			continue
		}
		remaining, items, err := clockSchedulerWork(method, target)
		if err != nil {
			return false, nil, err
		}
		work = work || remaining
		fingerprint = append(fingerprint, items...)
	}
	return work, fingerprint, nil
}

// windowWorkDone reports whether no plan, the root's or a routine one,
// has work left for the running window to simulate.
func (s *ClockScheduler) windowWorkDone(call context.Context, root domain.GenerationSnapshot) (bool, error) {
	plan, err := s.player.journal.LoadPlan(call, root.Plan)
	if err != nil {
		return false, err
	}
	if work, _, err := clockSchedulerWork(plan, root); err != nil || work {
		return false, err
	}
	plans, err := s.player.journal.LoadPlans(call, 256)
	if err != nil {
		return false, err
	}
	work, _, err := s.routineWork(call, root, plans)
	return !work, err
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
		if p.Action().Kind() == domain.SupplyAllowAction || p.Action().Kind() == domain.SupplyForbidAction || p.Action().Kind() == domain.WorkAssignmentAction || p.Action().Kind() == domain.ZoneCreateAction || p.Action().Kind() == domain.BuildingTemperatureAction || p.Action().Kind() == domain.BedMedicalAction || p.Action().Kind() == domain.BedAssignAction || p.Action().Kind() == domain.GrowerCropAction || p.Action().Kind() == domain.ClaimBuildingAction || p.Action().Kind() == domain.ZoneDeleteAction {
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
				domain.RepairAction, domain.CleanAction, domain.WasteAction, domain.MineAcquisitionAction, domain.DeconstructionAction, domain.CutPlantAction, domain.CoverClearanceAction, domain.ProductionPolicyAction, domain.MoodReliefAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.TradeAction, domain.QuestAcceptAction, domain.WallRemovalAction, domain.OpenCasketAction, domain.CaravanDepartureAction:
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

// clockPlayerRunning reports whether a stopped clock's game is running: the
// player un-paused after an external pause and drives the speed by hand
// (#601). Never a running or stopping epoch, which the scheduler paces.
func clockPlayerRunning(status *k.Status) bool {
	return status.GetStopped() != nil && status.ActualPaused != nil && !status.GetActualPaused()
}

// playerDriven reports whether the bundle just read shows the player
// running the game under a stopped clock this process no longer owes: the
// status is stopped and not actually paused, the tick moved on since the
// previous step's (a first step, with no previous tick, waits for the
// next), no epoch is owed, and authority stands so the re-take's review
// can admit. A player who paused and stays paused never trips it.
func (s *ClockScheduler) playerDriven(loaded *o.BundleSnapshot, epochs []store.ClockEpochObligation) bool {
	status := loaded.GetClockStatus()
	if !clockPlayerRunning(status) || !s.lastTickKnown || status.Context.GetTick() <= s.lastTick {
		return false
	}
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			return false
		}
	}
	return s.session.State().Enabled
}

// noteManual records a Manual authority change the poll committed: the
// player pressed a speed key at that wall time (#601).
func (s *ClockScheduler) noteManual(at time.Time) { s.manualAt.Store(at.UnixNano()) }

// playerQuiet reports how long since the last Manual bump and whether that
// is at least PlayerQuiet; with no bump seen the clock is quiet.
func (s *ClockScheduler) playerQuiet() (time.Duration, bool) {
	at := s.manualAt.Load()
	if at == 0 {
		return 0, true
	}
	since := s.clock.Now().Sub(time.Unix(0, at))
	return since, since >= s.playerQuietFor()
}

func (s *ClockScheduler) playerQuietFor() time.Duration {
	if s.config.PlayerQuiet > 0 {
		return s.config.PlayerQuiet
	}
	return DefaultPlayerQuiet
}

// journalTimer sums the wall time of a step's own journal reads (#634).
type journalTimer struct{ total time.Duration }

func journalTimed[T any](t *journalTimer, read func() (T, error)) (T, error) {
	began := time.Now()
	v, err := read()
	t.total += time.Since(began)
	return v, err
}

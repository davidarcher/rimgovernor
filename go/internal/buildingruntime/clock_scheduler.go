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

// ClockWindowNative is the scheduler's native read side: the step read is
// the one read a step or a renewal opens with (scope, clock status,
// emergency census); the plain clock status read serves the renewal's
// post-write re-check. The zone census, planning window and entity sections
// are required reads: every step attaches them (bridge.Client serves all).
type ClockWindowNative interface {
	observation.ZonesNative
	PlanningWindowNative
	EntityNative
	ReadStep(context.Context, bridge.StepRequest) (*o.BundleSnapshot, bridge.Result, error)
	ReadClockStatus(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
}
type ClockSchedulerConfig struct {
	CookingBills, PreservationBills, ButcherBills, CookAheadBills, ArtBills, SurgeryPartBills, BabyFoodBills, MechBills *RoundsBillPlanner
	Butcher                                                                                                             *RoundsBuildingPlanner
	Fields                                                                                                              *RoundsFieldPlanner
	Profile                                                                                                             string
	Start                                                                                                               bridge.ClockStart
	// PaceHorizonTicks is the safe horizon player acceleration's backoff
	// keeps the critical evidence inside (issue #627); zero is
	// DefaultPaceHorizonTicks. Unused unless Start.PlayerAccelerated.
	PaceHorizonTicks domain.Tick
	// FollowPlayerSpeed starts each window at the speed the player last
	// chose in the loaded game (Status.player_speed, #875), Ultrafast when
	// none was chosen; an Ultrafast window runs player acceleration.
	// Start.Speed and Start.PlayerAccelerated are then only the fallback.
	FollowPlayerSpeed bool
	MaxAge            time.Duration
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
	// PlayerQuiet is how long after the last Manual authority bump (the
	// player pressing a speed key) a step waits before it re-takes a clock
	// the player runs by hand under a stopped epoch (#601). Zero means
	// DefaultPlayerQuiet.
	PlayerQuiet time.Duration
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
	Rounds          *Rounder
	FoodAcquisition *RoundsAcquisitionPlanner
	// ResourceAcquisition chops, forages and hunts for MaintainResource.
	ResourceAcquisition *RoundsAcquisitionPlanner
	PestAcquisition     *RoundsAcquisitionPlanner
	Work                *RoundsWorkPlanner
	Supplies            *RoundsSupplyPlanner
	Blight              *RoundsBlightPlanner
	Pollution           *RoundsPollutionPlanner
	MechCharger         *RoundsMechChargerPlanner
	GeneBank            *RoundsGeneBankPlanner
	Armory              *RoundsArmoryPlanner
	Clearance           *RoundsClearancePlanner
	Shrine              *RoundsShrinePlanner
	Sleeping            *RoundsBuildingPlanner
	Cooking             *RoundsBuildingPlanner
	Comfort             *RoundsBuildingPlanner
	BasicComfort        *RoundsBuildingPlanner
	Workshop            *RoundsBuildingPlanner
	Hospital            *RoundsHospitalPlanner
	SleepingUpkeep      *RoundsSleepingUpkeepPlanner
	Expansion           *RoundsBuildingPlanner
	Power               *RoundsBuildingPlanner
	Temperature         *RoundsBuildingPlanner
	Refrigeration       *RoundsBuildingPlanner
	Lighting            *RoundsBuildingPlanner
	Flooring            *RoundsBuildingPlanner
	Routes              *RoundsBuildingPlanner
	Defense             *RoundsDefensePlanner
	Tend                *RoundsTendPlanner
	Rescue              *RoundsRescuePlanner
	Equip               *RoundsEquipPlanner
	Repair              *RoundsRepairPlanner
	FireSafety          *RoundsFireSafetyPlanner
	Clean               *RoundsCleanPlanner
	Gear                *RoundsGearPlanner
	Medical             *RoundsMedicalPlanner
	Surgery             *RoundsSurgeryPlanner
	FoodStorageUpkeep   *RoundsFoodStorageUpkeepPlanner
	AnimalContainment   *RoundsAnimalContainmentPlanner
	Recovery            *RoundsRecoveryPlanner
	Husbandry           *RoundsHusbandryPlanner
	Rules               *RoundsRulesPlanner
	PrisonerInteraction *RoundsPrisonerInteractionPlanner
	PopulationCustody   *RoundsPopulationCustodyPlanner
	PopulationJoiner    *RoundsPopulationJoinerPlanner
	Research            *RoundsResearchPlanner
	StorageShelves      *RoundsStorageShelvesPlanner
	Resource            *RoundsResourcePlanner
	HomeCoverage        *RoundsHomeCoveragePlanner
	MaintainShelter     *MaintainShelterPlanner
	Firebreak           *RoundsFirebreakPlanner
	Psylink             *RoundsPsylinkPlanner
	CreepJoiners        *RoundsCreepJoinerPlanner
	Permits             *RoundsPermitsPlanner
	IdeoRoles           *RoundsIdeoRolesPlanner
	Ideoligion          *RoundsIdeoligionPlanner
	Rituals             *RoundsRitualsPlanner
	StoneShell          *RoundsStoneShellPlanner
	Stockpiles          *RoundsStockpilePlanner
	DefenseLayout       *RoundsDefenseLayoutPlanner
	Burial              *RoundsBurialPlanner
	Incineration        *RoundsIncinerationPlanner
	MoodRelief          *RoundsMoodReliefPlanner
	Dialog              *RoundsDialogPlanner
	Trade               *RoundsTradePlanner
	RoundsMethods       bool
	// WorldReady, when set, runs after the step's opening read and before
	// any review: it rebuilds the store for the observed world if needed
	// (#1123) and reports whether a rebuild reset the review cache since
	// the last step, which makes this step review in full.
	WorldReady func(context.Context, *c.ObservationContext) (bool, error)
	// Autosave, when set, runs in the stop between windows: the step has
	// decided to admit a window, the game is paused, and nothing is in flight
	// (#2360). It gets the observed identity and tick and handles its own
	// failures; a save never fails the step.
	Autosave func(ctx context.Context, identity *c.Identity, tick int64)
}
type ClockSchedulerResult struct {
	// Pacing is what the step's clock status said of the pace (#627).
	Pacing                                                                                                              StepPacing
	CookingBills, PreservationBills, ButcherBills, CookAheadBills, ArtBills, SurgeryPartBills, BabyFoodBills, MechBills *RoundsBillResult
	Butcher                                                                                                             *RoundsBuildingResult
	Fields                                                                                                              *RoundsFieldResult
	Attempt                                                                                                             *store.ClockAttempt
	Decision                                                                                                            policy.ClockWindowDecision
	// Window is the colony window the admission tail sized (before any
	// native-work or combat bound), zero when the tail did not run.
	Window                       ClockWindowSize
	Rounds                       *store.RoundsResult
	FoodAcquisition              *RoundsAcquisitionResult
	Rules                        *RoundsRulesResult
	ResourceAcquisition          *RoundsAcquisitionResult
	PestAcquisition              *RoundsAcquisitionResult
	Work                         *RoundsWorkResult
	Supplies                     *RoundsSupplyResult
	Blight                       *RoundsBlightResult
	Pollution                    *RoundsPollutionResult
	MechCharger                  *RoundsBuildingResult
	GeneBank                     *RoundsBuildingResult
	Armory                       *RoundsArmoryResult
	Clearance                    *RoundsClearanceResult
	Shrine                       *RoundsShrineResult
	Sleeping                     *RoundsBuildingResult
	Cooking                      *RoundsBuildingResult
	Comfort                      *RoundsBuildingResult
	BasicComfort                 *RoundsBuildingResult
	Workshop                     *RoundsBuildingResult
	Hospital                     *RoundsBuildingResult
	SleepingUpkeep               *RoundsBuildingResult
	Expansion                    *RoundsBuildingResult
	Power                        *RoundsBuildingResult
	Temperature                  *RoundsBuildingResult
	Refrigeration                *RoundsBuildingResult
	Lighting                     *RoundsBuildingResult
	Flooring                     *RoundsBuildingResult
	Routes                       *RoundsBuildingResult
	Defense                      *RoundsDefenseResult
	Tend                         *RoundsTendResult
	Rescue                       *RoundsRescueResult
	Equip                        *RoundsEquipResult
	Repair                       *RoundsRepairResult
	FireSafety                   *RoundsFireSafetyResult
	Clean                        *RoundsCleanResult
	Gear                         *RoundsGearResult
	Medical                      *RoundsMedicalResult
	Surgery                      *RoundsSurgeryResult
	FoodStorageUpkeep            *RoundsFoodStorageUpkeepResult
	AnimalContainment            *RoundsAnimalContainmentResult
	Recovery                     *RoundsRecoveryResult
	Husbandry                    *RoundsHusbandryResult
	PrisonerInteraction          *RoundsPrisonerInteractionResult
	PopulationCustody            *RoundsPopulationCustodyResult
	PopulationJoiner             *RoundsPopulationJoinerResult
	Research                     *RoundsResearchResult
	StorageShelves               *RoundsStorageShelvesResult
	Resource                     *RoundsResourceResult
	HomeCoverage                 *RoundsHomeCoverageResult
	MaintainShelter              *MaintainShelterResult
	Firebreak                    *RoundsFirebreakResult
	Psylink                      *RoundsPsylinkResult
	CreepJoiners                 *RoundsCreepJoinerResult
	Permits                      *RoundsPermitsResult
	IdeoRoles                    *RoundsIdeoRolesResult
	Ideoligion                   *RoundsIdeoligionResult
	Rituals                      *RoundsRitualsResult
	StoneShell                   *RoundsStoneShellResult
	Stockpiles                   *RoundsStockpileResult
	DefenseLayout                *RoundsDefenseLayoutResult
	Burial                       *RoundsBurialResult
	Incineration                 *RoundsIncinerationResult
	MoodRelief                   *RoundsMoodReliefResult
	Dialog                       *RoundsDialogResult
	Trade                        *RoundsTradeResult
	Running, Reconciled, Cleaned bool
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
	// Planners names the catalog planners this step queued, in catalog order.
	Planners []string
	// Waiting names the planners the selection skipped because each still
	// waits on the open work it reported (plannerQueue.waits, #625); the
	// clock_step row reports it as waiting.
	Waiting []string
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
	// rounds and the critical planners.
	CriticalWave time.Duration
	// PlannerMS is each queued planner's wall time in milliseconds (the
	// elapsed time so far for one that missed the cutoff): the clock_step
	// row's planner_ms (#1915).
	PlannerMS map[string]float64
	// NativeWorkTicks is the native-work window the planners asked for,
	// the largest of their NativeWorkTicks, before the budget bounds it.
	NativeWorkTicks uint32
	// Reason is the step reason applied, with TickAdvanced resolved and a
	// timer promoted to full by FullStepEvery.
	Reason StepReason
}
type ClockScheduler struct {
	// plannerReasons is the last planner refusal filed per goal.
	plannerReasons      plannerReasonLog
	player              *Player
	session             *Session
	native              ClockWindowNative
	config              ClockSchedulerConfig
	clock               executor.Clock
	pollGate, renewGate chan struct{}
	// pollIdentity is the world the event poll asks for: the last page's,
	// else found by one bare bundle read. Poll goroutine only.
	pollIdentity *c.Identity
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
	// idleRefusals counts consecutive steps that refused no_work with every
	// planner returned; at idleLendAfter the step lends one idleLendTicks
	// window so game time passes (see idleLend). Under the player gate.
	idleRefusals int
	// paceTick and paceAt are the previous step's status tick and the wall
	// time it was read at, the basis of the running window's pace
	// (livePace); touched only under the player gate.
	paceTick  int64
	paceAt    time.Time
	paceKnown bool
	// pacePerSecond is the last pace livePace measured under a running
	// window, kept across stops so the next window starts with a pace
	// (seedLiveDrift) instead of the stopped clock's zero.
	pacePerSecond float64
	// combatStops measures combat windows' stops (#849), under the player gate.
	combatStops combatStopMetrics
	// livePaceTicks is the pace a step projects the tick by (drift):
	// pacePerSecond while a window runs, zero under a stopped clock. Touched only under the player gate.
	livePaceTicks float64
	// validity is the read validity of the latest step whose scope was
	// fixed (#624): what the Worker's dispatches under that step's window
	// judge their reads by (Validity).
	validity *atomic.Pointer[domain.ReadValidity]
	// manualAt is the wall time (unix nanoseconds, zero for none) of the
	// last Manual authority change a committed page carried: the player
	// pressing a speed key. Written under the poll gate, read under the
	// player gate by the re-take decision (#601).
	manualAt *atomic.Int64
	// running is the scheduler's belief that a colony window it admitted
	// is still running: set by the step that dispatched or observed it,
	// cleared by the step or poll that saw it stopped.
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
	readmitOwed bool
	// queue is the planners' due queue (#625): what a step selects
	// between full steps, and the waits it skips. Touched only under the
	// player gate.
	queue *plannerQueue
	// late carries proposals that reached a step's arbiter after its
	// cutoff to the next step's coordinator (#623).
	late *lateProposals
	// catalog is the planner table the steps queue from: plannerCatalog,
	// or a table a test substitutes.
	catalog []plannerEntry
}

func NewClockScheduler(player *Player, session *Session, native ClockWindowNative, config ClockSchedulerConfig, clock executor.Clock) (*ClockScheduler, error) {
	if player == nil || session == nil || player.session != session || player.journal != session.journal || session.clock == nil || native == nil || clock == nil || config.MaxAge <= 0 || config.MaxAge > time.Minute {
		return nil, fmt.Errorf("%w: NewClockScheduler: player == nil || session == nil || player.session != session || player.journal != session.journal || sessio", ErrControl)
	}
	if config.Start.Policy == nil {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Start.Policy == nil", ErrControl)
	}
	if config.Rounds != nil && config.Rounds.player != player {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Rounds != nil && config.Rounds.player != player", ErrControl)
	}
	for _, planner := range []*RoundsBillPlanner{config.CookingBills, config.PreservationBills, config.ButcherBills, config.CookAheadBills, config.ArtBills, config.SurgeryPartBills, config.BabyFoodBills, config.MechBills} {
		if planner != nil && (config.Rounds == nil || planner.reviewer != config.Rounds) {
			return nil, fmt.Errorf("%w: NewClockScheduler: planner != nil && (config.Rounds == nil || planner.reviewer != config.Rounds)", ErrControl)
		}
	}
	if config.Butcher != nil && (config.Rounds == nil || config.Butcher.reviewer != config.Rounds || config.Butcher.concern != policy.MaintainButcherSpot) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Butcher != nil && (config.Rounds == nil || config.Butcher.reviewer != config.Rounds || config.Butc", ErrControl)
	}
	if config.Fields != nil && (config.Rounds == nil || config.Fields.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Fields != nil && (config.Rounds == nil || config.Fields.reviewer != config.Rounds)", ErrControl)
	}
	for _, planner := range []*RoundsAcquisitionPlanner{config.FoodAcquisition, config.PestAcquisition, config.ResourceAcquisition} {
		if planner != nil && (config.Rounds == nil || planner.reviewer != config.Rounds) {
			return nil, fmt.Errorf("%w: NewClockScheduler: planner != nil && (config.Rounds == nil || planner.reviewer != config.Rounds)", ErrControl)
		}
	}
	if config.Rules != nil && (config.Rounds == nil || config.Rules.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Rules != nil && (config.Rounds == nil || config.Rules.reviewer != config.Rounds)", ErrControl)
	}
	if config.Work != nil && (config.Rounds == nil || config.Work.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Work != nil && (config.Rounds == nil || config.Work.reviewer != config.Rounds)", ErrControl)
	}
	if config.Supplies != nil && (config.Rounds == nil || config.Supplies.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Supplies != nil && (config.Rounds == nil || config.Supplies.reviewer != config.Rounds)", ErrControl)
	}
	if config.Clearance != nil && (config.Rounds == nil || config.Clearance.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Clearance != nil && (config.Rounds == nil || config.Clearance.reviewer != config.Rounds)", ErrControl)
	}
	if config.Shrine != nil && (config.Rounds == nil || config.Shrine.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Shrine != nil && (config.Rounds == nil || config.Shrine.reviewer != config.Rounds)", ErrControl)
	}
	if config.Armory != nil && (config.Rounds == nil || config.Armory.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Armory != nil && (config.Rounds == nil || config.Armory.reviewer != config.Rounds)", ErrControl)
	}
	if config.Blight != nil && (config.Rounds == nil || config.Blight.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Blight != nil && (config.Rounds == nil || config.Blight.reviewer != config.Rounds)", ErrControl)
	}
	if config.Pollution != nil && (config.Rounds == nil || config.Pollution.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Pollution != nil && (config.Rounds == nil || config.Pollution.reviewer != config.Rounds)", ErrControl)
	}
	if config.MechCharger != nil && (config.Rounds == nil || config.MechCharger.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.MechCharger != nil && (config.Rounds == nil || config.MechCharger.reviewer != config.Rounds)", ErrControl)
	}
	if config.GeneBank != nil && (config.Rounds == nil || config.GeneBank.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.GeneBank != nil && (config.Rounds == nil || config.GeneBank.reviewer != config.Rounds)", ErrControl)
	}
	if config.Sleeping != nil && (config.Rounds == nil || config.Sleeping.reviewer != config.Rounds || config.Sleeping.concern != policy.MaintainHousing || config.Sleeping.phase != policy.HousingShelter) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Sleeping != nil && (config.Rounds == nil || config.Sleeping.reviewer != config.Rounds || config.Sl", ErrControl)
	}
	if config.Cooking != nil && (config.Rounds == nil || config.Cooking.reviewer != config.Rounds || config.Cooking.concern != policy.EnsureCooking) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Cooking != nil && (config.Rounds == nil || config.Cooking.reviewer != config.Rounds || config.Cook", ErrControl)
	}
	if config.Comfort != nil && (config.Rounds == nil || config.Comfort.reviewer != config.Rounds || config.Comfort.concern != policy.EnsureComfort || config.Comfort.phase != policy.ComfortRanked) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Comfort != nil && (config.Rounds == nil || config.Comfort.reviewer != config.Rounds || config.Comf", ErrControl)
	}
	if config.BasicComfort != nil && (config.Rounds == nil || config.BasicComfort.reviewer != config.Rounds || config.BasicComfort.concern != policy.EnsureComfort || config.BasicComfort.phase != policy.ComfortBasic) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.BasicComfort != nil && (config.Rounds == nil || config.BasicComfort.reviewer != config.Rounds || c", ErrControl)
	}
	if config.Workshop != nil && (config.Rounds == nil || config.Workshop.reviewer != config.Rounds || config.Workshop.concern != policy.MaintainResource) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Workshop != nil && (config.Rounds == nil || config.Workshop.reviewer != config.Rounds || config.Wo", ErrControl)
	}
	if config.Hospital != nil && (config.Rounds == nil || config.Hospital.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Hospital != nil && (config.Rounds == nil || config.Hospital.reviewer != config.Rounds)", ErrControl)
	}
	if config.SleepingUpkeep != nil && (config.Rounds == nil || config.SleepingUpkeep.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.SleepingUpkeep != nil && (config.Rounds == nil || config.SleepingUpkeep.reviewer != config.Rounds)", ErrControl)
	}
	if config.Expansion != nil && (config.Rounds == nil || config.Expansion.reviewer != config.Rounds || config.Expansion.concern != policy.MaintainHousing || config.Expansion.phase != policy.HousingExpansion) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Expansion != nil && (config.Rounds == nil || config.Expansion.reviewer != config.Rounds || config", ErrControl)
	}
	if config.Power != nil && (config.Rounds == nil || config.Power.reviewer != config.Rounds || config.Power.concern != policy.EnsureBasicPower) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Power != nil && (config.Rounds == nil || config.Power.reviewer != config.Rounds || config.Power.go", ErrControl)
	}
	if config.Temperature != nil && (config.Rounds == nil || config.Temperature.reviewer != config.Rounds || config.Temperature.concern != policy.EnsureTemperatureSafety) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Temperature != nil && (config.Rounds == nil || config.Temperature.reviewer != config.Rounds || con", ErrControl)
	}
	if config.Refrigeration != nil && (config.Rounds == nil || config.Refrigeration.reviewer != config.Rounds || config.Refrigeration.concern != policy.MaintainRefrigeration) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Refrigeration != nil && (config.Rounds == nil || config.Refrigeration.reviewer != config.Rounds ||", ErrControl)
	}
	if config.Lighting != nil && (config.Rounds == nil || config.Lighting.reviewer != config.Rounds || config.Lighting.concern != policy.MaintainLighting) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Lighting != nil && (config.Rounds == nil || config.Lighting.reviewer != config.Rounds || config.Li", ErrControl)
	}
	if config.Flooring != nil && (config.Rounds == nil || config.Flooring.reviewer != config.Rounds || config.Flooring.concern != policy.MaintainFlooring) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Flooring != nil && (config.Rounds == nil || config.Flooring.reviewer != config.Rounds || config.Fl", ErrControl)
	}
	if config.Routes != nil && (config.Rounds == nil || config.Routes.reviewer != config.Rounds || config.Routes.concern != policy.MaintainRoutes) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Routes != nil && (config.Rounds == nil || config.Routes.reviewer != config.Rounds || config.Routes", ErrControl)
	}
	if config.Defense != nil && (config.Rounds == nil || config.Defense.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Defense != nil && (config.Rounds == nil || config.Defense.reviewer != config.Rounds)", ErrControl)
	}
	if config.Tend != nil && (config.Rounds == nil || config.Tend.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Tend != nil && (config.Rounds == nil || config.Tend.reviewer != config.Rounds)", ErrControl)
	}
	if config.Rescue != nil && (config.Rounds == nil || config.Rescue.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Rescue != nil && (config.Rounds == nil || config.Rescue.reviewer != config.Rounds)", ErrControl)
	}
	if config.Equip != nil && (config.Rounds == nil || config.Equip.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Equip != nil && (config.Rounds == nil || config.Equip.reviewer != config.Rounds)", ErrControl)
	}
	if config.Repair != nil && (config.Rounds == nil || config.Repair.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Repair != nil && (config.Rounds == nil || config.Repair.reviewer != config.Rounds)", ErrControl)
	}
	if config.FireSafety != nil && (config.Rounds == nil || config.FireSafety.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.FireSafety != nil && (config.Rounds == nil || config.FireSafety.reviewer != config.Rounds)", ErrControl)
	}
	if config.Clean != nil && (config.Rounds == nil || config.Clean.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Clean != nil && (config.Rounds == nil || config.Clean.reviewer != config.Rounds)", ErrControl)
	}
	if config.Incineration != nil && (config.Rounds == nil || config.Incineration.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Incineration != nil && (config.Rounds == nil || config.Incineration.reviewer != config.Rounds)", ErrControl)
	}
	if config.Burial != nil && (config.Rounds == nil || config.Burial.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Burial != nil && (config.Rounds == nil || config.Burial.reviewer != config.Rounds)", ErrControl)
	}
	if config.MoodRelief != nil && (config.Rounds == nil || config.MoodRelief.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.MoodRelief != nil && (config.Rounds == nil || config.MoodRelief.reviewer != config.Rounds)", ErrControl)
	}
	if config.AnimalContainment != nil && (config.Rounds == nil || config.AnimalContainment.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.AnimalContainment != nil && (config.Rounds == nil || config.AnimalContainment.reviewer != config.Ro", ErrControl)
	}
	if config.Gear != nil && (config.Rounds == nil || config.Gear.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Gear != nil && (config.Rounds == nil || config.Gear.reviewer != config.Rounds)", ErrControl)
	}
	if config.Medical != nil && (config.Rounds == nil || config.Medical.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Medical != nil && (config.Rounds == nil || config.Medical.reviewer != config.Rounds)", ErrControl)
	}
	if config.Surgery != nil && (config.Rounds == nil || config.Surgery.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Surgery != nil && (config.Rounds == nil || config.Surgery.reviewer != config.Rounds)", ErrControl)
	}
	if config.FoodStorageUpkeep != nil && (config.Rounds == nil || config.FoodStorageUpkeep.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.FoodStorageUpkeep != nil && (config.Rounds == nil || config.FoodStorageUpkeep.reviewer != config.Ro", ErrControl)
	}
	if config.Recovery != nil && (config.Rounds == nil || config.Recovery.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Recovery != nil && (config.Rounds == nil || config.Recovery.reviewer != config.Rounds)", ErrControl)
	}
	if config.Husbandry != nil && (config.Rounds == nil || config.Husbandry.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Husbandry != nil && (config.Rounds == nil || config.Husbandry.reviewer != config.Rounds)", ErrControl)
	}
	if config.PrisonerInteraction != nil && (config.Rounds == nil || config.PrisonerInteraction.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.PrisonerInteraction != nil && (config.Rounds == nil || config.PrisonerInteraction.reviewer != confi", ErrControl)
	}
	if config.PopulationCustody != nil && (config.Rounds == nil || config.PopulationCustody.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.PopulationCustody != nil && (config.Rounds == nil || config.PopulationCustody.reviewer != config.Ro", ErrControl)
	}
	if config.PopulationJoiner != nil && (config.Rounds == nil || config.PopulationJoiner.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.PopulationJoiner != nil && (config.Rounds == nil || config.PopulationJoiner.reviewer != config.Rout", ErrControl)
	}
	if config.Research != nil && (config.Rounds == nil || config.Research.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Research != nil && (config.Rounds == nil || config.Research.reviewer != config.Rounds)", ErrControl)
	}
	if config.StorageShelves != nil && (config.Rounds == nil || config.StorageShelves.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.StorageShelves != nil && (config.Rounds == nil || config.StorageShelves.reviewer != config.Rounds)", ErrControl)
	}
	if config.Dialog != nil && (config.Rounds == nil || config.Dialog.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Dialog != nil && (config.Rounds == nil || config.Dialog.reviewer != config.Rounds)", ErrControl)
	}
	if config.Trade != nil && (config.Rounds == nil || config.Trade.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Trade != nil && (config.Rounds == nil || config.Trade.reviewer != config.Rounds)", ErrControl)
	}
	if config.Resource != nil && (config.Rounds == nil || config.Resource.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Resource != nil && (config.Rounds == nil || config.Resource.reviewer != config.Rounds)", ErrControl)
	}
	if config.MaintainShelter != nil && (config.Rounds == nil || config.MaintainShelter.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.MaintainShelter != nil && (config.Rounds == nil || config.MaintainShelter.reviewer != config.Rounds)", ErrControl)
	}
	if config.Firebreak != nil && (config.Rounds == nil || config.Firebreak.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Firebreak != nil && (config.Rounds == nil || config.Firebreak.reviewer != config.Rounds)", ErrControl)
	}
	if config.Psylink != nil && (config.Rounds == nil || config.Psylink.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Psylink != nil && (config.Rounds == nil || config.Psylink.reviewer != config.Rounds)", ErrControl)
	}
	if config.IdeoRoles != nil && (config.Rounds == nil || config.IdeoRoles.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.IdeoRoles != nil && (config.Rounds == nil || config.IdeoRoles.reviewer != config.Rounds)", ErrControl)
	}
	if config.Rituals != nil && (config.Rounds == nil || config.Rituals.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Rituals != nil && (config.Rounds == nil || config.Rituals.reviewer != config.Rounds)", ErrControl)
	}
	if config.Ideoligion != nil && (config.Rounds == nil || config.Ideoligion.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: mismatched ideoligion reviewer", ErrControl)
	}
	if config.Permits != nil && (config.Rounds == nil || config.Permits.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Permits != nil && (config.Rounds == nil || config.Permits.reviewer != config.Rounds)", ErrControl)
	}
	if config.CreepJoiners != nil && (config.Rounds == nil || config.CreepJoiners.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.CreepJoiners != nil && (config.Rounds == nil || config.CreepJoiners.reviewer != config.Rounds)", ErrControl)
	}
	if config.HomeCoverage != nil && (config.Rounds == nil || config.HomeCoverage.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.HomeCoverage != nil && (config.Rounds == nil || config.HomeCoverage.reviewer != config.Rounds)", ErrControl)
	}
	if config.StoneShell != nil && (config.Rounds == nil || config.StoneShell.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.StoneShell != nil && (config.Rounds == nil || config.StoneShell.reviewer != config.Rounds)", ErrControl)
	}
	if config.Stockpiles != nil && (config.Rounds == nil || config.Stockpiles.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.Stockpiles != nil && (config.Rounds == nil || config.Stockpiles.reviewer != config.Rounds)", ErrControl)
	}
	if config.DefenseLayout != nil && (config.Rounds == nil || config.DefenseLayout.reviewer != config.Rounds) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.DefenseLayout != nil && (config.Rounds == nil || config.DefenseLayout.reviewer != config.Rounds)", ErrControl)
	}
	if config.RoundsMethods && (config.Rounds == nil || !session.roundsMethods) {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.RoundsMethods && (config.Rounds == nil || !session.roundsMethods)", ErrControl)
	}
	config.Start.Policy = proto.Clone(config.Start.Policy).(*k.WatchPolicy)
	p := config.Start.Policy
	if p.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(p.AcknowledgedHostileIds)+len(p.AcknowledgedDownedColonistIds)+len(p.AcknowledgedInjuredColonistIds)+len(p.SurgicalRecoveryIds)+len(p.MedicalRestIds) != 0 || p.GetInjuryStopCooldownMs() != 0 {
		return nil, fmt.Errorf("%w: NewClockScheduler: p.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(p.AcknowledgedHostileIds)+len(p.AcknowledgedDownedColon", ErrControl)
	}
	if config.CombatMaxTicks > config.Start.MaxTicks {
		return nil, fmt.Errorf("%w: NewClockScheduler: config.CombatMaxTicks > config.Start.MaxTicks", ErrControl)
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
		return nil, fmt.Errorf("%w: NewClockScheduler: !profile.IsDir() || !ownedProfile.IsDir() || !os.SameFile(profile, ownedProfile)", ErrControl)
	}
	ctx, cancel := context.WithTimeout(player.lifetime, player.config.JournalTimeout)
	defer cancel()
	inbox, err := player.journal.BindClockInbox(ctx, config.Profile)
	if err != nil {
		return nil, err
	}
	config.Profile = inbox.Profile
	scheduler := &ClockScheduler{player: player, session: session, native: native, config: config, clock: clock, pollGate: make(chan struct{}, 1), renewGate: make(chan struct{}, 1), facts: newClockFacts(config.Store), queue: newPlannerQueue(), running: new(atomic.Bool), manualAt: new(atomic.Int64), validity: new(atomic.Pointer[domain.ReadValidity]), latched: newClockLatched(), late: &lateProposals{}, catalog: plannerCatalog}
	if config.Start.PlayerAccelerated || config.FollowPlayerSpeed {
		scheduler.paceEpoch = new(atomic.Pointer[k.Epoch])
		scheduler.pace = newPaceBackoff(config.PaceHorizonTicks, clock.Now, scheduler.requestCeiling)
	}
	scheduler.queue.catalog = func() []plannerEntry { return scheduler.catalog }
	scheduler.queue.configured = func(entry plannerEntry) bool { return entry.configured(&scheduler.config) }
	if config.Rounds != nil {
		config.Rounds.store = scheduler.facts.store
		config.Rounds.foodGapZero = config.Faults.FoodGapZero
	}
	return scheduler, nil
}

// WindowRunning reports whether the last evidence the scheduler saw had a
// window it admitted still running. It is a hint for the poll cadence, not
// authority: a stale true costs one held poll before the next step or page
// clears it.
func (s *ClockScheduler) WindowRunning() bool { return s.running.Load() }

// Validity is the read validity of the latest step whose scope was fixed
// (#624): the scope and tick its facts describe. The Worker carries
// it on each dispatch's context; false before any step fixed one.
func (s *ClockScheduler) Validity() (domain.ReadValidity, bool) {
	v := s.validity.Load()
	if v == nil {
		return domain.ReadValidity{}, false
	}
	return *v, true
}

// readValidity fixes the step's validity once its scope and tick
// are known (#624) and publishes it for the Worker.
func (s *ClockScheduler) readValidity(snapshot domain.GenerationSnapshot, tick int64) domain.ReadValidity {
	v := domain.ValidityOf(snapshot, domain.Tick(tick))
	s.validity.Store(&v)
	return v
}

// drift is the ticks the running window's pace covers in the step's wall:
// what a step expects the tick to have moved by since its predecessor.
func (s *ClockScheduler) drift() domain.Tick {
	return domain.Tick(s.livePaceTicks * s.config.MaxAge.Seconds())
}

// clockEvent logs one typed service event: an Info record that the
// telemetry handler mirrors into the flight recorder as a row of kind,
// stamped with the last observed tick and the trace ctx carries, with
// attrs as its payload.
func clockEvent(ctx context.Context, component, kind, message string, attrs ...any) {
	slog.Default().InfoContext(ctx, message, append([]any{telemetry.ComponentKey, component, telemetry.KindKey, kind}, attrs...)...)
}

// clockAuthorityLost logs that the controller dropped its own authority
// and why, as a WARN authority row: every loss costs the auto-resumer a
// re-acquire with a growing backoff.
func clockAuthorityLost(ctx context.Context, why string, attrs ...any) {
	slog.Default().Log(ctx, slog.LevelWarn, "authority lost: "+why,
		append([]any{telemetry.ComponentKey, "clock-scheduler", telemetry.KindKey, "authority", "change", "lost", "reason", why}, attrs...)...)
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

// windowRefusedDecision is the admission row of a clock window the step did
// not admit: target "window", reason the first refusal (critical_wave_budget
// when a critical planner was still evaluating past the wall budget, else a
// ClockWindowReason), every refusal listed in attrs.refused beside attrs.
func windowRefusedDecision(refused []string, attrs map[string]any) telemetry.Decision {
	reason := "unspecified"
	if len(refused) > 0 {
		reason = refused[0]
	}
	attrs["refused"] = refused
	return telemetry.Decision{Kind: "admission", Component: "clock-scheduler", Verdict: "refused", Reason: reason, Target: "window", Attrs: attrs}
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
	var gate gateWait
	// The step is a trace root (or runs under the caller's): every flight
	// row it leaves, native or kinded, carries its trace_id (#298).
	ctx, trace := telemetry.EnsureTrace(ctx)
	s.trace.Store(trace)
	// The poll records latched outcomes as it commits them; the reason
	// repeats them for a step driven without the poll loop.
	s.latched.remember(reason.Events)
	call, epoch, done, gate, err := s.player.enterTimed(ctx, "clock_step", false)
	if err != nil {
		return out, err
	}
	defer done()
	gateWait := time.Since(entered)
	// A wake's evidence lands on the due queue once, here, so it outlives
	// a step that runs no planners (a paced live wave, a stopping window)
	// and selects the same planners on the next (#625).
	if reason.Cause == StepWake {
		s.queue.wake(reason, s.facts.kindOf)
	}
	// The round trips that still cross the bridge (frame misses and
	// writes) are tallied by tool so the cost of the composition is
	// visible per step: as a debug record and as a clock_step row in the
	// flight recorder, which `rimgovernor phases` reports as reads/step.
	call, reads := bridge.WithReadTally(call)
	// The planning window's refresher (#356): its step scope, tick and
	// whether this step reviews are fixed once the bundle below is read,
	// before any planning read asks it.
	reviews := s.stepReviews(reason)
	zones := &zoneRefresher{native: s.native, store: s.facts.store}
	call = observation.WithZones(call, zones)
	window := &planningWindow{native: s.native, store: s.facts.store}
	call = observation.WithPlanningWindow(call, window)
	stepBegan := time.Now()
	journal := &journalTimer{}
	defer func() {
		elapsed := time.Since(stepBegan)
		out.Journal = journal.total
		// The reason the step acted on (out.Reason once the status read
		// fixed it, else the caller's) and, for a step woken by a clock
		// stop, the latency from the native stop stamp to the step
		// (issue #112); `rimgovernor phases` reports both.
		cause := out.Reason.Cause
		if cause == "" {
			cause = reason.Cause
		}
		extra := map[string]any{"running": out.Running}
		// The wait for the player gate before the step began: the
		// Worker's dispatch step, or manual control, holding it (#593).
		if gateWait > 0 {
			extra["gate_wait_ms"] = float64(gateWait) / float64(time.Millisecond)
			extra["gate_holder"] = gate.holder
			extra["gate_holder_held_ms"] = float64(gate.holderHeld) / float64(time.Millisecond)
		}
		extra["journal_ms"] = float64(journal.total) / float64(time.Millisecond)
		if len(out.Waiting) > 0 {
			extra["waiting"] = out.Waiting
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
		if len(out.PlannerMS) > 0 {
			extra["planner_ms"] = out.PlannerMS
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
		reads.Publish(call, stepDecision(out, err, cause, elapsed, extra))
	}()
	attempts, err := journalTimed(journal, func() ([]store.ClockAttempt, error) { return s.player.journal.LoadClockAttempts(call, 4096) })
	if err != nil {
		return out, err
	}
	// The step's opening read: the current scope, the owned clock status
	// and the emergency census (issue #127).
	started := s.clock.Now()
	loaded, _, err := s.native.ReadStep(call, s.stepRead(reason))
	if err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	if loaded == nil {
		return out, errors.Join(executor.ErrHeld, s.session.Disable())
	}
	if err = bridge.ValidateContext(loaded.Context); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	epochs, err := journalTimed(journal, func() ([]store.ClockEpochObligation, error) { return s.player.journal.LoadClockEpochs(call, 4096) })
	if err != nil {
		return out, err
	}
	// The player runs the game by hand under a stopped clock (#601): every
	// fact read while it runs is stale before the review commits, and no
	// window can be admitted against a running game. Re-take the clock
	// here, before the facts below are filled, once the player has let go
	// of the speed keys: pause natively and read the bundle again at the
	// paused tick, so the same step reviews and admits from it.
	if s.playerDriven(loaded, epochs) {
		if _, quiet := s.playerQuiet(); !quiet {
			out.Deferred = true
			return out, nil
		}
		status, e := s.bundleClockStatus(loaded, s.session.State().Snapshot)
		if e != nil {
			clockAuthorityLost(call, "bundle clock status failed while re-taking the clock", "err", e)
			return out, errors.Join(e, s.session.Disable())
		}
		s.livePace(status, started)
		repaused, e := s.session.RepauseClock(call, status)
		if e != nil {
			return out, e
		}
		clockEvent(call, "clock-scheduler", "authority", "clock re-taken from the player", "change", "retaken", "tick", repaused.Context.GetTick(), "paused", repaused.GetActualPaused(), "pace", s.pacePerSecond, "stop_reason", status.GetStopped().GetReason().String())
		out.Retaken = true
		// The re-read below is judged against the paused tick: the families
		// the store holds fresh at the previous step's tick are not fresh
		// at this one, and a step that re-took the clock reviews in full.
		s.lastTick, s.lastTickKnown = repaused.Context.GetTick(), true
		reason.TickAdvanced = true
		reviews = s.stepReviews(reason)
		started = s.clock.Now()
		if loaded, _, err = s.native.ReadStep(call, s.stepRead(reason)); err != nil {
			return out, errors.Join(err, s.session.Disable())
		}
		if loaded == nil {
			return out, errors.Join(executor.ErrHeld, s.session.Disable())
		}
		if err = bridge.ValidateContext(loaded.Context); err != nil {
			return out, errors.Join(err, s.session.Disable())
		}
	}
	if s.config.WorldReady != nil {
		reset, e := s.config.WorldReady(call, loaded.Context)
		if e != nil {
			return out, fmt.Errorf("world rebuild: %w", e)
		}
		if reset && s.config.Rounds != nil {
			reason.Cause = StepFull
			s.replanAfterFailure()
			reviews = s.stepReviews(reason)
		}
	}
	window.scope = factsScope(loaded.Context)
	id, tick := loaded.Context.GetIdentity(), domain.Tick(loaded.Context.GetTick())
	window.layout = func(ctx context.Context) (store.LayoutPlanRecord, bool, error) {
		// The plan is keyed by colony and map; a session that holds no
		// plan identity for this world has no layout to cover.
		world := s.session.State().Snapshot
		if world.Colony != domain.ColonyID(id.GetColonyId()) || world.Map != domain.MapID(id.GetMapId()) || world.Validate() != nil {
			return store.LayoutPlanRecord{}, false, nil
		}
		return s.player.journal.LayoutPlan(ctx, world, tick)
	}
	zones.scope, zones.tick, zones.review = factsScope(loaded.Context), loaded.Context.GetTick(), reviews
	// Every full review step refreshes the entity sections and the zone
	// census, each read whole.
	if reviews {
		refreshEntitySections(call, s.native, s.facts, loaded.Context.Identity, factsScope(loaded.Context))
		// A failed zone refresh leaves the census held; planners read it
		// as stale rather than the step failing.
		_, _ = zones.Zones(call, loaded.Context.Identity)
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
				clockAuthorityLost(call, "start dispatched under another world or without authority; cleaning up", "request", v.Intent.RequestID, "enabled", state.Enabled)
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
		clockAuthorityLost(call, "owed epoch no longer matches the held authority; cleaning up",
			"enabled", state.Enabled, "known", state.ObservationKnown, "held", fmt.Sprintf("%+v", state.Snapshot),
			"observed_world", fmt.Sprintf("%v", world), "observed_generation", loaded.Context.GetNativeGeneration())
		out.Cleaned = true
		return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
	}
	if !state.ObservationKnown || !boundary.World(state.Snapshot, world) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native) {
		clockAuthorityLost(call, "observed scope does not match the held authority",
			"known", state.ObservationKnown, "held", fmt.Sprintf("%+v", state.Snapshot),
			"observed_world", fmt.Sprintf("%v", world), "observed_generation", loaded.Context.GetNativeGeneration())
		return out, errors.Join(executor.ErrAuthority, s.session.Disable())
	}
	status, err := s.bundleClockStatus(loaded, state.Snapshot)
	if err != nil {
		clockAuthorityLost(call, "bundle clock status failed", "err", err)
		return out, errors.Join(err, s.session.Disable())
	}
	if status.Context.GetTick() < loaded.Context.GetTick() {
		clockAuthorityLost(call, "clock status is behind the scope tick", "status_tick", status.Context.GetTick(), "scope_tick", loaded.Context.GetTick())
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
		// The window runs whatever planners the due queue selected; a
		// running window never waits for the stop to plan.
		if status.GetStopping() != nil || s.config.Rounds == nil || !sel.planners {
			out.Waiting = sel.waiting
			return out, s.pauseForHunt(call, state.Snapshot, status, &out)
		}
		// The window runs on; plan against the bundle's snapshot (one
		// main-thread hop, so its sections describe one tick) and let the
		// Worker dispatch live. Planners admit their methods as at a stop; only
		// the clock window itself is left to the stop that ends it.
		if err = s.player.current(call, epoch); err != nil {
			return out, err
		}
		reason.Cause = StepLive
		out.Reason = reason
		out.Waiting = sel.waiting
		if out.Planners, err = s.runPlanners(call, epoch, &out, sel, status); err != nil {
			s.replanAfterFailure()
			return out, err
		}
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if sel.pick == nil {
			s.lastFull = s.clock.Now()
		}
		return out, s.pauseForHunt(call, state.Snapshot, status, &out)
	}
	if obligations {
		// The window this step still owes is already stopped: settle it
		// from the status just read and, once nothing is owed, review in
		// this same step rather than leave the game paused for another
		// bundle read and a second pass (issue #162).
		readmit = true
		if err = s.session.CleanupClockObserved(call, status); err != nil {
			out.Cleaned = true
			return out, err
		}
		if epochs, err = journalTimed(journal, func() ([]store.ClockEpochObligation, error) { return s.player.journal.LoadClockEpochs(call, 4096) }); err != nil {
			out.Cleaned = true
			return out, err
		}
		for _, owned := range epochs {
			if !clockCoordinatorTerminal(owned.Stage) {
				out.Cleaned = true
				return out, nil
			}
		}
	}
	if !state.Enabled {
		return out, executor.ErrAuthority
	}
	if err = s.player.current(call, epoch); err != nil {
		return out, err
	}
	if status.GetStopped() != nil && s.combatStops.active {
		s.queue.combatStopped()
	}
	sel, err := s.selectPlanners(call, reason, status.Context.GetTick())
	if err != nil {
		return out, err
	}
	out.Waiting = sel.waiting
	if sel.planners {
		if out.Planners, err = s.runPlanners(call, epoch, &out, sel, status); err != nil {
			s.replanAfterFailure()
			return out, err
		}
		if len(out.HeldBy) > 0 {
			// A critical planner is still evaluating past the wall budget:
			// the window decision would read a verdict it does not have.
			// Hold, naming the planners; the next step evaluates again.
			telemetry.Decide(call, windowRefusedDecision([]string{"critical_wave_budget"}, map[string]any{"held_by": out.HeldBy, "wall_budget_ms": float64(s.config.Budget.wall()) / float64(time.Millisecond)}))
			return out, executor.ErrHeld
		}
		s.plannedTick, s.plannedTickKnown = status.Context.GetTick(), true
		if sel.pick == nil {
			s.lastFull = s.clock.Now()
		}
		// The planners ran between the step read and admission; MaxAge
		// bounds the admission reads alone, so read the status and the
		// emergency census again here.
		if out.Rounds != nil || len(out.Planners) > 0 {
			started = s.clock.Now()
			if loaded, err = s.readStep(call, loaded.Context.Identity, state.Snapshot); err != nil {
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
	facts.Put(s.facts.store, factsScope(loaded.Context), facts.Emergency, facts.Held[policy.EmergencyFacts]{Value: emergency.Facts, AsOf: emergency.Context.GetTick(), Complete: colonistsKnown && colonistsComplete, Source: "rimgovernor/observations_read_status"})
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
		plans, err := journalTimed(journal, func() ([]store.PlanState, error) { return s.player.journal.LoadPlans(call) })
		if err != nil {
			return out, err
		}
		remaining, items, err := s.roundsWork(call, state.Snapshot, plans)
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
		out.Deferred = true
		return out, nil
	}
	if waiting := s.latched.undispatched(fingerprint); s.config.Worker && len(waiting) > 0 {
		// Likewise while the successor is queued but not yet dispatched.
		out.Deferred = true
		return out, nil
	}
	combatPlan, fightOpen, huntPrey, err := clockSchedulerCombatPlan(call, s.player.journal, state.Snapshot)
	if err != nil {
		return out, err
	}
	// An open fight (#852) is work of its own once its drafts complete:
	// its next decision waits on the next armed stop, which needs ticks
	// (#886).
	work = work || fightOpen
	// The defense planner's verdict at this stop: only a reported
	// no-squad answer lets a hostile building be watched instead of held
	// (#326); any other outcome, or no planner, keeps the hold.
	squadUnanswered := domain.Unknown[bool]()
	if out.Defense != nil && !out.Defense.Verdict.IsZero() {
		squadUnanswered = domain.Known(out.Defense.Verdict == BuildingReasonNoSquad)
	}
	// A complete sheltering response lets the threat be waited out (#1560):
	// the wait is work of its own, on game time. It reads the review's
	// facts, so it holds whether or not the recovery planner ran this step.
	sheltered := clockShelterHeld(out.Rounds)
	if held, _ := sheltered.Value(); held {
		work = true
	}
	clockState := policy.ClockWindowState("")
	start := s.config.Start
	if s.config.FollowPlayerSpeed {
		start = followPlayerSpeed(start, status)
	}
	// A routine window runs the whole budget (#244); a native-work or
	// combat bound may narrow it below.
	paused = clockStopSpan(status, s.clock.Now())
	out.Window = ClockWindowSize{Ticks: start.MaxTicks}
	var nativeWorkTicks uint32
	if out.Shrine != nil {
		nativeWorkTicks = out.Shrine.NativeWorkTicks
	}
	if out.Fields != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Fields.NativeWorkTicks)
	}
	// Designated chunks are carried by ordinary hauling (#702, #764).
	if out.Clearance != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Clearance.NativeWorkTicks)
	}
	for _, result := range []*RoundsBuildingResult{out.Sleeping, out.Cooking, out.Butcher, out.Comfort, out.BasicComfort, out.Workshop, out.Hospital, out.SleepingUpkeep, out.Expansion, out.Power, out.Temperature, out.Refrigeration, out.Lighting, out.Flooring, out.Routes, out.MechCharger, out.GeneBank} {
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
	if out.ArtBills != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.ArtBills.NativeWorkTicks)
	}
	if out.PreservationBills != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.PreservationBills.NativeWorkTicks)
	}
	// A standing production bill past its first iteration needs game time,
	// not another method (RoundsResourceResult.NativeWorkTicks).
	if out.Resource != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Resource.NativeWorkTicks)
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
	// A queued surgery bill is native doctor work (#1238).
	if out.Surgery != nil {
		nativeWorkTicks = max(nativeWorkTicks, out.Surgery.NativeWorkTicks)
	}
	// A planner that failed on a native refusal produced neither work nor a
	// wait, and the same read is refused again next step while the game
	// stands still; one window lets the world move under it (#219).
	if wait := plannerRefusalWait(out.PlannerFailures); wait > 0 {
		nativeWorkTicks = max(nativeWorkTicks, wait)
	}
	out.NativeWorkTicks = nativeWorkTicks
	if !work && s.config.RoundsMethods && nativeWorkTicks > 0 {
		work = true
		start.MaxTicks = min(start.MaxTicks, nativeWorkTicks, s.nativeWorkBudget())
	}
	// A colony that is waiting on the world (a shell being raised, a dig in
	// progress, a development slot another goal holds) offers no method and
	// no native-work hint, and a stopped clock never moves the tick that
	// would change that: the step refused no_work forever (live, tick 60023).
	// After a few such steps one short window lends game time; every other
	// refusal (hostiles, interruption, stale facts) still applies to it.
	if !work && s.config.RoundsMethods && s.idleRefusals >= idleLendAfter && len(out.HeldBy) == 0 {
		s.warnIdleStall(call, loaded.Context.GetTick())
		work = true
		start.MaxTicks = min(start.MaxTicks, idleLendTicks, s.nativeWorkBudget())
		out.NativeWorkTicks = max(out.NativeWorkTicks, start.MaxTicks)
	}
	// An admitted trade phase keeps the window short: the session's next
	// phase lands in the stop after it, before the caravan leaves (#1195).
	if out.Trade != nil && out.Trade.Plan != "" && out.Trade.NativeWorkTicks > 0 {
		start.MaxTicks = min(start.MaxTicks, out.Trade.NativeWorkTicks)
	}
	if status.GetNeverStarted() != nil {
		clockState = policy.ClockNeverStarted
	}
	if status.GetStopped() != nil {
		clockState = policy.ClockStopped
	}
	facts := policy.ClockWindowFacts{Current: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), StartedAt: started, ObservedAt: s.clock.Now(), Emergency: emergencyFacts, Review: policy.ClockWindowReview{Revision: review.Revision, Captured: review.InboxCursor, Reviewed: review.ReviewedCursor, Acknowledged: review.AcknowledgedCursor, HasHolds: domain.Known(len(review.Holds) > 0)}, Status: policy.ClockWindowStatus{Snapshot: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), State: clockState, ActualPaused: boundary.FactBool(status.ActualPaused), NativeTickBoundary: boundary.FactBool(status.NativeTickBoundary), DurableEvents: boundary.FactBool(status.DurableEvents)}, Obligations: policy.ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(work), CombatPlan: domain.Known(combatPlan), HuntPrey: huntPrey, SquadUnanswered: squadUnanswered, Sheltered: sheltered}
	if status.NewestCursor != nil {
		facts.Status.NewestCursor = domain.Known(status.GetNewestCursor())
	}
	combatMaxTicks := min(s.config.CombatMaxTicks, start.MaxTicks)
	out.Decision = policy.EvaluateClockWindow(facts, policy.ClockWindowLimits{Now: s.clock.Now(), MaxAge: s.config.MaxAge, MaxTicks: start.MaxTicks, CombatMaxTicks: combatMaxTicks})
	s.noWork = !out.Decision.Admitted && slices.Contains(out.Decision.Refused, policy.ClockWindowNoWork)
	if s.noWork {
		s.idleRefusals++
	} else {
		s.idleRefusals = 0
	}
	if !out.Decision.Admitted {
		telemetry.Decide(call, windowRefusedDecision(clockReasonNames(out.Decision.Refused), map[string]any{"mode": string(out.Decision.Mode), "work": work, "combat_plan": combatPlan, "hostiles": len(out.Decision.Hostiles), "clock_state": string(clockState), "window_ticks": out.Window.Ticks}))
		return out, executor.ErrHeld
	}
	start.Policy = proto.Clone(start.Policy).(*k.WatchPolicy)
	// No window watches attempts: a completed order is not a reason to
	// stop the clock, the event poll carries its outcome to the Worker
	// under the running window (#243, #244, #856).
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
		start.Policy.AcknowledgedHostileIds = make([]string, 0, len(out.Decision.Hostiles))
		for _, id := range out.Decision.Hostiles {
			start.Policy.AcknowledgedHostileIds = append(start.Policy.AcknowledgedHostileIds, string(id))
		}
		start.MaxTicks = out.Decision.MaxTicks
		// Native stops the window on the tick an armed event happens
		// (#849); the combat budget is the backstop when none does. The
		// list is armed only while an admitted fight plan owns the combat
		// (#852): its stop is then that plan's next decision and is benign
		// (clock.BenignStop). A window with no owning fight is disarmed, so
		// a combat stop never lands without a plan to answer it.
		start.Policy.CombatStopEvents = armedCombatStops(fightOpen)
	}
	s.combatStops.admitted(call, status.GetStopped(), status.GetContext().GetTick(), s.clock.Now(), out.Combat)
	admission := &store.ClockWindowAdmission{Profile: s.config.Profile, Snapshot: state.Snapshot, Tick: facts.Tick, ReviewRevision: review.Revision, CapturedCursor: review.InboxCursor, MaxTicks: start.MaxTicks}
	key, err := clockSchedulerKey(admission, fingerprint, start)
	if err != nil {
		return out, err
	}
	if s.pace != nil && start.PlayerAccelerated {
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
	if s.config.Autosave != nil && status.GetActualPaused() {
		s.config.Autosave(call, loaded.Context.GetIdentity(), status.Context.GetTick())
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

// seedLiveDrift sets the pace for the window this step just started from
// the last pace livePace measured in this process, and restarts the pace
// measurement at the start tick: the step's own status read predates the
// start, and a pace measured from it would count the stop as running time.
// The first window of a process, with no pace measured yet, starts at zero.
func (s *ClockScheduler) seedLiveDrift(startTick int64) {
	s.paceTick, s.paceAt, s.paceKnown = startTick, s.clock.Now(), true
	if s.pacePerSecond <= 0 {
		return
	}
	s.livePaceTicks = s.pacePerSecond
}

const (
	// idleLendAfter is the consecutive no_work refusals before an idle
	// colony is lent a window; idleLendTicks is its length, about an hour.
	idleLendAfter = 3
	idleLendTicks = 2500
)

// warnIdleStall is the loud half of the idle lend: reaching it is a defect
// (a planner waits on game time without saying how many ticks, so the
// stopped clock deadlocks), not routine. It logs at Warn, mirrored to the
// flight recorder as an idle_stall row, naming every goal's standing
// planner refusal or wait: those are the planners to teach a
// NativeWorkTicks hint.
func (s *ClockScheduler) warnIdleStall(ctx context.Context, tick int64) {
	standing := make([]string, 0, len(s.plannerReasons.last))
	for goal, note := range s.plannerReasons.last {
		if note.Text == "" || note.Text == policy.PlannerOptOut {
			continue
		}
		kind := "refused"
		if note.Waiting {
			kind = "waiting"
		}
		standing = append(standing, fmt.Sprintf("%s %s: %s", goal, kind, note.Text))
	}
	slices.Sort(standing)
	// The colony refused no_work and nothing lent game time: a planner waits
	// on ticks without saying so, so a window is lent as a fallback.
	telemetry.Decide(ctx, telemetry.Decision{Kind: "idle_stall", Component: "clock-scheduler", Level: slog.LevelWarn, Verdict: "waiting", Reason: "no_work", Target: "colony",
		Attrs: map[string]any{"tick": tick, "refusals": s.idleRefusals, "lend_ticks": idleLendTicks, "standing": standing}})
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

// markStarved records a wave's outcome on the starved marks: a planner that
// returned loses its mark and one that missed the cutoff gains it. A mark
// lasts until its planner returns, so a wave that does not run it leaves
// it alone; wiping it there left a planner due less often than steps run
// cancelled at the grace every time it was due (#717). A planner that
// missed the cutoff stays due, so the next step reruns it with the whole
// wall budget: its discarded result otherwise left a clock that never
// started on no_work with nothing to rerun it (hunt cases, CI).
func (s *ClockScheduler) markStarved(finished, missed []string) {
	if s.starved == nil {
		s.starved = map[string]bool{}
	}
	for _, name := range finished {
		delete(s.starved, name)
	}
	for _, name := range missed {
		s.starved[name] = true
		s.queue.mark(name)
	}
}

// runPlanners runs the rounder and the selected planner wave as an
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
	// Under player acceleration the critical wave is the evidence the
	// backoff keeps inside the horizon, aged from the step's status read.
	watched := func(time.Duration, bool) {}
	readAt := began
	if s.paceKnown {
		readAt = s.paceAt
	}
	if s.pace != nil && status.GetRunning().GetEpoch().GetPacing() == k.Pacing_PACING_PLAYER_ACCELERATED {
		s.paceEpoch.Store(proto.Clone(status.GetRunning().GetEpoch()).(*k.Epoch))
		watched = s.pace.Watch(call, readAt)
	}
	planners, err := s.stepPlanners(call, epoch, out, wave, arbiter, sel.pick)
	if err != nil {
		watched(0, false)
		return nil, err
	}
	// The wall budget bounds the planners, not the rounds that
	// stepPlanners ran first: a cold review and layout take seconds on a
	// slow runner and used to leave the critical planners the rest of the
	// budget, so a startup planner was cut at every step.
	began = time.Now()
	wall := after(s.config.Budget.wall())
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
	}
	s.markStarved(wave.finishedNames(), out.MissedCutoff)
	out.PlannerMS = wave.plannerMS()
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
		wave.decided(outcome.Planner, outcome.Verdict)
	}
	wave.merge(out)
	// A failed planner is reported, not fatal: the step still evaluates the
	// clock window on what the other planners committed, and the failed
	// planner retries next step (#62).
	out.PlannerFailures = append(wave.group.Failures(), commitFailures...)
	// A planner's own failure filed its planner_step row when it returned; a
	// proposal that failed to commit has none, so it files one here.
	for _, failure := range commitFailures {
		plannerBookkeepingFailed(call, "proposals", failure)
	}
	return planners, nil
}

// recordWave files a wave on the due queue (plannerQueue.ran, #625):
// the planners that returned before the cutoff are due again at their
// cadence, and one that reported existing work of its kinds waits on the
// open attempts of those kinds. The plans are read once, only when some
// planner reported existing work.
func (s *ClockScheduler) recordWave(call context.Context, sel plannerSelectionResult, wave *plannerWave, tick int64) {
	s.recordPlannerReasons(call, wave)
	var plans []store.PlanState
	s.queue.ran(sel, wave.finishedNames(), wave.reason, tick, func(kinds []domain.ActionKind) []domain.ActionID {
		if plans == nil {
			loaded, err := s.player.journal.LoadPlans(call)
			if err != nil {
				plannerBookkeepingFailed(call, "waits", err)
				return nil
			}
			plans = loaded
		}
		return openWorkOfKinds(plans, kinds)
	})
}

// stepBudget is the coordinator's quantity budget for this step (#628): the
// stock the rounds's resource runways observed. Nothing is read when no proposal claims a
// quantity, and a step without a review carries no stock, so every
// quantity is unbounded here and checked beneath the commit.
func (s *ClockScheduler) stepBudget(call context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (stepBudget, error) {
	if out.Rounds == nil || !arbiter.claimsQuantities() {
		return stepBudget{}, nil
	}
	review := out.Rounds.Review
	stock := map[policy.Resource]int64{}
	for _, runway := range review.ResourceRunways {
		if runway.Stock != nil && runway.Tick == review.Tick {
			stock[runway.Resource] = *runway.Stock
		}
	}
	if len(stock) == 0 {
		return stepBudget{}, nil
	}
	return stepBudget{Stock: stock}, nil
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
	s.paceTick, s.paceAt, s.paceKnown = tick, readAt, true
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
		plans, err := s.player.journal.LoadPlans(call)
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

// stepRead is the step's opening read: the clock status and the emergency
// census always. The review's census families, entity sections and
// planning window come from the snapshot stream as the planners read them
// (#858).
func (s *ClockScheduler) stepRead(reason StepReason) bridge.StepRequest {
	return bridge.StepRequest{ClockStatus: true, Emergency: true}
}

// stepReviews is whether a step taken for reason is expected to run the
// rounds: never without a reviewer, on a timer step only when the
// full-step safety net is due, and for any other cause as plannerSelection
// decides.
func (s *ClockScheduler) stepReviews(reason StepReason) bool {
	if s.config.Rounds == nil {
		return false
	}
	return s.previewSelection(reason).planners
}

// factsScope is the store scope an observation context establishes: the
// load token, map and native generation.
func factsScope(context *c.ObservationContext) facts.Scope {
	return facts.Scope{Load: context.GetIdentity().GetLoadToken(), Map: context.GetIdentity().GetMapId(), Generation: context.GetNativeGeneration()}
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

// readStep re-reads the step's clock status and emergency census under
// the identity the step observed, validated against the enabled snapshot.
func (s *ClockScheduler) readStep(call context.Context, identity *c.Identity, snapshot domain.GenerationSnapshot) (*o.BundleSnapshot, error) {
	loaded, _, err := s.native.ReadStep(call, bridge.StepRequest{Identity: proto.Clone(identity).(*c.Identity), ClockStatus: true, Emergency: true})
	if err != nil {
		return nil, err
	}
	if loaded == nil {
		return nil, executor.ErrHeld
	}
	if _, err = boundary.Context(loaded.Context, snapshot); err != nil {
		return nil, err
	}
	return loaded, nil
}

// bundleClockStatus validates a step read's clock status section under the
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
// replanAfterFailure makes the next timer step a full one after a planner
// wave failed with its selection spent (a native generation moved under
// the review after an authority resume): at a stopped clock no tick
// re-queues the planners, and the review stays stale until the safety net
// (#869).
func (s *ClockScheduler) replanAfterFailure() { s.lastFull = time.Time{} }

func (s *ClockScheduler) fullStepDue() bool {
	every := s.config.FullStepEvery
	if every <= 0 {
		every = DefaultFullStepEvery
	}
	return s.lastFull.IsZero() || s.clock.Now().Sub(s.lastFull) >= every
}

// stepPlanners runs the rounder synchronously first (every other
// planner's dispatch depends on being able to load the review it commits),
// then queues the configured catalog planners pick selects (nil: all) onto
// the wave sharing arbiter, returning the queued names without waiting.
// Routine's own error aborts before anything is queued; an error from a
// queued planner is isolated by the wave and surfaces later, from its
// failures, without stopping the step. The colony stage's
// Foothold hold also drops the comfort-class planners and promotes the
// startup planners into the critical cycle for the step (#658).
func (s *ClockScheduler) stepPlanners(call, epoch context.Context, out *ClockSchedulerResult, wave *plannerWave, arbiter *stepArbiter, pick func(plannerEntry) bool) ([]string, error) {
	startup := false
	if s.config.Rounds != nil {
		review, err := s.config.Rounds.step(call, epoch, arbiter, pick != nil)
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
		out.Rounds = &review
		if err := s.establishExtent(call, review.Review.Tick); err != nil {
			return nil, fmt.Errorf("colony extent: %w", err)
		}
		// A mental break is not a hold: it only ends with ticks, so refusing
		// every window while one is observed stopped the clock for good in
		// autonomous play. The break stays visible through the pawn's mood
		// goal and the native hazard supervisor keeps its authority.
		if stage := review.Review.Stage; stage != nil && stage.HoldsDevelopment() {
			// Foothold (#630): the shelter's planner is critical for this
			// step (#658), its siting reads outlasting the optional grace.
			// Every other planner still runs.
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

// runningWork reads the current plan under a running epoch and reports two
// things about it. The count is the dispatched building attempts; the
// window runs on and the event poll carries their outcome (#243), so the
// count is step evidence only. The
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
	live := 0
	for _, item := range items {
		if clockWatchedKind(item.Kind) && item.Attempt != 0 && (item.Stage == domain.Dispatched || item.Stage == domain.AwaitingObservation) {
			live++
		}
	}
	return live, coupled, nil
}

// clockWatchedKind reports whether the native clock keeps an operation
// record for kind that a watch can observe (NativeClockWatch.cs).
func clockWatchedKind(kind domain.ActionKind) bool {
	return kind == domain.BuildingAction
}

// roundsWork is clockSchedulerWork over the authorized routine plans of
// the catalog (every plan but the root's own).
// clockShelterHeld is policy.ShelterHeld over the review's facts: unknown
// without an enabled review.
func clockShelterHeld(review *store.RoundsResult) domain.Fact[bool] {
	if review == nil || review.Detection == nil {
		return domain.Unknown[bool]()
	}
	return domain.Known(policy.ShelterHeld(review.Detection.Facts))
}

func (s *ClockScheduler) roundsWork(call context.Context, root domain.GenerationSnapshot, plans []store.PlanState) (bool, []clockWorkItem, error) {
	work := false
	var fingerprint []clockWorkItem
	for _, method := range plans {
		if method.Spec.ID() == root.Plan {
			continue
		}
		target := root
		target.Plan, target.Revision = method.Spec.ID(), method.Spec.Revision()
		if err := (planAuthorizer{s.player.journal, s.config.RoundsMethods}).AuthorizeRoundsPlan(call, root, target); err != nil {
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

func clockSchedulerWork(plan store.PlanState, current domain.GenerationSnapshot) (bool, []clockWorkItem, error) {
	if plan.Spec.ID() != current.Plan || plan.Spec.Revision() != current.Revision || len(plan.Progress) != len(plan.Spec.Actions()) {
		return false, nil, executor.ErrEvidence
	}
	work := false
	items := make([]clockWorkItem, 0, len(plan.Progress))
	for _, p := range plan.Progress {
		v := p.View()
		items = append(items, clockWorkItem{v.Action, p.Action().Kind(), v.Stage, v.Attempt, v.Unresolved})
		// An applied building is pawn work until census-aware retirement
		// ends its plan (#856): its blueprint or frame needs ticks.
		if v.Stage == domain.Completed && p.Action().Kind() == domain.BuildingAction && !plan.Retired {
			work = true
			continue
		}
		if v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful || !v.Unresolved && v.Stage == domain.Completed {
			continue
		}
		// Allow, work settings, zones, a building's temperature target, a
		// bed's medical flag, a bed's owner, a grower's crop, a claim, an
		// auto-refuel switch, a surgery bill and a
		// foundation removal's designation are immediate
		// designations and need no simulation window.
		if p.Action().Kind() == domain.SupplyAllowAction || p.Action().Kind() == domain.SupplyForbidAction || p.Action().Kind() == domain.WorkAssignmentAction || p.Action().Kind() == domain.ZoneCreateAction || p.Action().Kind() == domain.BuildingTemperatureAction || p.Action().Kind() == domain.BedUseAction || p.Action().Kind() == domain.AssignAction || p.Action().Kind() == domain.GrowerCropAction || p.Action().Kind() == domain.ClaimBuildingAction || p.Action().Kind() == domain.AutoRefuelAction || p.Action().Kind() == domain.SurgeryAction || p.Action().Kind() == domain.AutoHomeAreaAction || p.Action().Kind() == domain.PawnSettingsAction || p.Action().Kind() == domain.AreaAction || p.Action().Kind() == domain.PolicyPruneAction || p.Action().Kind() == domain.ReadingPolicyAction || p.Action().Kind() == domain.DrugPolicyAction || p.Action().Kind() == domain.FoodPolicyAction || p.Action().Kind() == domain.ZoneDeleteAction || p.Action().Kind() == domain.ZoneCellEditAction || p.Action().Kind() == domain.StockpilePatchAction || p.Action().Kind() == domain.FoundationRemovalAction || p.Action().Kind() == domain.FloorRemovalAction {
			continue
		}
		// Construction, native plant labor, and the routine-dispatched action
		// families (defense, medical, haul, equip — see roundsExecutableKind)
		// all use the healthy-colony clock window; anything else is unsupported.
		if _, ok := p.Action().Building(); !ok {
			switch p.Action().Kind() {
			case domain.AcquisitionAction, domain.AcquisitionWithdrawAction, domain.ProductionBillAction, domain.OwnedDraftAction,
				domain.SubdueAction, domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.UseItemAction,
				domain.HaulAction, domain.EquipAction, domain.DropEquipmentAction, domain.GearReplaceAction, domain.ApparelPolicyAction, domain.RecoveryServiceAction,
				domain.MovementAction, domain.HusbandryAction, domain.PrisonerInteractionAction,
				domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction, domain.DeconstructionAction, domain.RemoveRoofAction, domain.AreaPlantCutAction, domain.CutPlantAction, domain.StripAction, domain.RulesAttachAction, domain.MoveBuildingAction, domain.UninstallBuildingAction, domain.CoverClearanceAction, domain.WastepackHaulAction, domain.MoodReliefAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.TradeAction, domain.QuestAcceptAction, domain.RitualAction, domain.IdeoligionReformAction, domain.AbilityAction, domain.IgniteAction, domain.RemoveProductionBillAction, domain.CloseDoorAction, domain.WallRemovalAction, domain.OpenCasketAction, domain.CaravanDepartureAction:
			default:
				return false, nil, executor.ErrHeld
			}
		}
		work = true
	}
	return work, items, nil
}

// pauseForHunt ends a running colony window once a hunt fight is open: prey
// are not hostile, so the native watcher never stops the window, and only a
// combat window re-decides the fight. The next step admits that window with
// the prey watched.
func (s *ClockScheduler) pauseForHunt(call context.Context, snapshot domain.GenerationSnapshot, status *k.Status, out *ClockSchedulerResult) error {
	if status.GetRunning() == nil || out.Combat {
		return nil
	}
	_, fightOpen, prey, err := clockSchedulerCombatPlan(call, s.player.journal, snapshot)
	if err != nil || !fightOpen || len(prey) == 0 {
		return err
	}
	out.Cleaned = true
	return s.session.CleanupClock(call)
}

// clockSchedulerCombatPlan reports whether the current rounds binds an
// ActiveCombat incident in deficit whose admitted plan still has open work: the only
// evidence under which live hostiles are watched rather than refused.
func clockSchedulerCombatPlan(ctx context.Context, journal *store.Store, current domain.GenerationSnapshot) (bool, bool, []domain.PawnID, error) {
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		return false, false, nil, err
	}
	if !review.Enabled || review.Snapshot != current {
		return false, false, nil, nil
	}
	fights, err := journal.OpenCombatFights(ctx)
	if err != nil {
		return false, false, nil, err
	}
	incident, need, found, err := roundsIncident(ctx, journal, review, policy.ActiveCombat)
	if err != nil || !found || need != domain.SituationActive {
		return false, false, nil, err
	}
	for _, method := range incident.Methods {
		// A fight (#852) owns the combat: its orders go out at each
		// stop, not as plan work. Closed, it holds the incident until its
		// claims are released (#910), with no stops armed.
		if fight, ok := fights[method.Plan]; ok {
			// A hunt origin's fight is watched through its live prey.
			var prey []domain.PawnID
			if fight.Open {
				prey = store.HuntPrey(incident.Incident)
			}
			return true, fight.Open, prey, nil
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return false, false, nil, err
		}
		if domain.StandardWorkOpen(plan.Progress) {
			return true, false, nil, nil
		}
	}
	return false, false, nil, nil
}

// armedCombatStops is the combat event list a combat window arms: every
// stop event while an admitted fight plan owns the combat, none otherwise.
func armedCombatStops(fightOpen bool) []k.CombatEvent {
	if !fightOpen {
		return nil
	}
	return slices.Clone(combatStopEvents)
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

// playerDriven reports whether the step read just taken shows the player
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

// stepDecision is the clock_step row of one step: target the cause the step
// acted on (timer, wake, settled, full, live), dur the step's wall, verdict
// what the step did about the window (admitted, refused, deferred, idle) and
// reason why (the first window refusal, else a stable word for the verdict).
// A step that errored out is failed with the error in attrs.
func stepDecision(out ClockSchedulerResult, err error, cause StepCause, elapsed time.Duration, attrs map[string]any) telemetry.Decision {
	d := telemetry.Decision{Kind: "clock_step", Component: "clock-scheduler", Target: string(cause), Dur: elapsed, Attrs: attrs}
	switch {
	case err != nil:
		d.Verdict, d.Reason = "failed", "step_error"
		attrs["error"] = err
	case out.Deferred:
		d.Verdict, d.Reason = "deferred", "deferred"
	case out.Attempt != nil && out.Attempt.Phase == store.ClockRefused:
		d.Verdict, d.Reason = "refused", "window_refused"
		if len(out.Decision.Refused) > 0 {
			d.Reason = string(out.Decision.Refused[0])
		}
	case out.Attempt != nil:
		d.Verdict, d.Reason = "admitted", "window_admitted"
	default:
		d.Verdict, d.Reason = "idle", "no_window"
	}
	return d
}

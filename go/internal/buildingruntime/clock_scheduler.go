package buildingruntime

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
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
	Profile string
	Start   bridge.ClockStart
	MaxAge  time.Duration
	// Routine is reviewed only after owned clock obligations have drained.
	Routine        *RoutineReviewer
	Sleeping       *RoutineBuildingPlanner
	Cooking        *RoutineBuildingPlanner
	Comfort        *RoutineBuildingPlanner
	Expansion      *RoutineBuildingPlanner
	Power          *RoutineBuildingPlanner
	Temperature    *RoutineBuildingPlanner
	RoutineMethods bool
}
type ClockSchedulerResult struct {
	Attempt                      *store.ClockAttempt
	Decision                     policy.ClockWindowDecision
	Routine                      *store.RoutineReviewResult
	Sleeping                     *RoutineBuildingResult
	Cooking                      *RoutineBuildingResult
	Comfort                      *RoutineBuildingResult
	Expansion                    *RoutineBuildingResult
	Power                        *RoutineBuildingResult
	Temperature                  *RoutineBuildingResult
	Running, Reconciled, Cleaned bool
}
type ClockScheduler struct {
	player              *Player
	session             *Session
	native              ClockWindowNative
	config              ClockSchedulerConfig
	clock               executor.Clock
	pollGate, renewGate chan struct{}
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
	if config.Sleeping != nil && (config.Routine == nil || config.Sleeping.reviewer != config.Routine || config.Sleeping.goal != policy.EnsureInitialShelter) {
		return nil, ErrControl
	}
	if config.Cooking != nil && (config.Routine == nil || config.Cooking.reviewer != config.Routine || config.Cooking.goal != policy.EnsureCooking) {
		return nil, ErrControl
	}
	if config.Comfort != nil && (config.Routine == nil || config.Comfort.reviewer != config.Routine || config.Comfort.goal != policy.EnsureComfort) {
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
	if config.RoutineMethods && (config.Routine == nil || !session.routineMethods) {
		return nil, ErrControl
	}
	config.Start.Policy = proto.Clone(config.Start.Policy).(*k.WatchPolicy)
	p := config.Start.Policy
	if p.GetMode() != k.WatchMode_WATCH_MODE_COLONY || len(p.AcknowledgedHostileIds)+len(p.AcknowledgedDownedColonistIds)+len(p.AcknowledgedInjuredColonistIds)+len(p.SurgicalRecoveryIds)+len(p.MedicalRestIds) != 0 || p.GetInjuryStopCooldownMs() != 0 {
		return nil, ErrControl
	}
	// Validate command arguments through the canonical bridge validator; these
	// fixed validation identities carry no runtime permission.
	err := bridge.ValidateClockExpectation(bridge.ClockExpectation{Identity: &c.Identity{ColonyId: proto.String("validation"), LoadToken: proto.String("validation"), MapId: proto.Int32(0)}, Attempt: &c.AttemptKey{ControllerSessionId: proto.String("validation"), ActionId: proto.String("validation"), AttemptId: proto.Uint64(1)}, Owner: &a.Owner{ControllerSessionId: proto.String("validation"), PlayerDirection: proto.Uint64(1)}, NativeGeneration: 1, Command: bridge.ClockCommand{Start: &config.Start}})
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

// Step performs at most one scheduling decision. It never acquires authority,
// renews an epoch, acknowledges events, or starts a background loop.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	var out ClockSchedulerResult
	call, epoch, done, err := s.player.enter(ctx, false)
	if err != nil {
		return out, err
	}
	defer done()
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
			if !state.Enabled || !boundaryWorld(v.Intent.Snapshot, world) {
				out.Cleaned = true
				return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
			}
			recovered, e := s.session.ReconcileClock(call, v.Intent.RequestID)
			out.Attempt = &recovered
			out.Reconciled = true
			return out, e
		}
	}
	if obligations && (!state.Enabled || !state.ObservationKnown || !boundaryWorld(state.Snapshot, world) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native)) {
		out.Cleaned = true
		return out, errors.Join(s.session.Disable(), s.session.CleanupClock(call))
	}
	if !state.ObservationKnown || !boundaryWorld(state.Snapshot, domain.GenerationSnapshot{Colony: domain.ColonyID(loaded.Context.Identity.GetColonyId()), Load: domain.LoadID(loaded.Context.Identity.GetLoadToken()), Map: domain.MapID(loaded.Context.Identity.GetMapId())}) || loaded.Context.NativeGeneration == nil || loaded.Context.GetNativeGeneration() != uint64(state.Snapshot.Native) {
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
	if _, err = boundaryContext(status.Context, state.Snapshot); err != nil {
		return out, errors.Join(err, s.session.Disable())
	}
	if status.Context.GetTick() < loaded.Context.GetTick() {
		return out, errors.Join(executor.ErrEvidence, s.session.Disable())
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
					}
				}
			}
		}
		if (status.GetStopping() != nil || !ownedCurrent) && obligations {
			out.Cleaned = true
			return out, s.session.CleanupClock(call)
		}
		if !state.Enabled || !ownedCurrent {
			return out, executor.ErrHeld
		}
		out.Running = true
		return out, nil
	}
	if obligations {
		out.Cleaned = true
		return out, s.session.CleanupClock(call)
	}
	if !state.Enabled {
		return out, executor.ErrAuthority
	}
	if err = s.player.current(call, epoch); err != nil {
		return out, err
	}
	if s.config.Routine != nil {
		review, reviewErr := s.config.Routine.step(call, epoch)
		if reviewErr != nil {
			return out, reviewErr
		}
		out.Routine = &review
		if review.Review.Mood != nil {
			for _, state := range review.Review.Mood.States {
				if state.Active && state.MentalRisk {
					return out, executor.ErrHeld
				}
			}
		}
	}
	if s.config.Sleeping != nil {
		method, methodErr := s.config.Sleeping.step(call, epoch)
		if methodErr != nil {
			return out, methodErr
		}
		out.Sleeping = &method
	}
	if s.config.Power != nil {
		method, err := s.config.Power.step(call, epoch)
		if err != nil {
			return out, err
		}
		out.Power = &method
	}
	if s.config.Temperature != nil {
		method, err := s.config.Temperature.step(call, epoch)
		if err != nil {
			return out, err
		}
		out.Temperature = &method
	}
	if s.config.Cooking != nil {
		method, methodErr := s.config.Cooking.step(call, epoch)
		if methodErr != nil {
			return out, methodErr
		}
		out.Cooking = &method
	}
	if s.config.Comfort != nil {
		method, err := s.config.Comfort.step(call, epoch)
		if err != nil {
			return out, err
		}
		out.Comfort = &method
	}
	if s.config.Expansion != nil {
		method, err := s.config.Expansion.step(call, epoch)
		if err != nil {
			return out, err
		}
		out.Expansion = &method
	}
	emergency, _, err := s.native.ReadEmergency(call, loaded.Context.Identity)
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(emergency.Context, state.Snapshot); err != nil {
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
	if s.config.RoutineMethods {
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
			if err := s.player.journal.AuthorizeRoutinePlan(call, state.Snapshot, target); err != nil {
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
	clockState := policy.ClockWindowState("")
	start := s.config.Start
	var nativeWorkTicks uint32
	for _, result := range []*RoutineBuildingResult{out.Sleeping, out.Comfort, out.Expansion, out.Power, out.Temperature} {
		if result != nil {
			nativeWorkTicks = max(nativeWorkTicks, result.NativeWorkTicks)
		}
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
	facts := policy.ClockWindowFacts{Current: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), StartedAt: started, ObservedAt: s.clock.Now(), Emergency: emergencyFacts, Review: policy.ClockWindowReview{Revision: review.Revision, Captured: review.InboxCursor, Reviewed: review.ReviewedCursor, Acknowledged: review.AcknowledgedCursor, HasHolds: domain.Known(len(review.Holds) > 0)}, Status: policy.ClockWindowStatus{Snapshot: state.Snapshot, Tick: domain.Tick(status.Context.GetTick()), State: clockState, ActualPaused: draftBool(status.ActualPaused), NativeTickBoundary: draftBool(status.NativeTickBoundary), DurableEvents: draftBool(status.DurableEvents)}, Obligations: policy.ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(work)}
	if status.NewestCursor != nil {
		facts.Status.NewestCursor = domain.Known(status.GetNewestCursor())
	}
	out.Decision = policy.EvaluateClockWindow(facts, policy.ClockWindowLimits{Now: s.clock.Now(), MaxAge: s.config.MaxAge, MaxTicks: start.MaxTicks})
	if !out.Decision.Admitted {
		return out, executor.ErrHeld
	}
	admission := &store.ClockWindowAdmission{Profile: s.config.Profile, Snapshot: state.Snapshot, Tick: facts.Tick, ReviewRevision: review.Revision, CapturedCursor: review.InboxCursor, MaxTicks: start.MaxTicks}
	key, err := clockSchedulerKey(admission, fingerprint, start)
	if err != nil {
		return out, err
	}
	start.Policy = proto.Clone(start.Policy).(*k.WatchPolicy)
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
	attempt, err := s.session.CommandClockWindow(call, ClockWindowRequest{Intent: intent, Facts: facts, MaxAge: s.config.MaxAge})
	out.Attempt = &attempt
	if errors.Is(err, store.ErrConflict) {
		return out, errors.Join(executor.ErrHeld, err)
	}
	return out, err
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
		// Only ordinary building work can justify this healthy-colony clock window.
		if _, ok := p.Action().Building(); !ok {
			return false, nil, executor.ErrHeld
		}
		work = true
	}
	return work, items, nil
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

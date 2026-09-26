package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PollEvents never waits for the player gate. Interruption invalidation precedes
// persistence and owned cleanup, which may need to join an active command.
func (s *ClockScheduler) PollEvents(ctx context.Context, native ClockEventNative, limit uint32, wait time.Duration) (out ClockPollResult, err error) {
	// fresh is set once the poll holds a page that carries something earlier
	// polls have not applied; until then a disable is for evidence already
	// applied (see disableOnEvidence).
	fresh := false
	fail := func(cause error) (ClockPollResult, error) {
		s.admissionWarm.Store(nil)
		out.Interrupted = true
		s.running.Store(false)
		disabled := s.disableOnEvidence(fresh)
		cleanup, cancel := context.WithTimeout(context.Background(), s.session.control.config.CallTimeout)
		defer cancel()
		return out, errors.Join(cause, disabled, s.session.CleanupClock(cleanup))
	}
	if native == nil || limit < 1 || limit > 128 || wait < 0 || wait > bridge.ClockEventsMaxWaitMs*time.Millisecond {
		return fail(ErrControl)
	}
	select {
	case s.pollGate <- struct{}{}:
	case <-ctx.Done():
		return fail(ctx.Err())
	}
	defer func() { <-s.pollGate }()
	// One trace per poll: its read and the events it publishes (#298).
	ctx, _ = telemetry.EnsureTrace(ctx)
	call, cancel := context.WithTimeout(ctx, s.session.control.config.CallTimeout)
	defer cancel()
	invalidate := func() error { out.Interrupted = true; return s.disableOnEvidence(fresh) }
	review, err := s.player.journal.ReadClockReview(call, s.config.Profile)
	if err != nil {
		return fail(err)
	}
	// A standing hold disables authority granted under it, unless the page
	// read below shows the grant came after the hold (see answered).
	standing := len(review.Holds) > 0
	if err = s.maintainClockAttempts(call); err != nil {
		return fail(err)
	}
	if _, err = s.player.journal.CompactClockHistory(call, s.config.Profile); err != nil {
		return fail(err)
	}
	before := s.session.State()
	// The poll's one native read: the current scope and the events page
	// after the review's cursor, from the same hop (issue #127). The page is
	// read for the scope the bundle reports, so a load between the two is
	// impossible; authority is judged against that scope below.
	events := &o.BundleEventsRequest{AfterCursor: proto.Int64(review.InboxCursor), Limit: proto.Uint32(limit)}
	if wait > 0 {
		events.WaitMs = proto.Uint32(uint32(wait / time.Millisecond))
	}
	reply, _, err := native.ReadBundle(call, &o.BundleRequest{Events: events})
	if err != nil {
		return fail(err)
	}
	current := reply.GetObserved().GetContext()
	if err = bridge.ValidateContext(current); err != nil {
		return fail(err)
	}
	state := s.session.State()
	if !clockPollMatchesAuthority(current, state) {
		if state != before && !out.Interrupted {
			return out, executor.ErrAuthority
		}
		if err = s.session.control.disableObserved(state); errors.Is(err, store.ErrConflict) {
			if out.Interrupted {
				return fail(executor.ErrAuthority)
			}
			return out, executor.ErrAuthority
		} else if err != nil {
			return fail(err)
		}
		out.Interrupted = true
	}
	request := bridge.BundleEventsRequest(current.Identity, events)
	page := reply.GetObserved().GetEvents()
	if err = bridge.ValidateClockEventsPage(page, request); err != nil {
		return fail(err)
	}
	fresh = page.GetGap() || len(page.Events) > 0
	// The first page fixes the history watermark: native's newest cursor
	// when this process first read events. While nothing is enabled, a page
	// at or before it is not fresh evidence: a kept game's backlog of stops
	// from before this process started interrupts no authority it holds,
	// and disabling for it page by page cancelled the first resume's
	// acquisition in flight (#322). The events are still captured and
	// their holds still stand until acknowledged.
	if !s.history.known {
		s.history = clockHistory{cursor: page.GetNewestCursor(), known: true}
	}
	if !state.Enabled && clockPollLastCursor(page) <= s.history.cursor {
		fresh = false
	}
	if page.Context.GetTick() < current.GetTick() || current.NativeGeneration != nil && (page.Context.NativeGeneration == nil || page.Context.GetNativeGeneration() < current.GetNativeGeneration()) {
		return fail(executor.ErrEvidence)
	}
	latest := s.session.State()
	if !clockPollMatchesAuthority(page.Context, latest) {
		if latest != state && !out.Interrupted && !page.GetGap() && !clockPollInterrupts(page) {
			return out, executor.ErrAuthority
		}
		if err = s.session.control.disableObserved(latest); errors.Is(err, store.ErrConflict) {
			// Interruption evidence must still be captured even if authority changed.
			if !page.GetGap() && !clockPollInterrupts(page) {
				return out, executor.ErrAuthority
			}
		} else if err != nil {
			return fail(err)
		}
		if err == nil {
			out.Interrupted = true
		}
	}
	// An interruption the page carries before the grant of the generation
	// authority holds now was answered by that grant: the service's own
	// pause revoked, stopped the clock and re-acquired (a checkpoint), and
	// disabling on the stop would revoke the new grant again (#322). The
	// hold it leaves still keeps a window from opening until acknowledged,
	// and the poll reports it held without disabling. The grant is
	// remembered, so a hold that stands from an earlier page (the stop
	// and the grant read by different polls) is answered too.
	if generation, cursor, ok := clockPollGrant(page); ok {
		s.grant = clockGrant{generation: generation, cursor: cursor, known: true}
	}
	granted := s.grant.cursorFor(latest)
	answered := !page.GetGap() && !clockPollInterruptsAfter(page, granted)
	if standing && !s.grant.answers(latest, review.Holds, granted) {
		if err = invalidate(); err != nil {
			return fail(err)
		}
	}
	if page.GetGap() || clockPollInterruptsAfter(page, granted) {
		if clockDebug() {
			clockSchedulerLog("poll: interrupting gap=%v events=%s", page.GetGap(), clockPollEventKinds(page))
		}
		if err = invalidate(); err != nil {
			return fail(err)
		}
	}
	if err = call.Err(); err != nil {
		return fail(err)
	}
	_, out.Captured, err = s.player.journal.AppendClockEvents(call, s.config.Profile, request, page)
	if err != nil {
		return fail(err)
	}
	if out.Captured {
		s.admissionWarm.Store(nil)
		telemetry.ObserveTick(page.Context.GetTick())
		clockPollEvents(call, page)
		if clockPollManual(page) {
			s.noteManual(s.clock.Now())
		}
		if clockPollStopped(page) {
			s.running.Store(false)
		}
		out.Wake, out.Invalidated, out.AuthorityChanged, out.Stopped, out.StoppedAt = clockPageWakeStopped(page)
		out.InvalidatedSections = clockPageSections(page)
		// Recorded here, not only through the step's wake reason: a step
		// already past taking its reason (waiting on the player gate
		// behind the Worker) must still see an outcome this page carried
		// before it admits a window (issue #162).
		s.latched.remember(out.Wake)
		// The reviewer's retained census observed through the same facts:
		// whatever the page made stale retires it too.
		if s.facts.apply(page) && s.config.Routine != nil {
			s.config.Routine.census.invalidate()
		}
	}
	review, err = s.player.journal.ReadClockReview(call, s.config.Profile)
	if err != nil {
		return fail(err)
	}
	out.Review, err = s.player.journal.ReviewClockEvents(call, s.config.Profile, review.Revision)
	if err != nil {
		return fail(err)
	}
	if len(out.Review.Holds) > 0 && answered && !out.Interrupted && s.grant.answers(latest, out.Review.Holds, granted) {
		// Holds the grant answers are acknowledged here: the player's own
		// resume granted this generation after every interruption they cover,
		// and nothing else would clear them while authority stands (#322).
		ack := store.ClockAcknowledgement{RequestID: fmt.Sprintf("clock-ack-%d-%d", out.Review.Revision, out.Review.ReviewedCursor), ExpectedRevision: out.Review.Revision, ThroughCursor: out.Review.ReviewedCursor}
		if out.Review, err = s.player.journal.AcknowledgeClockEvents(call, s.config.Profile, ack); err != nil {
			return fail(err)
		}
	}
	if len(out.Review.Holds) > 0 || out.Review.ReviewedCursor != out.Review.InboxCursor || out.Review.ReviewedCursor != page.GetNewestCursor() {
		if answered && !out.Interrupted && s.grant.answers(latest, out.Review.Holds, granted) {
			s.running.Store(false)
			cleanup, cancel := context.WithTimeout(context.Background(), s.session.control.config.CallTimeout)
			defer cancel()
			return out, errors.Join(executor.ErrHeld, s.session.CleanupClock(cleanup))
		}
		return fail(executor.ErrHeld)
	}
	if err = call.Err(); err != nil {
		return fail(err)
	}
	if out.Interrupted {
		return fail(nil)
	}
	if out.Stopped {
		s.warmAdmission(call, current)
	}
	return out, nil
}

// disableOnEvidence disables authority for what a poll or renewal found.
// Fresh evidence (a captured page with events or a gap, a lease refused
// while authority is live) disables before persistence, an acquisition in
// progress included. Evidence earlier polls already applied (a standing
// unacknowledged hold, an empty page, a failed read while nothing is
// enabled) is not applied again: authority is already off, and every
// Control.Disable replaces the control epoch, which cancels an Acquire's
// SetMode in flight and reports the resume uncertain at generation 0 (#253).
// The hold still keeps a window from opening (clock.Window) until it is
// acknowledged; the grant itself is safe under it.
func (s *ClockScheduler) disableOnEvidence(fresh bool) error {
	if !fresh && !s.session.State().Enabled {
		return nil
	}
	return s.session.Disable()
}

// Event ingestion is profile-wide and does not require a live lease. Compare
// fresh evidence with current enabled authority, not a snapshot from before an
// overlapping acquire. A stale poll retries; real interruption evidence still
// disables writes before persistence, including an acquisition in progress.
func clockPollMatchesAuthority(observed *c.ObservationContext, state ControlState) bool {
	return !state.Enabled || state.ObservationKnown && proto.Equal(observed.Identity, controlIdentity(state.Snapshot)) && observed.NativeGeneration != nil && observed.GetNativeGeneration() == uint64(state.Snapshot.Native)
}

// clockPollEventKinds names, for the clock trace, which event(s) in a page
// tripped clockPollInterrupts, since that function itself only returns a
// bool.
func clockPollEventKinds(page *k.EventsPage) string {
	kinds := make([]string, 0, len(page.Events))
	for _, event := range page.Events {
		switch v := event.Event.(type) {
		case *k.Event_Stopped:
			kinds = append(kinds, "Stopped("+v.Stopped.GetReason().String()+")")
		default:
			kinds = append(kinds, fmt.Sprintf("%T", event.Event))
		}
	}
	return strings.Join(kinds, ",")
}

// clockPollEvents publishes the typed service events a committed page
// carries: "scheduler_stop" for each Stopped event (reason, detail, the
// native stop stamp), "authority_change" for each AuthorityChanged event,
// and "alert_row" for each game alert (#256). Every other event kind is the
// step reason's business and stays in the debug trace.
func clockPollEvents(ctx context.Context, page *k.EventsPage) {
	for _, event := range page.GetEvents() {
		switch v := event.Event.(type) {
		case *k.Event_Stopped:
			clockEvent(ctx, "clock-scheduler", "scheduler_stop", "window stopped", append([]any{"reason", v.Stopped.GetReason().String(), "evidence", clockStopEvidence(v.Stopped), "cursor", event.GetCursor(), "observed_at_unix_ms", event.GetObservedAtUnixMs(), "benign", clock.BenignStopEvent(v.Stopped)}, clockStopLegs(event, v.Stopped)...)...)
		case *k.Event_AuthorityChanged:
			clockEvent(ctx, "clock-scheduler", "authority_change", "authority changed", "reason", v.AuthorityChanged.GetReason(), "active", v.AuthorityChanged.GetActive(), "generation", v.AuthorityChanged.GetGeneration(), "previous_generation", v.AuthorityChanged.GetPreviousGeneration(), "cursor", event.GetCursor())
		case *k.Event_Alert:
			clockEvent(ctx, "clock-scheduler", "alert_row", "game alert", "key", v.Alert.GetKey(), "label", v.Alert.GetLabel(), "priority", v.Alert.GetPriority(), "cursor", event.GetCursor())
		}
	}
}

// clockStopLegs are the stop's latency legs on the row, the live half of
// the #621 split the throughput report computes offline: the tick the stop
// was taken at, the tick the supervisor first raised it, the tick the
// hazard arose (when the evidence carries one) and how long the stop sat
// unobserved in native before the page that carried it was composed. Ticks
// are native's own; the age is native's own span, so nothing subtracts one
// process's clock from another's. The spectator panel (#632) reads them
// beside the readmit leg the following clock_step row records.
func clockStopLegs(event *k.Event, stop *k.StopEvent) []any {
	attrs := []any{"tick", event.GetContext().GetTick()}
	if stop.DetectedTick != nil {
		attrs = append(attrs, "detected_tick", stop.GetDetectedTick(), "stop_ticks", event.GetContext().GetTick()-stop.GetDetectedTick())
		if stop.OccurrenceTick != nil {
			attrs = append(attrs, "occurrence_tick", stop.GetOccurrenceTick(), "detect_ticks", stop.GetDetectedTick()-stop.GetOccurrenceTick())
		}
	}
	if event.AgeAtReplyMs != nil {
		attrs = append(attrs, "age_at_reply_ms", float64(event.GetAgeAtReplyMs()))
	}
	return attrs
}

// clockStopEvidence names the evidence a stop event carries (the oneof
// case: "budget", "pause", "watch", ...), or "" when it carries none.
func clockStopEvidence(stop *k.StopEvent) string {
	if stop == nil || stop.Evidence == nil {
		return ""
	}
	name := fmt.Sprintf("%T", stop.Evidence)
	return strings.ToLower(name[strings.LastIndex(name, "_")+1:])
}

// clockPollStopped reports whether the page carries a stopped event: the
// window the scheduler admitted is no longer running, whatever stopped it.
func clockPollStopped(page *k.EventsPage) bool {
	for _, event := range page.Events {
		if _, ok := event.Event.(*k.Event_Stopped); ok {
			return true
		}
	}
	return false
}
func clockPollInterrupts(page *k.EventsPage) bool {
	return clockPollInterruptsAfter(page, -1)
}

// clockPollInterruptsAfter reports whether the page carries an interruption
// past the cursor granted (-1 for none).
func clockPollInterruptsAfter(page *k.EventsPage, granted int64) bool {
	for _, event := range page.Events {
		if event.GetCursor() > granted && clock.EventInterrupts(event) {
			return true
		}
	}
	return false
}

// clockPollLastCursor is the cursor of the page's last event, -1 for none.
func clockPollLastCursor(page *k.EventsPage) int64 {
	if len(page.Events) == 0 {
		return -1
	}
	return page.Events[len(page.Events)-1].GetCursor()
}

// clockPollGrant is the page's last AuthorityChanged grant (generation and
// cursor), if it carries one.
func clockPollGrant(page *k.EventsPage) (generation uint64, cursor int64, ok bool) {
	for _, event := range page.Events {
		if v, isChange := event.Event.(*k.Event_AuthorityChanged); isChange && v.AuthorityChanged.GetActive() && v.AuthorityChanged.Generation != nil {
			generation, cursor, ok = v.AuthorityChanged.GetGeneration(), event.GetCursor(), true
		}
	}
	return generation, cursor, ok
}

// clockHistory is native's newest event cursor when this process first read
// events: the watermark below which events are history, not interruptions.
type clockHistory struct {
	cursor int64
	known  bool
}

// clockGrant is the latest grant a committed page carried.
type clockGrant struct {
	generation uint64
	cursor     int64
	known      bool
}

// cursorFor is the cursor at which the generation state holds enabled was
// granted, or -1 when authority is off or the grant was not seen.
func (g clockGrant) cursorFor(state ControlState) int64 {
	if !g.known || !state.Enabled || !state.ObservationKnown || uint64(state.Snapshot.Native) != g.generation {
		return -1
	}
	return g.cursor
}

// answers reports whether every hold precedes the grant of the generation
// state holds: evidence that grant already answered, which the scheduler
// does not disable for again. granted is that cursor when the caller has
// it, -1 to derive it from state.
func (g clockGrant) answers(state ControlState, holds []clock.Hold, granted int64) bool {
	if granted < 0 {
		granted = g.cursorFor(state)
	}
	if granted < 0 {
		return false
	}
	for _, hold := range holds {
		if hold.ThroughCursor > granted {
			return false
		}
	}
	return true
}

// clockPollManual reports whether the page carries a Manual authority
// change: the player pressed a speed key, which revokes authority before
// the service re-acquires it (#601).
func clockPollManual(page *k.EventsPage) bool {
	for _, event := range page.GetEvents() {
		if v, ok := event.Event.(*k.Event_AuthorityChanged); ok && v.AuthorityChanged.GetReason() == "Manual" {
			return true
		}
	}
	return false
}

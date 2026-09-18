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
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PollEvents never waits for the player gate. Interruption invalidation precedes
// persistence and owned cleanup, which may need to join an active command.
func (s *ClockScheduler) PollEvents(ctx context.Context, native ClockEventNative, limit uint32, wait time.Duration) (out ClockPollResult, err error) {
	fail := func(cause error) (ClockPollResult, error) {
		out.Interrupted = true
		disabled := s.session.Disable()
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
	call, cancel := context.WithTimeout(ctx, s.session.control.config.CallTimeout)
	defer cancel()
	invalidate := func() error { out.Interrupted = true; return s.session.Disable() }
	review, err := s.player.journal.ReadClockReview(call, s.config.Profile)
	if err != nil {
		return fail(err)
	}
	if len(review.Holds) > 0 {
		if err = invalidate(); err != nil {
			return fail(err)
		}
	}
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
	if page.GetGap() || clockPollInterrupts(page) {
		if clockSchedulerDebug {
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
		out.Wake, out.Invalidated, out.AuthorityChanged = clockPageWake(page)
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
	if len(out.Review.Holds) > 0 || out.Review.ReviewedCursor != out.Review.InboxCursor || out.Review.ReviewedCursor != page.GetNewestCursor() {
		return fail(executor.ErrHeld)
	}
	if err = call.Err(); err != nil {
		return fail(err)
	}
	if out.Interrupted {
		return fail(nil)
	}
	return out, nil
}

// Event ingestion is profile-wide and does not require a live lease. Compare
// fresh evidence with current enabled authority, not a snapshot from before an
// overlapping acquire. A stale poll retries; real interruption evidence still
// disables writes before persistence, including an acquisition in progress.
func clockPollMatchesAuthority(observed *c.ObservationContext, state ControlState) bool {
	return !state.Enabled || state.ObservationKnown && proto.Equal(observed.Identity, controlIdentity(state.Snapshot)) && observed.NativeGeneration != nil && observed.GetNativeGeneration() == uint64(state.Snapshot.Native)
}

// clockPollEventKinds is a TEMPORARY diagnostic aid (RIMGOVERNOR_CLOCK_DEBUG=1)
// for issue #42: it names which event(s) in a page tripped clockPollInterrupts,
// since that function itself only returns a bool.
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
func clockPollInterrupts(page *k.EventsPage) bool {
	for _, event := range page.Events {
		if clock.EventInterrupts(event) {
			return true
		}
	}
	return false
}

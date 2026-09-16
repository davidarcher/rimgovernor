package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
	"time"
)

func clockWindowFixture(t *testing.T) (ClockWindowFacts, ClockWindowLimits) {
	t.Helper()
	now := time.Unix(100, 0)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	emergency, err := NewEmergencySnapshot(snapshot, 10, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		t.Fatal(err)
	}
	f := ClockWindowFacts{Current: snapshot, Tick: 10, StartedAt: now.Add(-time.Second), ObservedAt: now, Emergency: emergency, Review: ClockWindowReview{Revision: 3, Captured: 4, Reviewed: 4, Acknowledged: 2, HasHolds: domain.Known(false)}, Status: ClockWindowStatus{Snapshot: snapshot, Tick: 10, State: ClockStopped, ActualPaused: domain.Known(true), NativeTickBoundary: domain.Known(true), DurableEvents: domain.Known(true), NewestCursor: domain.Known(int64(4))}, Obligations: ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(true)}
	return f, ClockWindowLimits{Now: now, MaxAge: time.Second, MaxTicks: 100}
}
func TestClockWindowHealthyFiniteAdmission(t *testing.T) {
	f, limits := clockWindowFixture(t)
	for _, state := range []ClockWindowState{ClockStopped, ClockNeverStarted} {
		f.Status.State = state
		f.Status.DurableEvents = domain.Known(state != ClockNeverStarted)
		d := EvaluateClockWindow(f, limits)
		if !d.Admitted || len(d.Refused) != 0 || d.Snapshot != f.Current || d.Tick != f.Tick || d.ReviewRevision != f.Review.Revision || d.CapturedCursor != 4 || d.MaxTicks != 100 {
			t.Fatal(d)
		}
	}
}
func TestClockWindowConservativeHolds(t *testing.T) {
	cases := map[string]struct {
		edit   func(*ClockWindowFacts, *ClockWindowLimits)
		reason ClockWindowReason
	}{
		"native zero":        {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Current.Native = 0 }, ClockWindowUnknown},
		"revision zero":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Current.Revision = 0 }, ClockWindowUnknown},
		"invalid world":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Current.Colony = "" }, ClockWindowUnknown},
		"world changed":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.Snapshot.Load = "other" }, ClockWindowStale},
		"generation changed": {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.Snapshot.Native++ }, ClockWindowStale},
		"tick mismatch":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.Tick++ }, ClockWindowStale},
		"negative tick":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Tick = -1 }, ClockWindowUnknown},
		"future":             {func(f *ClockWindowFacts, l *ClockWindowLimits) { f.ObservedAt = l.Now.Add(time.Nanosecond) }, ClockWindowStale},
		"reversed":           {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.ObservedAt = f.StartedAt.Add(-time.Nanosecond) }, ClockWindowStale},
		"old start":          {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.StartedAt = f.StartedAt.Add(-time.Nanosecond) }, ClockWindowStale},
		"missing time":       {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.StartedAt = time.Time{} }, ClockWindowStale},
		"unknown emergency":  {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Emergency = EmergencySnapshot{} }, ClockWindowStale},
		"missing catalog":    {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Obligations.Complete = domain.Unknown[bool]() }, ClockWindowUnknown},
		"incomplete catalog": {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Obligations.Complete = domain.Known(false) }, ClockWindowUnknown},
		"owned":              {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Obligations.OwnedEpochPending = domain.Known(true) }, ClockWindowOutstanding},
		"uncertain start": {func(f *ClockWindowFacts, _ *ClockWindowLimits) {
			f.Obligations.UnknownStartPending = domain.Known(true)
		}, ClockWindowOutstanding},
		"missing outstanding": {func(f *ClockWindowFacts, _ *ClockWindowLimits) {
			f.Obligations.OwnedEpochPending = domain.Unknown[bool]()
		}, ClockWindowUnknown},
		"complete work":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.WorkRemaining = domain.Known(false) }, ClockWindowNoWork},
		"unknown work":       {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.WorkRemaining = domain.Unknown[bool]() }, ClockWindowUnknown},
		"running paused":     {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.State = ClockRunning }, ClockWindowNotPaused},
		"stopping paused":    {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.State = ClockStopping }, ClockWindowNotPaused},
		"unknown state":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.State = "other" }, ClockWindowUnknown},
		"unpaused":           {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.ActualPaused = domain.Known(false) }, ClockWindowNotPaused},
		"missing pause":      {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.ActualPaused = domain.Unknown[bool]() }, ClockWindowUnknown},
		"no native boundary": {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.NativeTickBoundary = domain.Known(false) }, ClockWindowUnknown},
		"no durable events":  {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.DurableEvents = domain.Known(false) }, ClockWindowUnknown},
		"unknown initial durability": {func(f *ClockWindowFacts, _ *ClockWindowLimits) {
			f.Status.State = ClockNeverStarted
			f.Status.DurableEvents = domain.Unknown[bool]()
		}, ClockWindowUnknown},
		"unreviewed":       {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Review.Reviewed-- }, ClockWindowUnreviewed},
		"ack ahead":        {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Review.Acknowledged = 5 }, ClockWindowUnreviewed},
		"negative cursor":  {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Review.Acknowledged = -1 }, ClockWindowUnreviewed},
		"native ahead":     {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.NewestCursor = domain.Known(int64(5)) }, ClockWindowUnreviewed},
		"native regressed": {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.NewestCursor = domain.Known(int64(3)) }, ClockWindowUnreviewed},
		"unknown cursor":   {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Status.NewestCursor = domain.Unknown[int64]() }, ClockWindowUnknown},
		"new interruption": {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Review.HasHolds = domain.Known(true) }, ClockWindowInterrupted},
		"unknown holds":    {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Review.HasHolds = domain.Unknown[bool]() }, ClockWindowUnknown},
		"zero budget":      {func(_ *ClockWindowFacts, l *ClockWindowLimits) { l.MaxTicks = 0 }, ClockWindowInvalidLimits},
		"excess budget":    {func(_ *ClockWindowFacts, l *ClockWindowLimits) { l.MaxTicks = 1800001 }, ClockWindowInvalidLimits},
		"overflow":         {func(f *ClockWindowFacts, _ *ClockWindowLimits) { f.Tick = domain.Tick(math.MaxInt64 - 1) }, ClockWindowInvalidLimits},
		"zero age":         {func(_ *ClockWindowFacts, l *ClockWindowLimits) { l.MaxAge = 0 }, ClockWindowInvalidLimits},
		"missing now":      {func(_ *ClockWindowFacts, l *ClockWindowLimits) { l.Now = time.Time{} }, ClockWindowInvalidLimits},
	}
	for name, c := range cases {
		t.Run(name, func(t *testing.T) {
			f, l := clockWindowFixture(t)
			c.edit(&f, &l)
			d := EvaluateClockWindow(f, l)
			found := false
			seen := map[ClockWindowReason]bool{}
			for _, reason := range d.Refused {
				if seen[reason] {
					t.Fatal("duplicate", d)
				}
				seen[reason] = true
				found = found || reason == c.reason
			}
			if d.Admitted || !found || d.Snapshot != (domain.GenerationSnapshot{}) || d.MaxTicks != 0 || d.Tick != 0 || d.CapturedCursor != 0 || d.ReviewRevision != 0 {
				t.Fatal(d)
			}
		})
	}
}
func TestClockWindowEmergencyAndTickBudgetBoundaries(t *testing.T) {
	f, l := clockWindowFixture(t)
	// "medical" is deliberately NOT refused: a colonist needing tend can only be
	// resolved by ticks passing (RoutineTendPlanner's dispatched order needs the
	// native clock running to execute), so holding the window here would deadlock
	// rather than protect anything. "threat" still refuses -- an unmanaged raid
	// should not auto-advance.
	for _, kind := range []string{"threat", "stale"} {
		t.Run(kind, func(t *testing.T) {
			f := f
			facts := EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}
			tick := f.Tick
			switch kind {
			case "threat":
				facts.Threats = []EmergencyThreat{{ID: "enemy", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			case "stale":
				tick--
			}
			var err error
			f.Emergency, err = NewEmergencySnapshot(f.Current, tick, facts)
			if err != nil {
				t.Fatal(err)
			}
			d := EvaluateClockWindow(f, l)
			want := ClockWindowUnsafe
			if kind == "stale" {
				want = ClockWindowStale
			}
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0] != want {
				t.Fatal(d)
			}
		})
	}
	t.Run("medical", func(t *testing.T) {
		f := f
		facts := EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
			Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(true)}}}
		var err error
		f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, facts)
		if err != nil {
			t.Fatal(err)
		}
		d := EvaluateClockWindow(f, l)
		if !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	})
	for _, budget := range []uint32{1, 1800000} {
		l.MaxTicks = budget
		f.Tick = domain.Tick(math.MaxInt64 - int64(budget))
		f.Status.Tick = f.Tick
		var err error
		f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)})
		if err != nil {
			t.Fatal(err)
		}
		if d := EvaluateClockWindow(f, l); !d.Admitted {
			t.Fatal(d)
		}
	}
}

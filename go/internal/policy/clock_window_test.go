package policy

import (
	"math"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func clockWindowFixture(t *testing.T) (ClockWindowFacts, ClockWindowLimits) {
	t.Helper()
	now := time.Unix(100, 0)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	emergency, err := NewEmergencySnapshot(snapshot, 10, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		t.Fatal(err)
	}
	f := ClockWindowFacts{Current: snapshot, Tick: 10, StartedAt: now.Add(-time.Second), ObservedAt: now, Emergency: emergency, Review: ClockWindowReview{Revision: 3, Captured: 4, Reviewed: 4, Acknowledged: 2, HasHolds: domain.Known(false)}, Status: ClockWindowStatus{Snapshot: snapshot, Tick: 10, State: ClockStopped, ActualPaused: domain.Known(true), NativeTickBoundary: domain.Known(true), DurableEvents: domain.Known(true), NewestCursor: domain.Known(int64(4))}, Obligations: ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(true), CombatPlan: domain.Known(false)}
	return f, ClockWindowLimits{Now: now, MaxAge: time.Second, MaxTicks: 100}
}
func TestClockWindowHealthyFiniteAdmission(t *testing.T) {
	f, limits := clockWindowFixture(t)
	for _, state := range []ClockWindowState{ClockStopped, ClockNeverStarted} {
		f.Status.State = state
		f.Status.DurableEvents = domain.Known(state != ClockNeverStarted)
		d := EvaluateClockWindow(f, limits)
		if !d.Admitted || len(d.Refused) != 0 || d.Snapshot != f.Current || d.Tick != f.Tick || d.ReviewRevision != f.Review.Revision || d.CapturedCursor != 4 || d.MaxTicks != 100 || d.Mode != ClockWindowColony || len(d.Hostiles) != 0 {
			t.Fatal(d)
		}
	}
}
func TestClockWindowCombatPlanWatchesLiveHostiles(t *testing.T) {
	f, l := clockWindowFixture(t)
	l.CombatMaxTicks = 30
	threats := func(rows ...EmergencyThreat) {
		t.Helper()
		var err error
		f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Threats: rows})
		if err != nil {
			t.Fatal(err)
		}
	}
	live := func(id PawnID, kind ThreatKind) EmergencyThreat {
		return EmergencyThreat{ID: id, Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false)}
	}
	threats(live("zed", Hostile), live("abe", Hostile), live("wolf", HuntingPredator), EmergencyThreat{ID: "down", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(true)}, live("bear", NearbyPredator))
	// Unknown plan evidence is not permission to fight or to refuse quietly.
	if d := EvaluateClockWindow(f, l); d.Admitted || len(d.Refused) != 1 || d.Refused[0] != ClockWindowUnsafe {
		t.Fatal(d)
	}
	f.CombatPlan = domain.Unknown[bool]()
	if d := EvaluateClockWindow(f, l); d.Admitted || len(d.Refused) != 1 || d.Refused[0] != ClockWindowUnknown {
		t.Fatal(d)
	}
	f.CombatPlan = domain.Known(true)
	d := EvaluateClockWindow(f, l)
	if !d.Admitted || len(d.Refused) != 0 || d.Mode != ClockWindowCombat || d.MaxTicks != 30 || !reflect.DeepEqual(d.Hostiles, []PawnID{"abe", "wolf", "zed"}) {
		t.Fatal(d)
	}
	// A zero combat budget keeps the colony budget; unknown hostile status
	// still refuses even under a plan.
	l.CombatMaxTicks = 0
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.MaxTicks != 100 || d.Mode != ClockWindowCombat {
		t.Fatal(d)
	}
	threats(live("zed", Hostile), EmergencyThreat{ID: "fog", Kind: Hostile, Dead: domain.Unknown[bool](), Downed: domain.Known(false)})
	if d := EvaluateClockWindow(f, l); d.Admitted || len(d.Refused) != 1 || d.Refused[0] != ClockWindowUnknown {
		t.Fatal(d)
	}
	// Every hostile dead or downed: the plan alone does not keep combat mode.
	threats(EmergencyThreat{ID: "zed", Kind: Hostile, Dead: domain.Known(true), Downed: domain.Known(false)}, EmergencyThreat{ID: "abe", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(true)})
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowColony || len(d.Hostiles) != 0 || d.MaxTicks != 100 {
		t.Fatal(d)
	}
}

// A colonist the census already knows downed is acknowledged in either mode
// so the native watcher does not stop the window at zero ticks on the same
// casualty (#213); a dead, unknown or merely bleeding colonist is not.
func TestClockWindowAcknowledgesKnownDownedColonists(t *testing.T) {
	f, l := clockWindowFixture(t)
	colonist := func(id PawnID, dead, downed, bleeding domain.Fact[bool]) EmergencyPawn {
		return EmergencyPawn{ID: id, Dead: dead, Downed: downed, Bleeding: bleeding, NeedsTend: domain.Known(false)}
	}
	census := func(colonists []EmergencyPawn, threats ...EmergencyThreat) {
		t.Helper()
		var err error
		f.Emergency, err = NewEmergencySnapshot(f.Current, f.Tick, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: colonists, Threats: threats})
		if err != nil {
			t.Fatal(err)
		}
	}
	known, no := domain.Known[bool], domain.Known(false)
	census([]EmergencyPawn{colonist("zed", no, known(true), no), colonist("abe", no, known(true), known(true)), colonist("cut", no, no, known(true)), colonist("gone", known(true), known(true), no), colonist("well", no, no, no)})
	d := EvaluateClockWindow(f, l)
	if !d.Admitted || d.Mode != ClockWindowColony || !reflect.DeepEqual(d.Downed, []PawnID{"abe", "zed"}) {
		t.Fatal(d)
	}
	// Combat mode acknowledges the casualty alongside the hostiles.
	l.CombatMaxTicks = 30
	f.CombatPlan = domain.Known(true)
	census([]EmergencyPawn{colonist("zed", no, known(true), no)}, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: no, Downed: no})
	if d := EvaluateClockWindow(f, l); !d.Admitted || d.Mode != ClockWindowCombat || !reflect.DeepEqual(d.Hostiles, []PawnID{"raider"}) || !reflect.DeepEqual(d.Downed, []PawnID{"zed"}) {
		t.Fatal(d)
	}
	// Unknown downed status holds the window rather than acknowledging.
	census([]EmergencyPawn{colonist("fog", no, domain.Unknown[bool](), no)})
	if d := EvaluateClockWindow(f, l); d.Admitted || len(d.Downed) != 0 {
		t.Fatal(d)
	}
	if d := EvaluateClockWindow(clockWindowFixture(t)); len(d.Downed) != 0 {
		t.Fatal(d)
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
		"combat budget":    {func(_ *ClockWindowFacts, l *ClockWindowLimits) { l.CombatMaxTicks = 101 }, ClockWindowInvalidLimits},
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

// Planner facts are bound by tick, not by MaxAge: facts from the admitted
// tick admit however old the planning step was, facts from another tick
// hold, and no planner facts at all leave the journal's work to admit.
func TestClockWindowFactsTickHoldsStalePlanning(t *testing.T) {
	f, limits := clockWindowFixture(t)
	f.FactsTick = domain.Known(f.Tick)
	if d := EvaluateClockWindow(f, limits); !d.Admitted {
		t.Fatal(d)
	}
	f.FactsTick = domain.Known(f.Tick - 1)
	if d := EvaluateClockWindow(f, limits); d.Admitted || !reflect.DeepEqual(d.Refused, []ClockWindowReason{ClockWindowStalePlanning}) {
		t.Fatal(d)
	}
	f.FactsTick = domain.Unknown[domain.Tick]()
	if d := EvaluateClockWindow(f, limits); !d.Admitted {
		t.Fatal(d)
	}
}

// A manhunter or hunting animal DistantThreatCells from every colonist admits
// an ordinary colony window: the native supervisor's radius stops it on
// approach. The same animal nearer, or a raider at any distance, refuses (#66).
func TestClockWindowDistantAnimalThreatAdmitsColonyWindow(t *testing.T) {
	f, limits := clockWindowFixture(t)
	threat := func(kind ThreatKind, animal bool, distance float64) EmergencyThreat {
		return EmergencyThreat{ID: "animal", Kind: kind, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(animal), Distance: domain.Known(distance)}
	}
	for _, c := range []struct {
		name     string
		threat   EmergencyThreat
		admitted bool
	}{
		{"manhunter far", threat(Hostile, true, DistantThreatCells), true},
		{"hunting far", threat(HuntingPredator, true, 120), true},
		{"manhunter near", threat(Hostile, true, 30), false},
		{"raider far", threat(Hostile, false, 200), false},
	} {
		t.Run(c.name, func(t *testing.T) {
			facts := EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, Threats: []EmergencyThreat{c.threat}}
			emergency, err := NewEmergencySnapshot(f.Current, 10, facts)
			if err != nil {
				t.Fatal(err)
			}
			g := f
			g.Emergency = emergency
			d := EvaluateClockWindow(g, limits)
			if d.Admitted != c.admitted || c.admitted && (d.Mode != ClockWindowColony || len(d.Hostiles) != 0) {
				t.Fatal(d)
			}
			if !c.admitted && !reflect.DeepEqual(d.Refused, []ClockWindowReason{ClockWindowUnsafe}) {
				t.Fatal(d)
			}
		})
	}
}

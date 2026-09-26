package startuplabor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func sample(pawn string, tick domain.Tick, job string) PawnSample {
	return PawnSample{Pawn: pawn, Tick: tick, Job: job, JobKnown: true}
}

func TestClassifySample(t *testing.T) {
	for _, tc := range []struct {
		name string
		in   PawnSample
		want Activity
	}{
		{"no job at all is idle", sample("a", 1, ""), ActivityIdle},
		{"waiting is idle", sample("a", 1, "Wait_Wander"), ActivityIdle},
		{"building is work", sample("a", 1, "FinishFrame"), ActivityWork},
		{"sleeping is excluded", sample("a", 1, "LayDown"), ActivitySleep},
		{"eating is excluded", sample("a", 1, "Ingest"), ActivityMeal},
		{"socialising is excluded", sample("a", 1, "SocialRelax"), ActivityRecreation},
		{"drafted is unavailable", PawnSample{Pawn: "a", Job: "Wait_Combat", JobKnown: true, Drafted: true}, ActivityUnavailable},
		{"downed in bed is medical rest", PawnSample{Pawn: "a", Job: "LayDown", JobKnown: true, Downed: true, InBed: true}, ActivityMedical},
		{"a mental break is unavailable", PawnSample{Pawn: "a", Job: "Wait_Wander", JobKnown: true, Mental: "Wander_Sad"}, ActivityUnavailable},
		{"an unread job is unknown", PawnSample{Pawn: "a"}, ActivityUnknown},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.in.Classify(); got != tc.want {
				t.Fatalf("activity = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestAccumulateCreditsIntervalsToTheirSample(t *testing.T) {
	a := Accumulate([]PawnSample{
		sample("a", 100, "Wait_Wander"),
		sample("a", 200, "FinishFrame"),
		sample("a", 300, "LayDown"),
		sample("a", 400, ""),
	}, 1000)
	if a.Idle != 100 || a.Work != 100 || a.Excluded != 100 {
		t.Fatalf("idle=%d work=%d excluded=%d", a.Idle, a.Work, a.Excluded)
	}
	// The last sample observed no end, so it credits nothing.
	if a.Idle+a.Work+a.Excluded+a.Unknown != 300 {
		t.Fatalf("credited %d ticks over a 300-tick observed span", a.Idle+a.Work+a.Excluded+a.Unknown)
	}
	if a.Samples != 4 || a.Pawns != 1 {
		t.Fatalf("samples=%d pawns=%d", a.Samples, a.Pawns)
	}
}

func TestAccumulateNeverFabricatesOrGoesNegative(t *testing.T) {
	a := Accumulate([]PawnSample{
		// A rewind: the second tick precedes the first.
		sample("a", 500, "Wait_Wander"),
		sample("a", 100, "Wait_Wander"),
		// A paused pair: the clock did not move.
		sample("a", 100, "Wait_Wander"),
		// A restart boundary: the interval spanning it is not credited.
		{Pawn: "a", Tick: 900, Job: "Wait_Wander", JobKnown: true, Restart: true},
		sample("a", 1000, "Wait_Wander"),
		// An unread job carries its own bucket, not idle.
		{Pawn: "a", Tick: 1100},
		sample("a", 1200, "Wait_Wander"),
	}, 1000)
	// Two fully observed idle intervals (900->1000, 1000->1100); the
	// rewind, the paused pair and the restart boundary credit nothing.
	if a.Idle != 200 {
		t.Fatalf("idle = %d, want the fully observed idle intervals only", a.Idle)
	}
	if a.Unknown != 100 {
		t.Fatalf("unknown = %d, want the unread interval", a.Unknown)
	}
	if a.Rewinds != 1 || a.Restarts != 1 {
		t.Fatalf("rewinds=%d restarts=%d", a.Rewinds, a.Restarts)
	}
	for name, v := range map[string]domain.Tick{"idle": a.Idle, "work": a.Work, "excluded": a.Excluded, "unknown": a.Unknown, "unattributed": a.Unattributed} {
		if v < 0 {
			t.Fatalf("%s is negative: %d", name, v)
		}
	}
}

func TestAccumulateBoundsLongGaps(t *testing.T) {
	a := Accumulate([]PawnSample{
		sample("a", 0, "Wait_Wander"),
		sample("a", 5000, "Wait_Wander"),
	}, 1000)
	if a.Idle != 1000 || a.Unattributed != 4000 {
		t.Fatalf("idle=%d unattributed=%d: the gap must not be assumed idle throughout", a.Idle, a.Unattributed)
	}
	if len(a.Gaps) != 1 || a.Gaps[0].From != 0 || a.Gaps[0].To != 5000 || a.Gaps[0].Unbounded != 4000 {
		t.Fatalf("gaps = %+v", a.Gaps)
	}
	if row := a.Row(); row["bounded_gaps"] == nil {
		t.Fatalf("row hides the gap: %v", row)
	}
}

func TestAccumulateSeparatesPawns(t *testing.T) {
	a := Accumulate([]PawnSample{
		sample("a", 0, "Wait_Wander"),
		sample("b", 0, "FinishFrame"),
		sample("a", 100, "Wait_Wander"),
		sample("b", 100, "FinishFrame"),
	}, 1000)
	if a.Idle != 100 || a.Work != 100 || a.Pawns != 2 {
		t.Fatalf("idle=%d work=%d pawns=%d", a.Idle, a.Work, a.Pawns)
	}
}

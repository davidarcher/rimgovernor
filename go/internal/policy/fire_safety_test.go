package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func eligibleFirefighter(pawn domain.PawnID) FireSafetyPawnFacts {
	return FireSafetyPawnFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		FirefightingEnabled: knownTrue()}
}

func TestEvaluateFireSafetyRecoveredWhenInactive(t *testing.T) {
	if got := EvaluateFireSafety(false, true, []FireSafetyPawnFacts{eligibleFirefighter("a")}); got != FireSafetyRecovered {
		t.Fatal(got)
	}
}

func TestEvaluateFireSafetyUnknownWhenUnobserved(t *testing.T) {
	if got := EvaluateFireSafety(true, false, nil); got != FireSafetyUnknown {
		t.Fatal(got)
	}
}

func TestEvaluateFireSafetyWaitsForNativeWithEligibleWorkerAndSafeFire(t *testing.T) {
	got := EvaluateFireSafety(true, true, []FireSafetyPawnFacts{eligibleFirefighter("a")})
	if got != FireSafetyWaitingForNative {
		t.Fatal(got)
	}
}

func TestEvaluateFireSafetyWaitsForNativeOnUnsafeFireWithEligibleWorker(t *testing.T) {
	got := EvaluateFireSafety(true, true, []FireSafetyPawnFacts{eligibleFirefighter("a")})
	if got != FireSafetyWaitingForNative {
		t.Fatal(got)
	}
}

func TestEvaluateFireSafetyBlockedWithoutEligibleWorker(t *testing.T) {
	if got := EvaluateFireSafety(true, true, nil); got != FireSafetyBlocked {
		t.Fatal(got)
	}
	base := eligibleFirefighter("a")
	for _, mutate := range []func(*FireSafetyPawnFacts){
		func(p *FireSafetyPawnFacts) { p.Dead = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.Downed = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.Drafted = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.MentalState = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.NeedsTend = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.Bleeding = knownTrue() },
		func(p *FireSafetyPawnFacts) { p.FirefightingEnabled = knownFalse() },
		func(p *FireSafetyPawnFacts) { p.FirefightingEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if got := EvaluateFireSafety(true, true, []FireSafetyPawnFacts{p}); got != FireSafetyBlocked {
			t.Fatal("ineligible firefighter treated as eligible", p, got)
		}
	}
}

func TestFireSafetyAllowsForcedWork(t *testing.T) {
	pawn := eligibleFirefighter("a")
	for _, forced := range []domain.Fact[bool]{domain.Known(true), domain.Unknown[bool]()} {
		pawn.PlayerForced = forced
		if got := EvaluateFireSafety(true, true, []FireSafetyPawnFacts{pawn}); got != FireSafetyWaitingForNative {
			t.Fatal(got)
		}
	}
}

package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func shrineDefender(id string, ranged bool, reach float64) ShrineDefenderFacts {
	return ShrineDefenderFacts{SquadDefenderFacts: SquadDefenderFacts{ID: domain.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known[uint32](0), ViolenceCapable: domain.Known(true), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), RangedEquipped: domain.Known(ranged), MeleeEquipped: domain.Known(!ranged), Armed: domain.Known(true)}, WeaponRange: domain.Known(reach)}
}

func shrineReadyRequest() ShrineReadinessRequest {
	shrine := AncientShrine{ID: "AncientShrineGroup_0", Sealed: true, BreachWalls: []ShrineBreachWall{
		{EntityID: "WallFar", Cell: domain.Cell{X: 40, Z: 10}, Outside: domain.Cell{X: 41, Z: 10}},
		{EntityID: "WallNear", Cell: domain.Cell{X: 20, Z: 10}, Outside: domain.Cell{X: 19, Z: 10}},
	}}
	traps := []domain.Cell{{X: 17, Z: 10}, {X: 17, Z: 11}, {X: 16, Z: 9}, {X: 60, Z: 60}}
	return ShrineReadinessRequest{Shrine: shrine, RaidPoints: domain.Known(250.0), Peaceful: domain.Unknown[bool](), Center: domain.Cell{X: 5, Z: 10}, Traps: traps,
		Squad: []ShrineDefenderFacts{shrineDefender("melee", false, 0), shrineDefender("rifle", true, 25.9), shrineDefender("bow", true, 24.9)}}
}

func TestShrineBreachReadinessReadyPicksNearestWallAndOrdersShooters(t *testing.T) {
	got := ShrineBreachReadiness(shrineReadyRequest())
	if !got.Ready || got.Reason != "" || got.Wall.EntityID != "WallNear" || got.Traps != 3 || len(got.Squad) != 3 || got.Squad[0] != "bow" || got.Squad[1] != "rifle" || got.Squad[2] != "melee" {
		t.Fatalf("%+v", got)
	}
}

func TestShrineBreachReadinessHolds(t *testing.T) {
	for name, tc := range map[string]struct {
		edit   func(*ShrineReadinessRequest)
		reason string
	}{
		"not sealed":     {func(r *ShrineReadinessRequest) { r.Shrine.Sealed = false }, ShrineHoldNotSealed},
		"no wall":        {func(r *ShrineReadinessRequest) { r.Shrine.BreachWalls = nil }, ShrineHoldNoBreachWall},
		"emergency":      {func(r *ShrineReadinessRequest) { r.Emergency = true }, ShrineHoldEmergency},
		"squad of one":   {func(r *ShrineReadinessRequest) { r.Squad = r.Squad[:1] }, ShrineHoldSquadTooSmall},
		"downed shooter": {func(r *ShrineReadinessRequest) { r.Squad[1].Downed = domain.Known(true); r.Squad = r.Squad[:2] }, ShrineHoldSquadTooSmall},
		"unarmed":        {func(r *ShrineReadinessRequest) { r.Squad[0].Armed = domain.Known(false); r.Squad = r.Squad[:2] }, ShrineHoldSquadTooSmall},
		"short ranged": {func(r *ShrineReadinessRequest) {
			r.Squad[1].WeaponRange, r.Squad[2].WeaponRange = domain.Known(12.0), domain.Unknown[float64]()
		}, ShrineHoldNoRanged},
		"traps far":      {func(r *ShrineReadinessRequest) { r.Traps = r.Traps[2:] }, ShrineHoldNoTraps},
		"threat unknown": {func(r *ShrineReadinessRequest) { r.RaidPoints = domain.Unknown[float64]() }, ShrineHoldThreatUnknown},
		"threat high":    {func(r *ShrineReadinessRequest) { r.RaidPoints = domain.Known(501.0) }, ShrineHoldThreatTooHigh},
	} {
		t.Run(name, func(t *testing.T) {
			r := shrineReadyRequest()
			tc.edit(&r)
			got := ShrineBreachReadiness(r)
			if got.Ready || got.Reason != tc.reason {
				t.Fatalf("%+v", got)
			}
		})
	}
}

func TestShrineBreachReadinessThreatStepsWithSquad(t *testing.T) {
	r := shrineReadyRequest()
	r.RaidPoints = domain.Known(500.0)
	if got := ShrineBreachReadiness(r); !got.Ready {
		t.Fatalf("three defenders hold 500: %+v", got)
	}
	r.Squad = append(r.Squad, shrineDefender("spear", false, 0))
	r.RaidPoints = domain.Known(800.0)
	if got := ShrineBreachReadiness(r); !got.Ready {
		t.Fatalf("four defenders hold 800: %+v", got)
	}
	r.RaidPoints = domain.Known(800.5)
	if got := ShrineBreachReadiness(r); got.Ready || got.Reason != ShrineHoldThreatTooHigh {
		t.Fatalf("%+v", got)
	}
	r.Squad = append(r.Squad, shrineDefender("club", false, 0), shrineDefender("axe", false, 0))
	if got := ShrineBreachReadiness(r); got.Ready {
		t.Fatalf("the ceiling stops at the last row: %+v", got)
	}
}

func TestShrineBreachReadinessPeacefulSoftensTheGate(t *testing.T) {
	r := shrineReadyRequest()
	r.Peaceful = domain.Known(true)
	r.Squad = r.Squad[1:2]
	r.Traps = nil
	r.RaidPoints = domain.Unknown[float64]()
	if got := ShrineBreachReadiness(r); !got.Ready || got.Traps != 0 || len(got.Squad) != 1 {
		t.Fatalf("%+v", got)
	}
	r.RaidPoints = domain.Known(5000.0)
	if got := ShrineBreachReadiness(r); !got.Ready {
		t.Fatalf("peaceful ignores raid points: %+v", got)
	}
	r.Squad[0].WeaponRange = domain.Known(10.0)
	if got := ShrineBreachReadiness(r); got.Ready || got.Reason != ShrineHoldNoRanged {
		t.Fatalf("a ranged weapon is still required: %+v", got)
	}
}

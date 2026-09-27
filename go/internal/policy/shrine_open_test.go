package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestShrineMeleeLockStaffsOneMeleeColonistPerFilledCasket(t *testing.T) {
	t.Parallel()
	caskets := []ShrineCasket{{EntityID: "b", HasContents: true}, {EntityID: "a", HasContents: true}}
	hurt := shrineDefender("hurt", false, 0)
	hurt.HealthFraction = domain.Known(0.5)
	pacifist := shrineDefender("pacifist", false, 0)
	pacifist.ViolenceCapable = domain.Known(false)
	squad := []ShrineDefenderFacts{shrineDefender("rifle", true, 25), hurt, pacifist, shrineDefender("club", false, 0), shrineDefender("spear", false, 0)}
	lock := ShrineMeleeLock(caskets, squad)
	if lock.Reason != "" || lock.Opener != "club" || lock.Casket != "a" || lock.Lockers["a"] != "club" || lock.Lockers["b"] != "spear" {
		t.Fatalf("%+v", lock)
	}
	short := ShrineMeleeLock(caskets, []ShrineDefenderFacts{shrineDefender("club", false, 0), shrineDefender("rifle", true, 25), pacifist})
	if short.Reason != CasketHoldLockUnderstaffed || len(short.Lockers) != 0 {
		t.Fatalf("%+v", short)
	}
	if none := ShrineMeleeLock(nil, squad); none.Reason != CasketHoldLockUnderstaffed {
		t.Fatalf("%+v", none)
	}
}

func TestOccupantDecision(t *testing.T) {
	t.Parallel()
	for name, tc := range map[string]struct {
		occupant ShrineOccupant
		room     bool
		want     string
	}{
		"corpse":           {ShrineOccupant{Dead: true, Hostile: true}, true, OccupantBury},
		"standing hostile": {ShrineOccupant{Hostile: true}, true, OccupantFight},
		"downed hostile":   {ShrineOccupant{Hostile: true, Downed: true}, true, OccupantCapture},
		"standing neutral": {ShrineOccupant{Faction: "Ancients"}, true, OccupantCapture},
		"custody full":     {ShrineOccupant{Hostile: true, Downed: true}, false, OccupantRelease},
		"already prisoner": {ShrineOccupant{Prisoner: true, Downed: true}, false, OccupantCaptured},
		"downed neutral":   {ShrineOccupant{Downed: true}, true, OccupantCapture},
	} {
		if got := OccupantDecision(tc.occupant, tc.room); got != tc.want {
			t.Errorf("%s: %s", name, got)
		}
	}
}

func TestShrineOpenReadinessGate(t *testing.T) {
	t.Parallel()
	ready := func() ShrineOpenRequest {
		return ShrineOpenRequest{Caskets: []ShrineCasket{{EntityID: "a", HasContents: true}, {EntityID: "b", HasContents: true}}, Squad: []ShrineDefenderFacts{shrineDefender("club", false, 0), shrineDefender("spear", false, 0), shrineDefender("rifle", true, 25)}, CustodyRoom: domain.Known(true), Medicine: domain.Known[int64](2), Doctors: domain.Known(1), RaidPoints: domain.Known(450.0)}
	}
	if got := ShrineOpenReadiness(ready()); got != CasketOpen {
		t.Fatal(got)
	}
	for name, tc := range map[string]struct {
		change func(*ShrineOpenRequest)
		want   string
	}{
		"understaffed": {func(r *ShrineOpenRequest) { r.Squad = r.Squad[1:] }, CasketHoldLockUnderstaffed},
		"no backup":    {func(r *ShrineOpenRequest) { r.Squad = r.Squad[:2] }, CasketHoldNoBackup},
		"injured":      {func(r *ShrineOpenRequest) { r.Squad[1].HealthFraction = domain.Known(0.7) }, CasketHoldLockInjured},
		"no bed":       {func(r *ShrineOpenRequest) { r.CustodyRoom = domain.Unknown[bool]() }, CasketHoldNoCustody},
		"medicine":     {func(r *ShrineOpenRequest) { r.Medicine = domain.Known[int64](1) }, CasketHoldNoMedicine},
		"doctor":       {func(r *ShrineOpenRequest) { r.Doctors = domain.Known(0) }, CasketHoldNoDoctor},
		"emergency":    {func(r *ShrineOpenRequest) { r.Emergency = true }, CasketHoldEmergency},
		"combat":       {func(r *ShrineOpenRequest) { r.Combat = true }, CasketHoldCombat},
		"threat":       {func(r *ShrineOpenRequest) { r.RaidPoints = domain.Known(600.0) }, CasketHoldThreatTooHigh},
		"unknown":      {func(r *ShrineOpenRequest) { r.RaidPoints = domain.Unknown[float64]() }, CasketHoldThreatUnknown},
	} {
		r := ready()
		tc.change(&r)
		if got := ShrineOpenReadiness(r); got != tc.want {
			t.Errorf("%s: %s", name, got)
		}
	}
}

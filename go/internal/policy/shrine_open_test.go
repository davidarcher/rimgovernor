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

func openCasketRequest(t *testing.T) OpenCasketRequest {
	t.Helper()
	open, _ := domain.NewOpenCasket("opener", "casket", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewOpenCasketAction("open", open)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := OpenCasketPawnFacts{Pawn: "opener", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(true), MentalState: domain.Known(false), ExistingJobDef: domain.Known("Wait_Combat")}
	return OpenCasketRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: OpenCasketFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, Casket: "casket", CasketSnapshotToken: "casket-cas", Exists: domain.Known(true), HasContents: domain.Known(true), NativeCanTry: domain.Known(true)}}
}

func TestEvaluateOpenCasketAdmitsADraftedOpenerAndHolds(t *testing.T) {
	t.Parallel()
	if d := EvaluateOpenCasket(openCasketRequest(t)); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
	for name, tc := range map[string]struct {
		change func(*OpenCasketRequest)
		reason Reason
	}{
		"downed":       {func(r *OpenCasketRequest) { r.Facts.Pawn.Downed = domain.Known(true) }, OpenerUnavailable},
		"already open": {func(r *OpenCasketRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("Open") }, OpenerUnavailable},
		"mental":       {func(r *OpenCasketRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }, PlayerOrder},
		"gone":         {func(r *OpenCasketRequest) { r.Facts.Exists = domain.Known(false) }, StructureIneligible},
		"emptied":      {func(r *OpenCasketRequest) { r.Facts.HasContents = domain.Known(false) }, StructureIneligible},
		"native":       {func(r *OpenCasketRequest) { r.Facts.NativeCanTry = domain.Known(false) }, NativeIneligible},
		"unknown":      {func(r *OpenCasketRequest) { r.Facts.HasContents = domain.Unknown[bool]() }, UnknownFacts},
		"stale":        {func(r *OpenCasketRequest) { r.MinimumTick = 14 }, StaleFacts},
		"wrong casket": {func(r *OpenCasketRequest) { r.Facts.Casket = "other" }, UnknownFacts},
	} {
		r := openCasketRequest(t)
		tc.change(&r)
		d := EvaluateOpenCasket(r)
		if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != tc.reason {
			t.Errorf("%s: %+v", name, d)
		}
	}
}

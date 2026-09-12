package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func sleepingFixture(using bool) SleepingObservation {
	b := SleepingBed{ID: "bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), RestEffectiveness: domain.Known(1.0), Temperature: domain.Known(20.0), Owners: []PawnID{"pawn"}, AccessibleTo: []PawnID{"pawn"}}
	if using {
		b.Users = []PawnID{"pawn"}
	}
	return SleepingObservation{Colonists: 1, People: []SleepingPerson{{ID: "pawn", OwnedBed: domain.Known("bed"), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0)}}, Beds: []SleepingBed{b}}
}

func TestSleepingRequiresExactOwnedSuitableBedUse(t *testing.T) {
	v := sleepingFixture(false)
	r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
	rows, known := r.Targets.Value()
	if err != nil || !known || len(rows) != 1 || rows[0].Kind != "use" {
		t.Fatal(r, err)
	}
	v.Beds[0].Users = []PawnID{"pawn"}
	r, err = ReviewSleeping(domain.Known(v), r.History, 2)
	if recovered, known := r.Recovered().Value(); err != nil || !known || !recovered || len(r.History.Uses) != 1 {
		t.Fatal(r, err)
	}
	v.Beds[0].Users = nil
	r, err = ReviewSleeping(domain.Known(v), r.History, 3)
	if recovered, _ := r.Recovered().Value(); err != nil || !recovered {
		t.Fatal(r, err)
	}
	v.Beds[0].Temperature = domain.Known(31.0)
	r, err = ReviewSleeping(domain.Known(v), r.History, 4)
	rows, _ = r.Targets.Value()
	if err != nil || len(rows) != 1 || rows[0].Kind != "unsafe" {
		t.Fatal(r, err)
	}
	v.Beds[0].Temperature = domain.Known(30.0)
	r, err = ReviewSleeping(domain.Known(v), r.History, 5)
	if recovered, _ := r.Recovered().Value(); err != nil || !recovered {
		t.Fatal(r, err)
	}
	v.Beds[0].ID = "replacement"
	v.People[0].OwnedBed = domain.Known("replacement")
	r, err = ReviewSleeping(domain.Known(v), r.History, 6)
	rows, _ = r.Targets.Value()
	if err != nil || len(rows) != 1 || rows[0].Kind != "use" {
		t.Fatal("replacement inherited use", r, err)
	}
}

func TestSleepingUpgradeOwnershipAndUnknownCensus(t *testing.T) {
	for _, kind := range []string{"floor", "unowned", "inaccessible", "medical", "prisoner", "unroofed"} {
		v := sleepingFixture(true)
		switch kind {
		case "floor":
			v.Beds[0].Definition = "SleepingSpot"
		case "unowned":
			v.Beds[0].Owners = nil
		case "inaccessible":
			v.Beds[0].AccessibleTo = nil
		case "medical":
			v.Beds[0].Medical = domain.Known(true)
		case "prisoner":
			v.Beds[0].Prisoners = domain.Known(true)
		case "unroofed":
			v.Beds[0].Roofed = domain.Known(false)
		}
		r, err := ReviewSleeping(domain.Known(v), SleepingHistory{}, 1)
		rows, known := r.Targets.Value()
		want := SleepingUnsafe
		if kind == "floor" || kind == "unowned" {
			want = SleepingUpgrade
		}
		if err != nil || !known || len(rows) != 1 || rows[0].Kind != want || len(r.History.Uses) != 0 {
			t.Fatal(kind, r, err)
		}
	}
	history := SleepingHistory{Uses: []SleepingUse{{"pawn", "bed", 1}}}
	for _, v := range []SleepingObservation{{Colonists: 1}, func() SleepingObservation {
		v := sleepingFixture(false)
		v.People[0].OwnedBed = domain.Unknown[string]()
		return v
	}()} {
		r, err := ReviewSleeping(domain.Known(v), history, 2)
		if _, known := r.Targets.Value(); err != nil || known || len(r.History.Uses) != 1 {
			t.Fatal(r, err)
		}
	}
	if _, err := ReviewSleeping(domain.Unknown[SleepingObservation](), history, 0); err == nil {
		t.Fatal("future use accepted")
	}
}

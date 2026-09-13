package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func containmentAnimal(id PawnID, requiresPen, contained bool, suitablePen string) UpkeepAnimal {
	return UpkeepAnimal{ID: id, Definition: "Muffalo", RequiresPen: domain.Known(requiresPen),
		Contained: domain.Known(contained), Release: domain.Known(false), Slaughter: domain.Known(false),
		SuitablePen: domain.Known(suitablePen)}
}

func TestSelectAnimalContainmentMethodNoDeficit(t *testing.T) {
	for _, animals := range [][]UpkeepAnimal{
		nil,
		{containmentAnimal("muffalo-1", false, false, "")},
		{containmentAnimal("muffalo-1", true, true, "")},
		{{ID: "muffalo-1", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Known(false), Release: domain.Known(true), Slaughter: domain.Known(false)}},
	} {
		m, err := SelectAnimalContainmentMethod(animals, domain.Known(true), ContainmentShellNone, false)
		if err != nil || m.Reason != ContainmentNoDeficit || len(m.Animals) != 0 {
			t.Fatalf("%+v: %+v %v", animals, m, err)
		}
	}
}

func TestSelectAnimalContainmentMethodAllSuitableWaitsForHandlerThenNative(t *testing.T) {
	animals := []UpkeepAnimal{containmentAnimal("muffalo-1", true, false, "pen-1")}
	m, err := SelectAnimalContainmentMethod(animals, domain.Known(false), ContainmentShellNone, false)
	if err != nil || m.Reason != ContainmentWaitingHandler || len(m.Animals) != 1 || m.Animals[0] != "muffalo-1" {
		t.Fatalf("%+v %v", m, err)
	}
	m, err = SelectAnimalContainmentMethod(animals, domain.Known(true), ContainmentShellNone, false)
	if err != nil || m.Reason != ContainmentWaitingNativePen {
		t.Fatalf("%+v %v", m, err)
	}
	if _, err := SelectAnimalContainmentMethod(animals, domain.Unknown[bool](), ContainmentShellNone, false); err == nil {
		t.Fatal("unknown handler availability accepted")
	}
}

func TestSelectAnimalContainmentMethodBoundedHerdAndShellStaging(t *testing.T) {
	var large []UpkeepAnimal
	for i := 0; i < 9; i++ {
		large = append(large, containmentAnimal(PawnID("muffalo-"+string(rune('0'+i))), true, false, ""))
	}
	m, err := SelectAnimalContainmentMethod(large, domain.Known(true), ContainmentShellNone, false)
	if err != nil || m.Reason != ContainmentExceedsBound || len(m.Animals) != 9 {
		t.Fatalf("%+v %v", m, err)
	}

	small := large[:8]
	m, err = SelectAnimalContainmentMethod(small, domain.Known(true), ContainmentShellNone, false)
	if err != nil || m.Reason != ContainmentBuildShell || len(m.Animals) != 8 {
		t.Fatalf("%+v %v", m, err)
	}
	m, err = SelectAnimalContainmentMethod(small, domain.Known(true), ContainmentShellPending, false)
	if err != nil || m.Reason != ContainmentAwaitingShell {
		t.Fatalf("%+v %v", m, err)
	}
	m, err = SelectAnimalContainmentMethod(small, domain.Known(true), ContainmentShellComplete, false)
	if err != nil || m.Reason != ContainmentPlaceMarker {
		t.Fatalf("%+v %v", m, err)
	}
	m, err = SelectAnimalContainmentMethod(small, domain.Known(true), ContainmentShellComplete, true)
	if err != nil || m.Reason != ContainmentMarkerExhausted {
		t.Fatalf("%+v %v", m, err)
	}
}

func TestSelectAnimalContainmentMethodInvalidOrUnknownFacts(t *testing.T) {
	unknownPen := UpkeepAnimal{ID: "muffalo-1", Definition: "Muffalo", RequiresPen: domain.Unknown[bool]()}
	if _, err := SelectAnimalContainmentMethod([]UpkeepAnimal{unknownPen}, domain.Known(true), ContainmentShellNone, false); err == nil {
		t.Fatal("unknown pen requirement accepted")
	}
	unknownState := UpkeepAnimal{ID: "muffalo-1", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Unknown[bool](), Release: domain.Known(false), Slaughter: domain.Known(false)}
	if _, err := SelectAnimalContainmentMethod([]UpkeepAnimal{unknownState}, domain.Known(true), ContainmentShellNone, false); err == nil {
		t.Fatal("unknown containment state accepted")
	}
	dup := containmentAnimal("muffalo-1", true, false, "")
	if _, err := SelectAnimalContainmentMethod([]UpkeepAnimal{dup, dup}, domain.Known(true), ContainmentShellNone, false); err == nil {
		t.Fatal("duplicate animal accepted")
	}
	if _, err := SelectAnimalContainmentMethod([]UpkeepAnimal{{ID: "  ", Definition: "Muffalo", RequiresPen: domain.Known(true), Contained: domain.Known(false), Release: domain.Known(false), Slaughter: domain.Known(false)}}, domain.Known(true), ContainmentShellNone, false); err == nil {
		t.Fatal("invalid animal id accepted")
	}
}

func TestSelectAnimalContainmentMethodSortsBoundedRows(t *testing.T) {
	animals := []UpkeepAnimal{
		containmentAnimal("muffalo-2", true, false, ""),
		containmentAnimal("muffalo-1", true, false, ""),
	}
	m, err := SelectAnimalContainmentMethod(animals, domain.Known(true), ContainmentShellNone, false)
	if err != nil || len(m.Animals) != 2 || m.Animals[0] != "muffalo-1" || m.Animals[1] != "muffalo-2" {
		t.Fatalf("%+v %v", m, err)
	}
}

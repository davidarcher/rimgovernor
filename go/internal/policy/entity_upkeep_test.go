package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func door(x, z int32, open, hold, breached, blocked bool) ContainmentDoor {
	return ContainmentDoor{Cell: domain.Cell{X: x, Z: z}, Open: domain.Known(open), HoldOpen: domain.Known(hold), Breached: domain.Known(breached), BlockedOpen: domain.Known(blocked)}
}

func holding(pawn string, doors ...ContainmentDoor) ContainmentPlanning {
	p := planning(0, 0)
	p.Holders = domain.Known([]BuiltHolder{{Strength: 300, Available: false, HeldPawn: pawn, Doors: domain.Known(doors)}})
	return p
}

func TestHeldOpenCellDoorIsClosed(t *testing.T) {
	got := ContainmentDoorUpkeep(holding("e1", door(9, 4, true, true, true, false), door(3, 7, true, true, false, false), door(1, 1, false, false, false, false)))
	if len(got.CloseDoors) != 2 || got.CloseDoors[0] != (domain.Cell{X: 9, Z: 4}) || got.CloseDoors[1] != (domain.Cell{X: 3, Z: 7}) || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	if cell, ok := ContainmentDoorTarget(holding("e1", door(9, 4, true, true, true, false))); !ok || cell != (domain.Cell{X: 9, Z: 4}) {
		t.Fatalf("target %v %v", cell, ok)
	}
	if !entityDoorOwed(holding("e1", door(9, 4, true, true, false, false))) {
		t.Fatal("a held-open door owes the work")
	}
}

func TestOpenDoorNotHeldClosesByItself(t *testing.T) {
	// Open but not held and not blocked: the door closes after its delay, no
	// order is given and nothing is reported.
	got := ContainmentDoorUpkeep(holding("e1", door(9, 4, true, false, false, false)))
	if len(got.CloseDoors) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	got = ContainmentDoorUpkeep(holding("e1", door(9, 4, true, false, true, false)))
	if len(got.CloseDoors) != 0 || len(got.Issues) != 0 {
		t.Fatalf("a breached door that is neither held nor blocked: %+v", got)
	}
}

func TestBreachedDoorBlockedOpenIsReportedNotOrdered(t *testing.T) {
	got := ContainmentDoorUpkeep(holding("e1", door(9, 4, true, false, true, true)))
	if len(got.CloseDoors) != 0 || len(got.Issues) != 1 || !strings.Contains(got.Issues[0].Reason, "blocked open") || got.Issues[0].Cell != (domain.Cell{X: 9, Z: 4}) {
		t.Fatalf("%+v", got)
	}
	// A blocked doorway that is not yet breached is nothing to report.
	if got := ContainmentDoorUpkeep(holding("e1", door(9, 4, true, false, false, true))); len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
}

func TestEmptyPlatformKeepsNoDoors(t *testing.T) {
	if got := ContainmentDoorUpkeep(holding("", door(9, 4, true, true, true, false))); len(got.CloseDoors) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
}

func TestUnreadDoorFactsAreLoud(t *testing.T) {
	p := planning(0, 0)
	p.Holders = domain.Known([]BuiltHolder{{Strength: 300, HeldPawn: "e1", Doors: domain.Unknown[[]ContainmentDoor]()}})
	got := ContainmentDoorUpkeep(p)
	if len(got.CloseDoors) != 0 || len(got.Issues) != 1 || !strings.Contains(got.Issues[0].Reason, "unread") {
		t.Fatalf("%+v", got)
	}
	unread := door(9, 4, true, true, true, false)
	unread.HoldOpen = domain.Unknown[bool]()
	if got := ContainmentDoorUpkeep(holding("e1", unread)); len(got.CloseDoors) != 0 || len(got.Issues) != 1 {
		t.Fatalf("hold unread: %+v", got)
	}
	unread = door(9, 4, true, false, true, true)
	unread.BlockedOpen = domain.Unknown[bool]()
	if got := ContainmentDoorUpkeep(holding("e1", unread)); len(got.CloseDoors) != 0 || len(got.Issues) != 1 {
		t.Fatalf("blocked unread on a breached door: %+v", got)
	}
	unread = door(9, 4, true, false, true, true)
	unread.Breached = domain.Unknown[bool]()
	if got := ContainmentDoorUpkeep(holding("e1", unread)); len(got.CloseDoors) != 0 || len(got.Issues) != 1 {
		t.Fatalf("breached unread: %+v", got)
	}
	// Unknown holders yield nothing to act on.
	p.Holders = domain.Unknown[[]BuiltHolder]()
	if got := ContainmentDoorUpkeep(p); len(got.CloseDoors) != 0 || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
}

func tendEntity(id domain.PawnID, downed, needsTend, bleeding bool) CapturableEntity {
	return CapturableEntity{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), CanBeCaptured: domain.Known(true), Held: domain.Known(true), Need: domain.Known(100.0),
		NeedsTend: domain.Known(needsTend), Bleeding: domain.Known(bleeding)}
}

func TestHeldEntityNeedingTendIsTended(t *testing.T) {
	p := planning(0, 0)
	p.Entities = domain.Known([]CapturableEntity{tendEntity("e3", true, false, false), tendEntity("e2", true, true, false), tendEntity("e1", true, true, true), tendEntity("e0", true, true, true)})
	if pawn, ok := EntityTendTarget(p); !ok || pawn != "e0" {
		t.Fatalf("a bleeding entity is tended first, then by id: %q %v", pawn, ok)
	}
	if !entityTendOwed(p) {
		t.Fatal("tend not owed")
	}
}

func populationNeed(t *testing.T, f RoutineFacts) domain.Finding {
	t.Helper()
	needs, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range needs.Assessments {
		if a.ID == MaintainPopulation {
			return a.Finding
		}
	}
	t.Fatal("no population assessment")
	return ""
}

// TestUpkeepOwedKeepsPopulationInDeficit (#1743): a held-open cell door or a
// held entity that needs tending keeps MaintainPopulation open.
func TestUpkeepOwedKeepsPopulationInDeficit(t *testing.T) {
	quiet := stableRoutine()
	base := populationNeed(t, quiet)
	if base == domain.FindingUnmet {
		t.Fatal("the stable routine already has a population deficit")
	}
	doors := stableRoutine()
	doors.Containment = holding("e1", door(9, 4, true, true, false, false))
	if got := populationNeed(t, doors); got != domain.FindingUnmet {
		t.Fatalf("held-open door: %v", got)
	}
	tend := stableRoutine()
	tend.Containment = planning(0, 0)
	tend.Containment.Entities = domain.Known([]CapturableEntity{tendEntity("e1", true, true, true)})
	if got := populationNeed(t, tend); got != domain.FindingUnmet {
		t.Fatalf("tend: %v", got)
	}
}

func TestEntityTendSkipsWhatItCannotOrder(t *testing.T) {
	p := planning(0, 0)
	notHeld := tendEntity("a", true, true, true)
	notHeld.Held = domain.Known(false)
	dead := tendEntity("b", true, true, true)
	dead.Dead = domain.Known(true)
	standing := tendEntity("c", false, true, true)
	unreadNeeds := tendEntity("d", true, true, true)
	unreadNeeds.NeedsTend = domain.Unknown[bool]()
	unreadDowned := tendEntity("e", true, true, true)
	unreadDowned.Downed = domain.Unknown[bool]()
	p.Entities = domain.Known([]CapturableEntity{notHeld, dead, standing, unreadNeeds, unreadDowned})
	if pawn, ok := EntityTendTarget(p); ok {
		t.Fatalf("tend owed for %q", pawn)
	}
	p.Entities = domain.Unknown[[]CapturableEntity]()
	if entityTendOwed(p) {
		t.Fatal("tend owed without entity facts")
	}
}

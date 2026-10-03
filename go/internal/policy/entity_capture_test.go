package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// downedEntity is a recorded-style downed, living, capturable, unheld entity
// needing the given strength.
func downedEntity(id domain.PawnID, need float64) CapturableEntity {
	return CapturableEntity{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(true), CanBeCaptured: domain.Known(true), Held: domain.Known(false), Need: domain.Known(need)}
}

// capturePlanning has the platformDefs door (300 hit points, factor 1: a
// margin of 60) and one available platform of the given native strength.
func capturePlanning(strength float64, entities ...CapturableEntity) ContainmentPlanning {
	p := planning(1, 0)
	p.Holders = domain.Known([]BuiltHolder{{Strength: strength, Available: true}})
	p.Entities = domain.Known(entities)
	return p
}

func soleVerdict(t *testing.T, p ContainmentPlanning) EntityVerdict {
	t.Helper()
	v := EntityVerdicts(p)
	if len(v) != 1 {
		t.Fatalf("verdicts %+v", v)
	}
	return v[0]
}

func TestCaptureMarginIsTheDoorTerm(t *testing.T) {
	d := platformDefs()
	if m, err := CaptureMargin(d); err != nil || m != 60 {
		t.Fatalf("margin %v %v", m, err)
	}
	// The holder's factor scales the door term as it does in the formula.
	d.HolderFactor = 0.7
	if m, _ := CaptureMargin(d); m < 41.99 || m > 42.01 {
		t.Fatalf("a holding spot's margin %v", m)
	}
	d.DoorHP = 0
	if _, err := CaptureMargin(d); err == nil {
		t.Fatal("a margin from a door without hit points")
	}
}

func TestEntityIsCapturedWhenTheCellIsStrongEnoughWithMargin(t *testing.T) {
	// Needs 100, margin 60: a platform at exactly 160 holds it through a
	// door lapse.
	if v := soleVerdict(t, capturePlanning(160, downedEntity("e1", 100))); v.Decision != EntityCapture || v.Reason != "" {
		t.Fatalf("%+v", v)
	}
	if pawn, ok := EntityCaptureTarget(capturePlanning(300, downedEntity("e2", 100), downedEntity("e1", 100))); !ok || pawn != "e1" {
		t.Fatalf("target %q %v", pawn, ok)
	}
}

func TestEntityIsKilledWhenTheCellFallsShortOfTheMargin(t *testing.T) {
	// 159 reaches the need (100) but not the need plus the margin.
	v := soleVerdict(t, capturePlanning(159, downedEntity("e1", 100)))
	if v.Decision != EntityKill || !strings.Contains(v.Reason, "needs 100.0 plus a margin of 60.0") {
		t.Fatalf("%+v", v)
	}
	// No available platform holds nothing: the rest are killed.
	p := capturePlanning(500, downedEntity("e1", 1))
	p.Holders = domain.Known([]BuiltHolder{{Strength: 500, Available: false}})
	if v := soleVerdict(t, p); v.Decision != EntityKill || !strings.Contains(v.Reason, "no holding platform is available") {
		t.Fatalf("%+v", v)
	}
	p.Holders = domain.Known([]BuiltHolder{})
	if v := soleVerdict(t, p); v.Decision != EntityKill {
		t.Fatalf("%+v", v)
	}
	// An entity the game does not let the colony capture is the rest too.
	e := downedEntity("e1", 1)
	e.CanBeCaptured = domain.Known(false)
	if v := soleVerdict(t, capturePlanning(500, e)); v.Decision != EntityKill || !strings.Contains(v.Reason, "does not let") {
		t.Fatalf("%+v", v)
	}
	// The strongest available platform is the one that counts.
	p = capturePlanning(10, downedEntity("e1", 100))
	p.Holders = domain.Known([]BuiltHolder{{Strength: 10, Available: true}, {Strength: 200, Available: true}, {Strength: 900, Available: false}})
	if v := soleVerdict(t, p); v.Decision != EntityCapture {
		t.Fatalf("%+v", v)
	}
}

func TestEntityRulesRefuseLoudlyWithoutFacts(t *testing.T) {
	unknownNeed := downedEntity("e1", 0)
	unknownNeed.Need = domain.Unknown[float64]()
	unknownCapturable := downedEntity("e1", 0)
	unknownCapturable.CanBeCaptured = domain.Unknown[bool]()
	unknownDowned := downedEntity("e1", 0)
	unknownDowned.Downed = domain.Unknown[bool]()
	unknownHeld := downedEntity("e1", 0)
	unknownHeld.Held = domain.Unknown[bool]()
	unknownDead := downedEntity("e1", 0)
	unknownDead.Dead = domain.Unknown[bool]()
	noHolders := capturePlanning(500, downedEntity("e1", 1))
	noHolders.Holders = domain.Unknown[[]BuiltHolder]()
	noDefs := capturePlanning(500, downedEntity("e1", 1))
	noDefs.Defs, noDefs.DefsReason = domain.Unknown[ContainmentDefs](), "no door stat"
	noDoor := capturePlanning(500, downedEntity("e1", 1))
	badDoor := platformDefs()
	badDoor.DoorHP = 0
	noDoor.Defs = domain.Known(badDoor)
	for name, c := range map[string]struct {
		p      ContainmentPlanning
		reason string
	}{
		"need":          {capturePlanning(500, unknownNeed), "strength the entity needs is unread"},
		"capturable":    {capturePlanning(500, unknownCapturable), "lets the colony capture"},
		"downed":        {capturePlanning(500, unknownDowned), "downed is unread"},
		"held":          {capturePlanning(500, unknownHeld), "holds the entity is unread"},
		"dead":          {capturePlanning(500, unknownDead), "dead is unread"},
		"holders":       {noHolders, "platforms are unread"},
		"defs":          {noDefs, "no door stat"},
		"door hit pts":  {noDoor, "no hit points"},
		"strength gone": {capturePlanning(500, unknownNeed), "unread"},
	} {
		v := soleVerdict(t, c.p)
		if v.Decision != EntityRefuse || !strings.Contains(v.Reason, c.reason) {
			t.Fatalf("%s: %+v", name, v)
		}
		if _, ok := EntityCaptureTarget(c.p); ok {
			t.Fatalf("%s: captured without the facts", name)
		}
	}
	if v := EntityVerdicts(ContainmentPlanning{Entities: domain.Unknown[[]CapturableEntity]()}); v != nil {
		t.Fatalf("unread entities decide nothing: %+v", v)
	}
}

func TestEntityRulesLeaveStandingHeldAndDeadEntitiesAlone(t *testing.T) {
	standing := downedEntity("a", 1)
	standing.Downed = domain.Known(false)
	held := downedEntity("b", 1)
	held.Held = domain.Known(true)
	dead := downedEntity("c", 1)
	dead.Dead = domain.Known(true)
	if v := EntityVerdicts(capturePlanning(500, standing, held, dead)); len(v) != 0 {
		t.Fatalf("%+v", v)
	}
}

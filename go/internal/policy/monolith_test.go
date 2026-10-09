package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func monolithAt(level int32, next string, can bool) MonolithFacts {
	return MonolithFacts{Spawned: domain.Known(true), AmbientHorror: domain.Known(false), Level: domain.Known(level), ID: domain.Known("Thing_VoidMonolith1"),
		CanActivate: domain.Known(can), NextLevel: domain.Known(next), CodexShortfall: domain.Known(uint32(0)), Gleaming: domain.Known(false)}
}

// strongGate is a colony that meets the awakening gate: capacity 1.25 times
// the raid points, no threat and every entity held.
func strongGate() AwakenGate {
	cell := heldEntity("e1", domain.Known(false))
	cell.Escaping = domain.Known(false)
	return AwakenGate{Stage: StageStable, Capacity: domain.Known(250.0), RaidPoints: domain.Known(200.0), Hostiles: domain.Known(int64(0)),
		Containment: ContainmentPlanning{Entities: domain.Known([]CapturableEntity{cell}), Holders: domain.Known([]BuiltHolder{})}}
}

func TestInactiveMonolithIsInvestigatedAndEarlierLevelsActivatedOnceAllowed(t *testing.T) {
	got := MonolithAdvanceOwed(domain.Known(monolithAt(0, "Stirring", true)), AwakenGate{Stage: StageStable})
	if got.Order != MonolithInvestigate || got.Monolith != "Thing_VoidMonolith1" || got.Awaken || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	got = MonolithAdvanceOwed(domain.Known(monolithAt(1, "Waking", true)), AwakenGate{Stage: StageStable})
	if got.Order != MonolithActivate || got.Awaken || got.Level != 1 {
		t.Fatalf("the Stirring level is activated without the awakening gate: %+v", got)
	}
}

// The game's CanActivate carries the codex requirement and the blocking
// conditions: while it says no, nothing is owed and the reason is named.
func TestMonolithHoldsWhileTheGameDoesNotAllowActivation(t *testing.T) {
	f := monolithAt(1, "Waking", false)
	f.CodexShortfall = domain.Known(uint32(3))
	got := MonolithAdvanceOwed(domain.Known(f), strongGate())
	if got.Order != "" || !strings.Contains(got.Hold, "short 3") || len(got.Issues) != 0 {
		t.Fatalf("%+v", got)
	}
	f.CodexShortfall = domain.Known(uint32(0))
	f.Blocking = []string{"UnnaturalDarkness"}
	if got := MonolithAdvanceOwed(domain.Known(f), strongGate()); got.Order != "" || !strings.Contains(got.Hold, "UnnaturalDarkness") {
		t.Fatalf("%+v", got)
	}
}

func TestMonolithIsInertInAmbientHorrorAndWithoutAMonolith(t *testing.T) {
	f := monolithAt(0, "Stirring", true)
	f.AmbientHorror = domain.Known(true)
	if got := MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable}); got.Order != "" || got.Hold != "" || len(got.Issues) != 0 {
		t.Fatalf("Ambient Horror: %+v", got)
	}
	if MonolithInPlay(domain.Known(f)) {
		t.Fatal("Ambient Horror runs no questline")
	}
	f = monolithAt(0, "Stirring", true)
	f.Spawned = domain.Known(false)
	if got := MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable}); got.Order != "" || len(got.Issues) != 0 {
		t.Fatalf("no monolith: %+v", got)
	}
	if got := MonolithAdvanceOwed(domain.Unknown[MonolithFacts](), AwakenGate{Stage: StageStable}); got.Order != "" || len(got.Issues) != 0 {
		t.Fatalf("no Anomaly: %+v", got)
	}
}

func TestMonolithUnreadFactsHoldTheOrderLoudly(t *testing.T) {
	f := monolithAt(1, "Waking", true)
	f.ID = domain.Unknown[string]()
	f.AmbientHorror = domain.Known(false)
	if got := MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable}); got.Order != "" || len(got.Issues) != 1 {
		t.Fatalf("unread id: %+v", got)
	}
	f = monolithAt(1, "Waking", true)
	f.AmbientHorror = domain.Unknown[bool]()
	if got := MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable}); got.Order != "" || len(got.Issues) != 1 {
		t.Fatalf("unread mode: %+v", got)
	}
	f = monolithAt(1, "Waking", true)
	f.NextLevel = domain.Unknown[string]()
	if got := MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable}); got.Order != "" || len(got.Issues) != 1 {
		t.Fatalf("unread next level: %+v", got)
	}
}

func TestAwakeningRunsOnlyWhenTheColonyIsStrongAndStable(t *testing.T) {
	awaken := domain.Known(monolithAt(2, MonolithLevelVoidAwakened, true))
	got := MonolithAdvanceOwed(awaken, strongGate())
	if got.Order != MonolithActivate || !got.Awaken || got.Hold != "" || len(got.Issues) != 0 {
		t.Fatalf("strong and stable: %+v", got)
	}

	weak := strongGate()
	weak.Capacity = domain.Known(249.0)
	if got := MonolithAdvanceOwed(awaken, weak); got.Order != "" || !got.Awaken || !strings.Contains(got.Hold, "defense capacity") {
		t.Fatalf("below 1.25 times the raid points: %+v", got)
	}
	exact := strongGate()
	exact.Capacity = domain.Known(AwakenStrengthFactor * 200)
	if got := MonolithAdvanceOwed(awaken, exact); got.Order != MonolithActivate {
		t.Fatalf("the factor itself is enough: %+v", got)
	}

	threat := strongGate()
	threat.Hostiles = domain.Known(int64(2))
	if got := MonolithAdvanceOwed(awaken, threat); got.Order != "" || !strings.Contains(got.Hold, "threats") {
		t.Fatalf("a standing threat: %+v", got)
	}

	escaping := strongGate()
	e := heldEntity("e1", domain.Known(false))
	e.Escaping = domain.Known(true)
	escaping.Containment.Entities = domain.Known([]CapturableEntity{e})
	if got := MonolithAdvanceOwed(awaken, escaping); got.Order != "" || !strings.Contains(got.Hold, "escaping") {
		t.Fatalf("an escaping entity: %+v", got)
	}

	free := strongGate()
	e = heldEntity("e2", domain.Known(false))
	e.Held = domain.Known(false)
	free.Containment.Entities = domain.Known([]CapturableEntity{e})
	if got := MonolithAdvanceOwed(awaken, free); got.Order != "" || !strings.Contains(got.Hold, "free") {
		t.Fatalf("a free entity: %+v", got)
	}

	open := strongGate()
	open.Containment.Holders = domain.Known([]BuiltHolder{{HeldPawn: "e1", Doors: domain.Known([]ContainmentDoor{{Cell: domain.Cell{X: 1, Z: 1}, HoldOpen: domain.Known(true), Breached: domain.Known(false), BlockedOpen: domain.Known(false), Open: domain.Known(true)}})}})
	if got := MonolithAdvanceOwed(awaken, open); got.Order != "" || !strings.Contains(got.Hold, "door") {
		t.Fatalf("a door held open: %+v", got)
	}
}

func TestAwakeningUnknownStrengthIsAnIssueNotAGuess(t *testing.T) {
	awaken := domain.Known(monolithAt(2, MonolithLevelVoidAwakened, true))
	for name, change := range map[string]func(*AwakenGate){
		"capacity": func(g *AwakenGate) { g.Capacity = domain.Unknown[float64]() },
		"points":   func(g *AwakenGate) { g.RaidPoints = domain.Unknown[float64]() },
		"hostiles": func(g *AwakenGate) { g.Hostiles = domain.Unknown[int64]() },
		"entities": func(g *AwakenGate) { g.Containment.Entities = domain.Unknown[[]CapturableEntity]() },
		"escape": func(g *AwakenGate) {
			e := heldEntity("e1", domain.Known(false))
			g.Containment.Entities = domain.Known([]CapturableEntity{e})
		},
	} {
		g := strongGate()
		change(&g)
		if got := MonolithAdvanceOwed(awaken, g); got.Order != "" || len(got.Issues) == 0 {
			t.Fatalf("%s: %+v", name, got)
		}
	}
}

func TestMonolithAdvanceIsAPopulationDeficit(t *testing.T) {
	f := RoundsFacts{Monolith: domain.Known(monolithAt(0, "Stirring", true))}
	if !monolithAdvanceOwed(f, StageStable) {
		t.Fatal("an allowed investigation is a standing work")
	}
	f.Monolith = domain.Known(monolithAt(1, "Waking", false))
	if monolithAdvanceOwed(f, StageStable) {
		t.Fatal("a held advance is no deficit")
	}
}

// The awakening quest is walked in the game's order (#2438): void structures,
// the Gleaming monolith, then the void node touched by the colonist skipped
// there; nothing is owed outside the quest.
func TestVoidAwakeningQuestIsWalkedStageByStage(t *testing.T) {
	quest := func(change func(*MonolithFacts)) MonolithAdvance {
		f := monolithAt(3, "", false)
		change(&f)
		return MonolithAdvanceOwed(domain.Known(f), AwakenGate{Stage: StageStable})
	}
	if got := quest(func(*MonolithFacts) {}); got.Order != "" || got.Target != "" {
		t.Fatalf("outside the quest: %+v", got)
	}
	got := quest(func(f *MonolithFacts) { f.PendingStructures = []string{"Thing_VoidStructure1", "Thing_VoidStructure2"} })
	if got.Order != MonolithInteract || got.Target != "Thing_VoidStructure1" || len(got.Performers) != 0 {
		t.Fatalf("a pending structure is interacted with: %+v", got)
	}
	got = quest(func(f *MonolithFacts) {
		f.Gleaming = domain.Known(true)
		f.PendingStructures = []string{"Thing_VoidStructure3"}
	})
	if got.Order != MonolithInteract || got.Target != "Thing_VoidMonolith1" {
		t.Fatalf("the Gleaming monolith is interacted with: %+v", got)
	}
	got = quest(func(f *MonolithFacts) {
		f.NodeID = "Thing_VoidNode1"
		f.NodePawns = []string{"Thing_Human4"}
		f.Gleaming = domain.Known(true)
	})
	if got.Order != MonolithInteract || got.Target != "Thing_VoidNode1" || len(got.Performers) != 1 || got.Performers[0] != "Thing_Human4" {
		t.Fatalf("the void node is touched by the skipped colonist: %+v", got)
	}
	if got = quest(func(f *MonolithFacts) { f.NodeID = "Thing_VoidNode1" }); got.Order != "" || got.Hold == "" {
		t.Fatalf("a node nobody can touch is a hold: %+v", got)
	}
	if got = quest(func(f *MonolithFacts) { f.Gleaming = domain.Unknown[bool]() }); got.Order != "" || len(got.Issues) != 1 {
		t.Fatalf("an unread Gleaming fact is an issue: %+v", got)
	}
	pending := monolithAt(3, "", false)
	pending.PendingStructures = []string{"Thing_VoidStructure1"}
	if !monolithAdvanceOwed(RoundsFacts{Monolith: domain.Known(pending)}, StageStable) {
		t.Fatal("a pending structure is a standing work")
	}
}

func TestMonolithWaitsForAStableColony(t *testing.T) {
	for _, stage := range []ColonyStage{StageFoothold, StageReserves} {
		gate := strongGate()
		gate.Stage = stage
		for _, f := range []MonolithFacts{monolithAt(0, "Stirring", true), monolithAt(1, "Waking", true), monolithAt(2, MonolithLevelVoidAwakened, true)} {
			got := MonolithAdvanceOwed(domain.Known(f), gate)
			if got.Order != "" || !strings.Contains(got.Hold, "has not reached Stable") {
				t.Fatalf("%s level %+v: %+v", stage, f.Level, got)
			}
		}
	}
	gate := strongGate()
	gate.Stage = StageFoothold
	quest := monolithAt(3, "", false)
	quest.Gleaming = domain.Known(true)
	if got := MonolithAdvanceOwed(domain.Known(quest), gate); got.Order != MonolithInteract {
		t.Fatalf("a running awakening quest is walked at any stage: %+v", got)
	}
}

package bridge

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The verdicts the native classifier used to give (the
// native-threat-classifier probe's cases, #646, #1080), now Go's (#1356).
func TestClassifyThreat(t *testing.T) {
	yes := proto.Bool(true)
	at := proto.Float64
	prey := &c.Ref{Id: proto.String("prey")}
	for name, c := range map[string]struct {
		row  *o.ThreatPawn
		want []policy.ThreatKind
	}{
		"faction hostile":             {&o.ThreatPawn{FactionHostile: yes, Faction: NewRef("Faction_9"), NearestColonistDistance: at(40)}, []policy.ThreatKind{policy.Hostile}},
		"far faction hostile":         {&o.ThreatPawn{FactionHostile: yes, NearestColonistDistance: at(400)}, []policy.ThreatKind{policy.Hostile}},
		"hostile without colonists":   {&o.ThreatPawn{FactionHostile: yes}, []policy.ThreatKind{policy.Hostile}},
		"manhunter":                   {&o.ThreatPawn{MentalState: proto.String("ManhunterPermanent"), Predator: yes, NearestColonistDistance: at(100)}, []policy.ThreatKind{policy.Hostile}},
		"manhunter any case":          {&o.ThreatPawn{MentalState: proto.String("manhunter")}, []policy.ThreatKind{policy.Hostile}},
		"hostility precedes the hunt": {&o.ThreatPawn{FactionHostile: yes, Downed: yes, Predator: yes, PredatorHunt: yes, NearestColonistDistance: at(1)}, []policy.ThreatKind{policy.Hostile}},
		"prison break":                {&o.ThreatPawn{PrisonBreak: yes, NearestColonistDistance: at(2)}, []policy.ThreatKind{policy.Hostile}},
		"held prisoner":               {&o.ThreatPawn{NearestColonistDistance: at(3)}, nil},
		"other mental state":          {&o.ThreatPawn{MentalState: proto.String("Berserk"), NearestColonistDistance: at(3)}, nil},
		"far mental predator":         {&o.ThreatPawn{MentalState: proto.String("Wander_Sad"), Predator: yes, NearestColonistDistance: at(31)}, nil},
		"player-owned hunter":         {&o.ThreatPawn{Ours: yes, PredatorHunt: yes, Prey: prey, PreyIsOurs: proto.Bool(false), Predator: yes}, []policy.ThreatKind{policy.IgnoredHunter}},
		"hunter on wild prey":         {&o.ThreatPawn{PredatorHunt: yes, Prey: prey, PreyIsOurs: proto.Bool(false), Predator: yes, NearestColonistDistance: at(50)}, []policy.ThreatKind{policy.IgnoredHunter}},
		"hunt on our prey":            {&o.ThreatPawn{PredatorHunt: yes, Prey: prey, PreyIsOurs: yes, Predator: yes, NearestColonistDistance: at(7)}, []policy.ThreatKind{policy.HuntingPredator}},
		"hunt with unreadable prey":   {&o.ThreatPawn{PredatorHunt: yes, Predator: yes, NearestColonistDistance: at(5)}, []policy.ThreatKind{policy.HuntingPredator}},
		"far hunt":                    {&o.ThreatPawn{PredatorHunt: yes, Prey: prey, PreyIsOurs: yes}, []policy.ThreatKind{policy.HuntingPredator}},
		"downed near":                 {&o.ThreatPawn{Downed: yes, NearestColonistDistance: at(5)}, []policy.ThreatKind{policy.NearbyDowned}},
		"downed predator is both":     {&o.ThreatPawn{Downed: yes, Predator: yes, NearestColonistDistance: at(3)}, []policy.ThreatKind{policy.NearbyDowned, policy.NearbyPredator}},
		"predator at the edge":        {&o.ThreatPawn{Predator: yes, NearestColonistDistance: at(ThreatProximityRadius)}, []policy.ThreatKind{policy.NearbyPredator}},
		"predator past the edge":      {&o.ThreatPawn{Predator: yes, NearestColonistDistance: at(ThreatProximityRadius + 1)}, nil},
		"predator without colonists":  {&o.ThreatPawn{Predator: yes, Downed: yes}, nil},
		"our downed animal":           {&o.ThreatPawn{Ours: yes, Downed: yes, Predator: yes, NearestColonistDistance: at(1)}, nil},
		"healthy wildlife":            {&o.ThreatPawn{NearestColonistDistance: at(1)}, nil},
	} {
		if got := ClassifyThreat(c.row); !slices.Equal(got, c.want) {
			t.Errorf("%s: got %v, want %v", name, got, c.want)
		}
	}
}

// The passive fact (#948, #1335) reaches the policy on hostiles only.
func TestEmergencyPassiveOnHostilesOnly(t *testing.T) {
	v := emergencyFixture()
	dormant := emergencyThreat("spider")
	dormant.Passive = proto.Bool(true)
	hunter := &o.ThreatPawn{Pawn: emergencyRef("wolf"), PredatorHunt: proto.Bool(true), Passive: proto.Bool(true)}
	v.Threats.Pawns = []*o.ThreatPawn{dormant, hunter}
	got, e := emergencyStatus(v, emergencyTable(emergencyRow("spider"), emergencyRow("wolf")), pbIdentity())
	if e != nil || len(got.Facts.Threats) != 2 {
		t.Fatal(got, e)
	}
	if passive, known := got.Facts.Threats[0].Passive.Value(); !known || !passive {
		t.Fatal("dormant hostile lost passive", got.Facts.Threats[0])
	}
	if _, known := got.Facts.Threats[1].Passive.Value(); known {
		t.Fatal("passive leaked onto a hunter", got.Facts.Threats[1])
	}
}

// A row no rule classifies is dropped before its reference resolves, and
// two rows for one pawn are a contract violation.
func TestEmergencyUnclassifiedAndDuplicateRows(t *testing.T) {
	v := emergencyFixture()
	v.Threats.Pawns = []*o.ThreatPawn{{Pawn: emergencyRef("berserk"), MentalState: proto.String("Berserk")}, emergencyThreat("raider")}
	got, e := emergencyStatus(v, emergencyTable(emergencyRow("raider")), pbIdentity())
	if e != nil || len(got.Facts.Threats) != 1 || got.Facts.Threats[0].ID != "raider" {
		t.Fatal(got, e)
	}
	v.Threats.Pawns = []*o.ThreatPawn{emergencyThreat("raider"), {Pawn: emergencyRef("raider"), PredatorHunt: proto.Bool(true)}}
	if _, e = emergencyStatus(v, emergencyTable(emergencyRow("raider")), pbIdentity()); e == nil {
		t.Fatal("duplicate threat row accepted")
	}
}

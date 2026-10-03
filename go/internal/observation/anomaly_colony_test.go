package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// A recorded Anomaly colony read projects knowledge, codex progress, held
// entities and incident state, keeps absent scalars unknown, and an absent or
// unavailable section stays unknown.
func TestAnomalyColonyProjection(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func() ColonyProjection {
		t.Helper()
		p, err := DecodeColony(r, id, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		return p
	}
	for _, section := range []*o.AnomalySection{nil, {Outcome: &o.AnomalySection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}} {
		r.GetObserved().Anomaly = section
		if _, known := decode().Anomaly.Value(); known {
			t.Fatal("absent or unavailable section became known")
		}
	}
	f := &o.AnomalyColonyFacts{
		Knowledge: []*o.KnowledgeProgress{
			{Category: proto.String("Basic"), CurrentProject: proto.String("BioferriteHarvesting"), Knowledge: proto.Float64(12.5), ProjectsAvailable: proto.Bool(true)},
			{Category: proto.String("Advanced"), ProjectsAvailable: proto.Bool(false)}},
		Codex:                    []*o.CodexProgress{{Category: proto.String("Minor"), Entries: proto.Uint32(8), Discovered: proto.Uint32(3)}},
		DiscoveredEntries:        []string{"Shambler", "Sightstealer"},
		HeldEntities:             []*o.HeldEntity{{PawnId: proto.String("Thing_Pawn_1"), PlatformId: proto.String("Thing_Platform_2")}},
		HoldingPlatformAvailable: proto.Bool(true),
		Incidents: &o.AnomalyIncidentState{MonolithSpawned: proto.Bool(true), LevelDef: proto.String("Dormant"), Level: proto.Int32(0), HighestLevelReached: proto.Int32(1),
			AmbientHorrorMode: proto.Bool(false), AnomalyThreatFractionNow: proto.Float64(0.2)},
	}
	r.GetObserved().Anomaly = &o.AnomalySection{Outcome: &o.AnomalySection_Observed{Observed: f}}
	v, known := decode().Anomaly.Value()
	if !known || len(v.Knowledge) != 2 || len(v.Codex) != 1 || len(v.DiscoveredEntries) != 2 || len(v.HeldEntities) != 1 {
		t.Fatalf("lost rows: %+v", v)
	}
	if p, ok := v.Knowledge[0].CurrentProject.Value(); !ok || p != "BioferriteHarvesting" {
		t.Fatal("current project lost")
	}
	if _, ok := v.Knowledge[1].CurrentProject.Value(); ok {
		t.Fatal("absent project became known")
	}
	if _, ok := v.Knowledge[1].Knowledge.Value(); ok {
		t.Fatal("absent knowledge became known")
	}
	i, ok := v.Incidents.Value()
	if !ok {
		t.Fatal("incident state lost")
	}
	if n, ok := i.HighestLevelReached.Value(); !ok || n != 1 {
		t.Fatal("highest level lost")
	}
	if _, ok := i.VoidAwakeningActive.Value(); ok {
		t.Fatal("absent flag became known")
	}
	// Contract violations are refused, never projected.
	for name, change := range map[string]func(){
		"duplicate category":     func() { f.Knowledge[1].Category = proto.String("Basic") },
		"knowledge sans project": func() { f.Knowledge[1].Knowledge = proto.Float64(1) },
		"negative knowledge":     func() { f.Knowledge[0].Knowledge = proto.Float64(-1) },
		"discovered above total": func() { f.Codex[0].Discovered = proto.Uint32(9) },
		"duplicate entry":        func() { f.DiscoveredEntries[1] = "Shambler" },
		"duplicate held pawn": func() {
			f.HeldEntities = append(f.HeldEntities, &o.HeldEntity{PawnId: proto.String("Thing_Pawn_1"), PlatformId: proto.String("other")})
		},
		"negative level":    func() { f.Incidents.Level = proto.Int32(-1) },
		"negative fraction": func() { f.Incidents.AnomalyThreatFractionNow = proto.Float64(-0.1) },
	} {
		saved := proto.Clone(f).(*o.AnomalyColonyFacts)
		change()
		if _, err := DecodeColony(r, id, bridge.Tables{}); err == nil {
			t.Fatalf("%s accepted", name)
		}
		proto.Reset(f)
		proto.Merge(f, saved)
	}
}

package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func anomalyCatalogFixture() *o.AnomalyCatalog {
	return &o.AnomalyCatalog{
		EntityCategories:    []*o.EntityCategoryRow{{DefName: proto.String("Basic"), ListOrder: proto.Int32(1)}},
		KnowledgeCategories: []*o.KnowledgeCategoryRow{{DefName: proto.String("Basic"), OverflowCategory: proto.String("Advanced")}, {DefName: proto.String("Advanced")}},
		CodexEntries: []*o.EntityCodexRow{{DefName: proto.String("Fingerspike"), Category: proto.String("Basic"), StartDiscovered: proto.Bool(false),
			DiscoveryType: o.EntityDiscoveryKind_ENTITY_DISCOVERY_KIND_SPAWN.Enum(), LinkedThings: []string{"Fingerspike"}, ProvocationIncidents: []string{"FleshbeastAttack"}}},
		Things: []*o.AnomalyThingRow{
			{DefName: proto.String("Fingerspike"), CodexEntry: proto.String("Fingerspike"), Entity: proto.Bool(true), RaceKnowledgeCategory: proto.String("Basic"),
				RaceAnomalyKnowledge: proto.Int32(2), MinContainmentStrength: proto.Float64(30),
				Studiable:     &o.StudiableProps{Comp: proto.String("CompProperties_Studiable"), FrequencyTicks: proto.Int32(60000), StudyAmountToComplete: proto.Float64(5), KnowledgeCategory: proto.String("Basic")},
				HoldingTarget: &o.HoldingTargetProps{HeldPawnKind: proto.String("Fingerspike"), BaseEscapeIntervalMtbDays: proto.Float64(10)}},
			{DefName: proto.String("HoldingPlatform"), Holder: &o.EntityHolderProps{Comp: proto.String("CompProperties_EntityHolderPlatform"), ContainmentFactor: proto.Float64(1)}},
		},
		Incidents: []*o.AnomalyIncidentRow{{DefName: proto.String("FleshbeastAttack"), Category: proto.String("ThreatBig"), Threat: proto.Bool(true),
			Worker: proto.String("IncidentWorker_FleshbeastAttack"), TargetTags: []string{"Map_PlayerHome"}, BaseChance: proto.Float64(1.5), CodexEntry: proto.String("Fingerspike")}},
	}
}

// TestAnomalyCatalogDecode (#1737): the section decodes by name and refuses
// duplicates, unknown references, nonfinite and negative numbers; absent
// stays nil.
func TestAnomalyCatalogDecode(t *testing.T) {
	got, err := DecodeAnomalyCatalog(anomalyCatalogFixture())
	if err != nil || got.EntityCategories["Basic"] == nil || got.KnowledgeCategories["Advanced"] == nil || got.Codex["Fingerspike"] == nil ||
		got.Things["Fingerspike"] == nil || got.Things["HoldingPlatform"] == nil || got.Incidents["FleshbeastAttack"] == nil {
		t.Fatalf("%+v %v", got, err)
	}
	if none, err := DecodeAnomalyCatalog(nil); none != nil || err != nil {
		t.Fatal("a game without Anomaly must decode to nil", none, err)
	}
	for name, mutate := range map[string]func(*o.AnomalyCatalog){
		"duplicate thing":         func(v *o.AnomalyCatalog) { v.Things = append(v.Things, v.Things[0]) },
		"unnamed incident":        func(v *o.AnomalyCatalog) { v.Incidents[0].DefName = nil },
		"unknown category":        func(v *o.AnomalyCatalog) { v.CodexEntries[0].Category = proto.String("Missing") },
		"unknown overflow":        func(v *o.AnomalyCatalog) { v.KnowledgeCategories[0].OverflowCategory = proto.String("Missing") },
		"unknown codex on thing":  func(v *o.AnomalyCatalog) { v.Things[0].CodexEntry = proto.String("Missing") },
		"unknown codex on threat": func(v *o.AnomalyCatalog) { v.Incidents[0].CodexEntry = proto.String("Missing") },
		"unknown study category":  func(v *o.AnomalyCatalog) { v.Things[0].Studiable.KnowledgeCategory = proto.String("Missing") },
		"unknown race category":   func(v *o.AnomalyCatalog) { v.Things[0].RaceKnowledgeCategory = proto.String("Missing") },
		"unspecified discovery": func(v *o.AnomalyCatalog) {
			v.CodexEntries[0].DiscoveryType = o.EntityDiscoveryKind_ENTITY_DISCOVERY_KIND_UNSPECIFIED.Enum()
		},
		"duplicate link":       func(v *o.AnomalyCatalog) { v.CodexEntries[0].LinkedThings = []string{"Fingerspike", "Fingerspike"} },
		"nan containment":      func(v *o.AnomalyCatalog) { v.Things[0].MinContainmentStrength = proto.Float64(math.NaN()) },
		"negative containment": func(v *o.AnomalyCatalog) { v.Things[0].MinContainmentStrength = proto.Float64(-1) },
		"nan escape": func(v *o.AnomalyCatalog) {
			v.Things[0].HoldingTarget.BaseEscapeIntervalMtbDays = proto.Float64(math.Inf(1))
		},
		"nan factor":      func(v *o.AnomalyCatalog) { v.Things[1].Holder.ContainmentFactor = proto.Float64(math.NaN()) },
		"negative chance": func(v *o.AnomalyCatalog) { v.Incidents[0].BaseChance = proto.Float64(-1) },
		"duplicate tag":   func(v *o.AnomalyCatalog) { v.Incidents[0].TargetTags = []string{"Map_PlayerHome", "Map_PlayerHome"} },
	} {
		v := anomalyCatalogFixture()
		mutate(v)
		if _, err := DecodeAnomalyCatalog(v); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

// Vanilla Odyssey animal defs (Alligator in CI run 37128653267) carry
// studyAmountToComplete = -1; the catalog mirrors the def, so it decodes.
func TestAnomalyCatalogKeepsNegativeStudyAmount(t *testing.T) {
	v := anomalyCatalogFixture()
	v.Things[0].Studiable.StudyAmountToComplete = proto.Float64(-1)
	if _, err := DecodeAnomalyCatalog(v); err != nil {
		t.Fatal(err)
	}
}

func anomalyPawnFixture() *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("Thing_Fingerspike1")}, Anomaly: &o.PawnAnomaly{
		Entity: proto.Bool(true), Mutant: proto.Bool(false), Shambler: proto.Bool(false), MinContainmentStrength: proto.Float64(30),
		Held: &o.HeldState{Held: proto.Bool(true), Platform: &c.Ref{Id: proto.String("Building_HoldingPlatform1")},
			Mode: o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_STUDY.Enum(), Escaping: proto.Bool(false)},
		Study: &o.StudyState{StudyEnabled: proto.Bool(true), Completed: proto.Bool(false), ProgressPercent: proto.Float64(.25), AnomalyKnowledge: proto.Float64(2), KnowledgeCategory: proto.String("Basic")},
	}}
}

// TestPawnAnomalyRow (#1737): the row block lifts into typed facts, an absent
// field stays unknown, a failed sub-read is named by an issue, and malformed
// blocks are refused.
func TestPawnAnomalyRow(t *testing.T) {
	row := anomalyPawnFixture()
	if err := validatePawnAnomaly(row.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, known := PawnAnomaly(row.Anomaly).Value()
	if !known {
		t.Fatal("anomaly unknown")
	}
	if entity, ok := a.Entity.Value(); !ok || !entity {
		t.Fatal("entity", entity, ok)
	}
	if min, ok := a.MinContainmentStrength.Value(); !ok || min != 30 {
		t.Fatal("minimum containment strength", min, ok)
	}
	held, ok := a.Held.Value()
	if !ok || held == nil {
		t.Fatal("held", held, ok)
	}
	if mode, _ := held.Mode.Value(); mode != policy.ContainmentStudy {
		t.Fatal("mode", mode)
	}
	if platform, _ := held.Platform.Value(); platform != "Building_HoldingPlatform1" {
		t.Fatal("platform", platform)
	}
	if _, ok := held.ExtractBioferrite.Value(); ok {
		t.Fatal("an unread bioferrite flag must stay unknown")
	}
	study, _ := a.Study.Value()
	if p, ok := study.ProgressPercent.Value(); !ok || p != .25 {
		t.Fatal("progress", p, ok)
	}
	if _, ok := study.Completed.Value(); !ok {
		t.Fatal("completed must be known")
	}
	if _, ok := study.CurrentlyStudiable.Value(); ok {
		t.Fatal("an unread studiable flag must stay unknown")
	}
	if _, ok := PawnAnomaly(nil).Value(); ok {
		t.Fatal("a pawn without Anomaly must stay unknown")
	}
	plain := &o.PawnAnomaly{Entity: proto.Bool(false), Mutant: proto.Bool(false), Shambler: proto.Bool(false)}
	if err := validatePawnAnomaly(plain); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(plain).Value()
	if held, ok := a.Held.Value(); !ok || held != nil {
		t.Fatal("a pawn with no holding comp is known not held", held, ok)
	}
	if _, ok := a.MinContainmentStrength.Value(); ok {
		t.Fatal("a non-entity has no containment minimum")
	}
	failed := &o.PawnAnomaly{Entity: proto.Bool(true), Study: row.Anomaly.Study,
		Issues: []*o.ReadIssue{{Field: proto.String("held"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	if err := validatePawnAnomaly(failed); err != nil {
		t.Fatal(err)
	}
	a, _ = PawnAnomaly(failed).Value()
	if _, ok := a.Held.Value(); ok {
		t.Fatal("a failed held read must stay unknown")
	}
	if _, ok := a.Study.Value(); !ok {
		t.Fatal("a failed held read must not hide study")
	}
	for name, mutate := range map[string]func(*o.PawnAnomaly){
		"nan minimum":      func(v *o.PawnAnomaly) { v.MinContainmentStrength = proto.Float64(math.NaN()) },
		"negative minimum": func(v *o.PawnAnomaly) { v.MinContainmentStrength = proto.Float64(-1) },
		"unspecified mode": func(v *o.PawnAnomaly) {
			v.Held.Mode = o.EntityContainmentModeKind_ENTITY_CONTAINMENT_MODE_KIND_UNSPECIFIED.Enum()
		},
		"unknown mode":     func(v *o.PawnAnomaly) { v.Held.Mode = o.EntityContainmentModeKind(99).Enum() },
		"empty platform":   func(v *o.PawnAnomaly) { v.Held.Platform = &c.Ref{} },
		"progress range":   func(v *o.PawnAnomaly) { v.Study.ProgressPercent = proto.Float64(1.5) },
		"nan knowledge":    func(v *o.PawnAnomaly) { v.Study.AnomalyKnowledge = proto.Float64(math.NaN()) },
		"negative points":  func(v *o.PawnAnomaly) { v.Study.StudyPoints = proto.Float64(-1) },
		"known and issued": func(v *o.PawnAnomaly) { v.Issues = []*o.ReadIssue{{Field: proto.String("entity")}} },
	} {
		r := anomalyPawnFixture()
		mutate(r.Anomaly)
		if validatePawnAnomaly(r.Anomaly) == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

func anomalyBuildingFixture() *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Building_HoldingPlatform1")}, Anomaly: &o.AnomalyBuilding{
		Holder: &o.EntityHolderState{ContainmentStrength: proto.Float64(42.5), Available: proto.Bool(false), HeldPawn: &c.Ref{Id: proto.String("Thing_Fingerspike1")}},
	}}
}

// TestBuildingAnomalyRow (#1737): a holding platform's containment strength
// and held pawn lift into typed facts and a block native could not read
// stays unknown.
func TestBuildingAnomalyRow(t *testing.T) {
	row := anomalyBuildingFixture()
	if err := validateBuildingAnomaly(row.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, known := BuildingAnomaly(row).Value()
	if !known {
		t.Fatal("anomaly unknown")
	}
	holder, ok := a.Holder.Value()
	if !ok || holder == nil {
		t.Fatal("holder", holder, ok)
	}
	if s, ok := holder.ContainmentStrength.Value(); !ok || s != 42.5 {
		t.Fatal("containment strength", s, ok)
	}
	if holder.HeldPawn != "Thing_Fingerspike1" {
		t.Fatal("held pawn", holder.HeldPawn)
	}
	if study, ok := a.Study.Value(); !ok || study != nil {
		t.Fatal("a platform is known not studiable", study, ok)
	}
	if _, ok := BuildingAnomaly(&o.BuildingState{}).Value(); ok {
		t.Fatal("a row without the block must stay unknown")
	}
	failed := &o.BuildingState{Anomaly: &o.AnomalyBuilding{Issues: []*o.ReadIssue{{Field: proto.String("holder"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}}
	if err := validateBuildingAnomaly(failed.Anomaly); err != nil {
		t.Fatal(err)
	}
	a, _ = BuildingAnomaly(failed).Value()
	if _, ok := a.Holder.Value(); ok {
		t.Fatal("a failed holder read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.AnomalyBuilding){
		"nan strength":      func(v *o.AnomalyBuilding) { v.Holder.ContainmentStrength = proto.Float64(math.NaN()) },
		"negative strength": func(v *o.AnomalyBuilding) { v.Holder.ContainmentStrength = proto.Float64(-1) },
		"empty pawn":        func(v *o.AnomalyBuilding) { v.Holder.HeldPawn = &c.Ref{} },
		"known and issued":  func(v *o.AnomalyBuilding) { v.Issues = []*o.ReadIssue{{Field: proto.String("holder")}} },
	} {
		r := anomalyBuildingFixture()
		mutate(r.Anomaly)
		if validateBuildingAnomaly(r.Anomaly) == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if checkBuildingListRow(row) != nil {
		t.Fatal("list row refused")
	}
}

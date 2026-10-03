package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func biotechCatalogFixture() *o.BiotechCatalog {
	return &o.BiotechCatalog{
		LifeStages: []*o.LifeStageRow{
			{DefName: proto.String("HumanlikeBaby"), DevelopmentalStage: proto.String("Baby"), AlwaysDowned: proto.Bool(true), Effects: []*o.StatEffect{{Stat: proto.String("MoveSpeed"), Factor: proto.Float64(0)}}},
			{DefName: proto.String("HumanlikeAdult"), DevelopmentalStage: proto.String("Adult")}},
		Races: []*o.RaceLifeStages{{Race: proto.String("Human"), Stages: []*o.LifeStageAgeRow{{LifeStage: proto.String("HumanlikeBaby"), MinAgeYears: proto.Float64(0)}, {LifeStage: proto.String("HumanlikeAdult"), MinAgeYears: proto.Float64(18)}},
			WorkMinAges: []*o.WorkMinAge{{WorkType: proto.String("Hauling"), MinAge: proto.Int32(3)}}}},
		Genes:         []*o.GeneRow{{DefName: proto.String("Robust"), DisabledWorkTags: []string{"Violent"}, Effects: []*o.StatEffect{{Stat: proto.String("WorkSpeedGlobal"), Offset: proto.Float64(.1)}}, Aptitudes: []*o.SkillLevel{{Skill: proto.String("Shooting"), Level: proto.Int32(2)}}}},
		Xenotypes:     []*o.XenotypeRow{{DefName: proto.String("Hussar"), Genes: []string{"Robust"}}},
		MechKinds:     []*o.MechKindRow{{DefName: proto.String("Mech_Lifter"), BandwidthCost: proto.Float64(1), WorkTypes: []string{"Hauling"}, WorkPriorities: []*o.MechWorkPriority{{WorkType: proto.String("Hauling"), Priority: proto.Int32(1)}}}},
		MechWorkModes: []*o.MechWorkModeRow{{DefName: proto.String("Work"), UiOrder: proto.Int32(1)}},
	}
}

// TestBiotechCatalogDecode (#1678): the section decodes by name and refuses
// duplicates, bad references and nonfinite numbers; absent stays nil.
func TestBiotechCatalogDecode(t *testing.T) {
	got, err := DecodeBiotechCatalog(biotechCatalogFixture())
	if err != nil || got.Genes["Robust"] == nil || got.Xenotypes["Hussar"] == nil || got.MechKinds["Mech_Lifter"] == nil || got.MechWorkModes["Work"] == nil || got.LifeStages["HumanlikeBaby"] == nil || got.Races["Human"] == nil {
		t.Fatalf("%+v %v", got, err)
	}
	if none, err := DecodeBiotechCatalog(nil); none != nil || err != nil {
		t.Fatal("Core-only catalog must decode to nil", none, err)
	}
	for name, mutate := range map[string]func(*o.BiotechCatalog){
		"duplicate gene":     func(v *o.BiotechCatalog) { v.Genes = append(v.Genes, v.Genes[0]) },
		"unnamed stage":      func(v *o.BiotechCatalog) { v.LifeStages[0].DefName = nil },
		"unknown xeno gene":  func(v *o.BiotechCatalog) { v.Xenotypes[0].Genes = []string{"Missing"} },
		"unknown race stage": func(v *o.BiotechCatalog) { v.Races[0].Stages[0].LifeStage = proto.String("Missing") },
		"descending ages":    func(v *o.BiotechCatalog) { v.Races[0].Stages[1].MinAgeYears = proto.Float64(-1) },
		"nan effect":         func(v *o.BiotechCatalog) { v.Genes[0].Effects[0].Offset = proto.Float64(math.NaN()) },
		"both sides":         func(v *o.BiotechCatalog) { v.Genes[0].Effects[0].Factor = proto.Float64(1) },
		"duplicate tag":      func(v *o.BiotechCatalog) { v.Genes[0].DisabledWorkTags = []string{"Violent", "Violent"} },
		"negative work age":  func(v *o.BiotechCatalog) { v.Races[0].WorkMinAges[0].MinAge = proto.Int32(-1) },
		"nan bandwidth":      func(v *o.BiotechCatalog) { v.MechKinds[0].BandwidthCost = proto.Float64(math.Inf(1)) },
		"priority missing":   func(v *o.BiotechCatalog) { v.MechKinds[0].WorkPriorities[0].Priority = nil },
	} {
		v := biotechCatalogFixture()
		mutate(v)
		if _, err := DecodeBiotechCatalog(v); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

func biotechPawnFixture() *o.PawnBiotech {
	return &o.PawnBiotech{LifeStage: proto.String("HumanlikeChild"), DevelopmentalStage: proto.String("Child"), Learning: proto.Float64(.6), LearningCategory: proto.String("Satisfied"),
		Genes: []*o.PawnGene{{DefName: proto.String("Robust"), Xenogene: proto.Bool(false), Active: proto.Bool(true)}}, Xenotype: proto.String("Hussar"), XenotypeName: proto.String("Hussar"), Hybrid: proto.Bool(false),
		Mechanitor: &o.PawnMechanitor{UsedBandwidth: proto.Int32(2), TotalBandwidth: proto.Int32(6), ControlledMechs: []*c.Ref{{Id: proto.String("Thing_Mech1")}}},
	}
}

// TestPawnBiotechRow (#1678): the pawn block lifts into typed facts, a
// field a read issue names stays unknown, and malformed blocks are refused.
func TestPawnBiotechRow(t *testing.T) {
	b := biotechPawnFixture()
	if err := validatePawnBiotech(b); err != nil {
		t.Fatal(err)
	}
	got, known := PawnBiotech(b).Value()
	if !known {
		t.Fatal("block unknown")
	}
	if child, ok := got.IsChild(); !child || !ok {
		t.Fatal("child stage not a child", child, ok)
	}
	if genes, ok := got.Genes.Value(); !ok || len(genes) != 1 || genes[0].Name != "Robust" {
		t.Fatal("genes", genes)
	}
	if m, ok := got.Mechanitor.Value(); !ok || m == nil || m.UsedBandwidth != domain.Known(2) || len(m.ControlledMechs) != 1 {
		t.Fatal("mechanitor", m)
	}
	if mech, ok := got.Mech.Value(); !ok || mech != nil {
		t.Fatal("a pawn with no mech block is a known non-mech", mech)
	}
	if _, ok := PawnBiotech(nil).Value(); ok {
		t.Fatal("Core-only pawn block must stay unknown")
	}
	failed := biotechPawnFixture()
	failed.Genes, failed.Xenotype, failed.XenotypeName, failed.Hybrid = nil, nil, nil, nil
	failed.Issues = []*o.ReadIssue{{Field: proto.String("genes"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := validatePawnBiotech(failed); err != nil {
		t.Fatal(err)
	}
	if bt, _ := PawnBiotech(failed).Value(); func() bool { _, ok := bt.Genes.Value(); return ok }() {
		t.Fatal("failed genes read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.PawnBiotech){
		"learning range":   func(v *o.PawnBiotech) { v.Learning = proto.Float64(1.5) },
		"duplicate gene":   func(v *o.PawnBiotech) { v.Genes = append(v.Genes, v.Genes[0]) },
		"gene flags":       func(v *o.PawnBiotech) { v.Genes[0].Active = nil },
		"negative band":    func(v *o.PawnBiotech) { v.Mechanitor.TotalBandwidth = proto.Int32(-1) },
		"known and issued": func(v *o.PawnBiotech) { v.Issues = []*o.ReadIssue{{Field: proto.String("genes")}} },
	} {
		v := biotechPawnFixture()
		mutate(v)
		if validatePawnBiotech(v) == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

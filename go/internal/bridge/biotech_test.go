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
		MechWorkModes: []*o.MechWorkModeRow{{DefName: proto.String("Work"), UiOrder: proto.Int32(1), Work: proto.Bool(true)}, {DefName: proto.String("Escort"), UiOrder: proto.Int32(3), Escort: proto.Bool(true)}, {DefName: proto.String("Recharge"), UiOrder: proto.Int32(2), Recharge: proto.Bool(true)}},
	}
}

// TestGeneTuningFacts (#1932): the singleton passes through the catalog view
// and a malformed one is refused.
func TestGeneTuningFacts(t *testing.T) {
	tuning := func() *o.GeneTuningFacts {
		return &o.GeneTuningFacts{BiostatMin: proto.Int32(-5), BiostatMax: proto.Int32(5), BaseMaxComplexity: proto.Int32(6),
			CreationHoursCurve: []*o.CurvePointRow{{X: proto.Float64(0), Y: proto.Float64(3)}, {X: proto.Float64(4), Y: proto.Float64(5)}},
			RegrowDaysMin:      proto.Float64(12), RegrowDaysMax: proto.Float64(20), ExtractTicks: proto.Int32(30000), NoPowerEjectTicks: proto.Int32(60000)}
	}
	v := biotechCatalogFixture()
	v.GeneTuning = tuning()
	got, err := DecodeBiotechCatalog(v)
	if err != nil || got.GeneTuning.GetBiostatMax() != 5 || got.GeneTuning.GetExtractTicks() != 30000 || len(got.GeneTuning.CreationHoursCurve) != 2 {
		t.Fatalf("gene tuning = %+v, %v", got, err)
	}
	if plain, err := DecodeBiotechCatalog(biotechCatalogFixture()); err != nil || plain.GeneTuning != nil {
		t.Fatalf("absent gene tuning = %v, %v", plain, err)
	}
	for name, mutate := range map[string]func(*o.GeneTuningFacts){
		"descending biostat":  func(g *o.GeneTuningFacts) { g.BiostatMin = proto.Int32(6) },
		"descending regrow":   func(g *o.GeneTuningFacts) { g.RegrowDaysMin = proto.Float64(30) },
		"nan regrow":          func(g *o.GeneTuningFacts) { g.RegrowDaysMax = proto.Float64(math.NaN()) },
		"negative ticks":      func(g *o.GeneTuningFacts) { g.ExtractTicks = proto.Int32(-1) },
		"curve not ascending": func(g *o.GeneTuningFacts) { g.CreationHoursCurve[1].X = proto.Float64(0) },
		"curve incomplete":    func(g *o.GeneTuningFacts) { g.CreationHoursCurve[0].Y = nil },
	} {
		bad := biotechCatalogFixture()
		bad.GeneTuning = tuning()
		mutate(bad.GeneTuning)
		if _, err := DecodeBiotechCatalog(bad); err == nil {
			t.Errorf("%s accepted", name)
		}
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

// TestMechEnergyAndRechargeRole (#1688): the mech block carries energy and
// its group's recharge band, a failed band read stays unknown, and the
// catalog names exactly one recharge mode from the row flag.
func TestMechEnergyAndRechargeRole(t *testing.T) {
	b := biotechPawnFixture()
	b.Mech = &o.PawnMech{WorkMode: proto.String("Work"), ControlGroup: proto.Int32(0), Energy: proto.Float64(.4), RechargeBelow: proto.Float64(.3), RechargeAbove: proto.Float64(.7)}
	if err := validatePawnBiotech(b); err != nil {
		t.Fatal(err)
	}
	bt, _ := PawnBiotech(b).Value()
	mech, _ := bt.Mech.Value()
	if e, ok := mech.Energy.Value(); !ok || e != .4 {
		t.Fatal("energy", e, ok)
	}
	if lo, ok := mech.RechargeBelow.Value(); !ok || lo != .3 {
		t.Fatal("band", lo, ok)
	}
	failed := biotechPawnFixture()
	failed.Mech = &o.PawnMech{Energy: proto.Float64(.4)}
	failed.Issues = []*o.ReadIssue{{Field: proto.String("mech_thresholds"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	if err := validatePawnBiotech(failed); err != nil {
		t.Fatal(err)
	}
	bt, _ = PawnBiotech(failed).Value()
	mech, _ = bt.Mech.Value()
	if _, ok := mech.RechargeBelow.Value(); ok {
		t.Fatal("a failed band read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.PawnMech){
		"energy range":  func(m *o.PawnMech) { m.Energy = proto.Float64(1.2) },
		"inverted band": func(m *o.PawnMech) { m.RechargeBelow, m.RechargeAbove = proto.Float64(.8), proto.Float64(.2) },
	} {
		v := biotechPawnFixture()
		v.Mech = &o.PawnMech{Energy: proto.Float64(.4), RechargeBelow: proto.Float64(.3), RechargeAbove: proto.Float64(.7)}
		mutate(v.Mech)
		if validatePawnBiotech(v) == nil {
			t.Errorf("%s accepted", name)
		}
	}
	catalog, err := DecodeBiotechCatalog(biotechCatalogFixture())
	if modes := catalog.MechCatalog(); err != nil || modes.Work != "Work" || modes.Escort != "Escort" || modes.Recharge != "Recharge" {
		t.Fatal("mode roles", err)
	}
	for role, set := range map[string]func(*o.MechWorkModeRow){
		"work": func(r *o.MechWorkModeRow) { r.Work = proto.Bool(true) }, "escort": func(r *o.MechWorkModeRow) { r.Escort = proto.Bool(true) },
		"recharge": func(r *o.MechWorkModeRow) { r.Recharge = proto.Bool(true) },
	} {
		two := biotechCatalogFixture()
		set(two.MechWorkModes[2])
		two.MechWorkModes = append(two.MechWorkModes, &o.MechWorkModeRow{DefName: proto.String("Extra" + role)})
		set(two.MechWorkModes[3])
		if _, err := DecodeBiotechCatalog(two); err == nil {
			t.Fatalf("two %s modes accepted", role)
		}
		none := biotechCatalogFixture()
		none.MechWorkModes = none.MechWorkModes[:0:0]
		none.MechWorkModes = append(none.MechWorkModes, &o.MechWorkModeRow{DefName: proto.String("Only")})
		if _, err := DecodeBiotechCatalog(none); err == nil {
			t.Fatalf("no %s mode accepted", role)
		}
	}
}

package bridge

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// biotechRolesFixture names the three role work modes of a catalog that
// carries them as MechWorkModeDef rows.
func biotechRolesFixture() (*DefinitionCatalog, *o.MechWorkModeRoles) {
	modes := map[string]proto.Message{}
	for _, name := range []string{"Work", "Escort", "Recharge"} {
		modes[name] = &d.MechWorkModeDef{DefName: name}
	}
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{},
		Defs: map[protoreflect.FullName]map[string]proto.Message{(&d.MechWorkModeDef{}).ProtoReflect().Descriptor().FullName(): modes}}
	return catalog, &o.MechWorkModeRoles{Work: proto.String("Work"), Escort: proto.String("Escort"), Recharge: proto.String("Recharge")}
}

// TestBiotechFacade: the section is built from the catalog's rows, carries
// the game's role picks and the gene tuning singleton, and a malformed one is
// refused; absent stays nil.
func TestBiotechFacade(t *testing.T) {
	tuning := func() *o.GeneTuningFacts {
		return &o.GeneTuningFacts{BiostatMin: proto.Int32(-5), BiostatMax: proto.Int32(5), BaseMaxComplexity: proto.Int32(6),
			CreationHoursCurve: []*o.CurvePointRow{{X: proto.Float64(0), Y: proto.Float64(3)}, {X: proto.Float64(4), Y: proto.Float64(5)}},
			RegrowDaysMin:      proto.Float64(12), RegrowDaysMax: proto.Float64(20), ExtractTicks: proto.Int32(30000), NoPowerEjectTicks: proto.Int32(60000)}
	}
	catalog, roles := biotechRolesFixture()
	got, err := buildBiotech(catalog, &o.BiotechCatalog{MechWorkModes: roles, GeneTuning: tuning()})
	if err != nil || got.GeneTuning.GetBiostatMax() != 5 || got.GeneTuning.GetExtractTicks() != 30000 || len(got.GeneTuning.CreationHoursCurve) != 2 {
		t.Fatalf("gene tuning = %+v, %v", got, err)
	}
	if modes := got.MechCatalog(); modes.Work != "Work" || modes.Escort != "Escort" || modes.Recharge != "Recharge" {
		t.Fatalf("mode roles %+v", modes)
	}
	if plain, err := buildBiotech(catalog, &o.BiotechCatalog{MechWorkModes: roles}); err != nil || plain.GeneTuning != nil {
		t.Fatalf("absent gene tuning = %v, %v", plain, err)
	}
	if none, err := buildBiotech(catalog, nil); none != nil || err != nil {
		t.Fatal("Core-only catalog must build to nil", none, err)
	}
	for name, mutate := range map[string]func(*o.GeneTuningFacts){
		"descending biostat":  func(g *o.GeneTuningFacts) { g.BiostatMin = proto.Int32(6) },
		"descending regrow":   func(g *o.GeneTuningFacts) { g.RegrowDaysMin = proto.Float64(30) },
		"nan regrow":          func(g *o.GeneTuningFacts) { g.RegrowDaysMax = proto.Float64(math.NaN()) },
		"negative ticks":      func(g *o.GeneTuningFacts) { g.ExtractTicks = proto.Int32(-1) },
		"curve not ascending": func(g *o.GeneTuningFacts) { g.CreationHoursCurve[1].X = proto.Float64(0) },
		"curve incomplete":    func(g *o.GeneTuningFacts) { g.CreationHoursCurve[0].Y = nil },
	} {
		bad := tuning()
		mutate(bad)
		if _, err := buildBiotech(catalog, &o.BiotechCatalog{MechWorkModes: roles, GeneTuning: bad}); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
	for role, set := range map[string]func(*o.MechWorkModeRoles){
		"work":     func(r *o.MechWorkModeRoles) { r.Work = nil },
		"escort":   func(r *o.MechWorkModeRoles) { r.Escort = proto.String("Missing") },
		"recharge": func(r *o.MechWorkModeRoles) { r.Recharge = proto.String("") },
	} {
		_, bad := biotechRolesFixture()
		set(bad)
		if _, err := buildBiotech(catalog, &o.BiotechCatalog{MechWorkModes: bad}); err == nil {
			t.Errorf("bad %s role accepted", role)
		}
	}
	if _, err := buildBiotech(catalog, &o.BiotechCatalog{}); err == nil {
		t.Error("catalog without roles accepted")
	}
}

// TestBiotechFacadeOnRecordedCatalog: the facade built from the recorded
// whole-game catalog answers what the retired native Biotech rows did.
func TestBiotechFacadeOnRecordedCatalog(t *testing.T) {
	cat := fullCatalog(t).Biotech
	if cat == nil {
		t.Fatal("recorded catalog has no Biotech")
	}
	ages, err := cat.WorkMinAges("Human")
	if err != nil || ages["Hauling"] != 3 || ages["Research"] != 13 {
		t.Fatalf("ages %v, %v", ages, err)
	}
	modes := cat.MechCatalog()
	if modes.Work != "Work" || modes.Escort != "Escort" || modes.Recharge != "Recharge" {
		t.Fatalf("roles %+v", modes)
	}
	if k := modes.Kinds["Mech_Agrihand"]; !k.WorkMech || k.BandwidthCost != 1 || k.CombatPower != 10 || len(k.WorkTypes) != 2 {
		t.Fatalf("agrihand %+v", k)
	}
	if k := modes.Kinds["Mech_Centurion"]; k.WorkMech || k.BandwidthCost != 5 || k.CombatPower != 250 {
		t.Fatalf("centurion %+v", k)
	}
	effects, err := cat.GeneEffects([]policy.PawnGene{{Name: "Robust"}})
	if err != nil || effects.Stat("IncomingDamageFactor").Factor != 0.75 {
		t.Fatalf("robust %+v, %v", effects, err)
	}
}

func biotechPawnFixture() *o.PawnBiotech {
	return &o.PawnBiotech{LifeStage: proto.String("HumanlikeChild"), DevelopmentalStage: proto.String("Child"), Learning: proto.Float64(.6), LearningCategory: proto.String("Satisfied"),
		Genes: []*o.PawnGene{{DefName: proto.String("Robust"), Xenogene: proto.Bool(false), Active: proto.Bool(true)}}, Xenotype: proto.String("Hussar"), XenotypeName: proto.String("Hussar"), Hybrid: proto.Bool(false),
		Mechanitor: &o.PawnMechanitor{UsedBandwidth: proto.Int32(2), TotalBandwidth: proto.Int32(6), ControlledMechs: []*c.Ref{{Id: proto.String("Thing_Mech1")}}},
	}
}

// TestPawnBiotechRow: the pawn block lifts into typed facts, a
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

// TestMechEnergyAndRechargeRole: the mech block carries energy and
// its group's recharge band, a failed band read stays unknown, and the
// catalog is covered by TestBiotechFacade.
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
}

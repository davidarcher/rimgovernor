package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGearQualityMultipliers(t *testing.T) {
	for q, want := range []struct{ armor, thermal float64 }{{.6, .8}, {.8, .9}, {1, 1}, {1.15, 1.1}, {1.3, 1.2}, {1.45, 1.5}, {1.8, 1.8}} {
		a, i := GearQualityMultipliers(q)
		if a != want.armor || i != want.thermal {
			t.Fatalf("quality %d: %v %v", q, a, i)
		}
		p := GearLoadoutInput{Ambient: 0, ComfortableMin: 20, ComfortableMax: 30}
		o := loadoutOption("coat", GearOuter)
		o.Quality = q
		o.Sharp = 1
		o.Cold = 5
		if got := gearItemScore(p, o); math.Abs(got-(2*a+5*i)) > 1e-9 {
			t.Fatalf("score %d: %v", q, got)
		}
	}
}

func loadoutOption(id string, slot GearSlot) GearOption {
	layer, group := "OnSkin", "Torso"
	switch slot {
	case GearSkinLegs:
		group = "Legs"
	case GearMiddleTorso:
		layer = "Middle"
	case GearOuter:
		layer = "Shell"
	case GearHeadgear:
		group = "Head"
	case GearBelt:
		layer = "Belt"
	}
	return GearOption{ID: id, Definition: Resource(id), Quality: 2, Slot: slot, Layers: []string{layer}, Groups: []string{group}, Source: GearBillSource, Condition: 1}
}

func TestGearRoles(t *testing.T) {
	work := func(w WorkType) GearRoleInput {
		return GearRoleInput{Work: WorkPawn{Work: domain.Known([]WorkPriority{{Work: w, Priority: 1}})}}
	}
	for _, tt := range []struct {
		name  string
		input GearRoleInput
		want  GearRole
	}{
		{"worker", work(WorkConstruction), GearWorker},
		{"soldier", GearRoleInput{DraftedSquad: true}, GearSoldier},
		{"hunter", work(WorkHunting), GearHunter},
		{"crafter", work(WorkCrafting), GearIndoor},
		{"child", GearRoleInput{Child: true, DraftedSquad: true}, GearChild},
		{"slave", GearRoleInput{Slave: true, DraftedSquad: true}, GearSlave},
		{"noncombatant", GearRoleInput{IncapableOfViolence: true, DraftedSquad: true}, GearNonCombatant},
	} {
		t.Run(tt.name, func(t *testing.T) {
			if got := DeriveGearRole(tt.input); got != tt.want {
				t.Fatal(got)
			}
		})
	}
}

func TestGearRoleChoices(t *testing.T) {
	coat := loadoutOption("coat", GearOuter)
	coat.Cold = 10
	armor := loadoutOption("armor", GearOuter)
	armor.Sharp = 1
	armor.MoveSpeed = -.2
	cheap := loadoutOption("cheap", GearOuter)
	cheap.Cost = 1
	coat.Cost = 20
	armor.Cost = 100
	for _, tt := range []struct {
		name string
		p    GearLoadoutInput
		want Resource
	}{
		{"worker", GearLoadoutInput{Ambient: -10, ComfortableMin: 0}, "coat"},
		{"soldier", GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}}, "armor"},
		{"slave", GearLoadoutInput{Female: true, Role: GearRoleInput{Slave: true}}, "cheap"},
		{"noncombatant", GearLoadoutInput{Ambient: -10, Role: GearRoleInput{IncapableOfViolence: true}}, "coat"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			tt.p.Options = []GearOption{coat, armor, cheap}
			l, err := PlanGearLoadout(tt.p)
			if err != nil || len(l.Target) != 1 || l.Target[0].Definition != tt.want {
				t.Fatal(l, err)
			}
		})
	}
	shield := loadoutOption("shield", GearBelt)
	shield.Shield = true
	shield.Sharp = 1
	p := GearLoadoutInput{Role: GearRoleInput{DraftedSquad: true}}
	if gearEligible(p, shield) {
		t.Fatal("ranged shield")
	}
	p.Role.Work.Skills = domain.Known([]WorkSkill{{Name: "Melee", Level: 10}})
	if !gearEligible(p, shield) {
		t.Fatal("melee shield")
	}
	helm := loadoutOption("helmet", GearHeadgear)
	helm.Sharp = 1
	if gearEligible(p, helm) {
		t.Fatal("helmet before smithing")
	}
	p.Smithing = true
	if !gearEligible(p, helm) {
		t.Fatal("helmet after smithing")
	}
	p.Role = GearRoleInput{Work: WorkPawn{Work: domain.Known([]WorkPriority{{Work: WorkHunting, Priority: 1}})}}
	bow := loadoutOption("bow", GearPrimary)
	bow.Ranged = true
	bow.Range = 24
	if gearEligible(p, bow) {
		t.Fatal("short hunter weapon")
	}
	bow.Range = 25
	if !gearEligible(p, bow) {
		t.Fatal("hunter weapon")
	}
	p.Options = []GearOption{bow}
	l, err := PlanGearLoadout(p)
	if err != nil || len(l.Target) != 1 {
		t.Fatal(l, err)
	}
	p.Role.Work.Work = domain.Known([]WorkPriority{{Work: WorkCrafting, Priority: 1}})
	p.Ambient = -10
	p.Options = nil
	if got := gearItemScore(p, coat); math.Abs(got-1.98) > 1e-9 {
		t.Fatal("indoor thermal weight", got)
	}
}

func TestGearEnsembles(t *testing.T) {
	shirt := loadoutOption("shirt", GearSkinTorso)
	pants := loadoutOption("pants", GearSkinLegs)
	vest := loadoutOption("vest", GearMiddleTorso)
	shirt.Sharp = .1
	vest.Sharp = .8
	duster := loadoutOption("duster", GearOuter)
	duster.Sharp = .2
	for _, female := range []bool{false, true} {
		p := GearLoadoutInput{Female: female, Options: []GearOption{shirt, pants}}
		l, err := PlanGearLoadout(p)
		if err != nil {
			t.Fatal(err)
		}
		// Equal-scoring optional shirts may be retained in the target; male
		// uncovered chest alone must carry no penalty.
		if gearEnsembleScore(p, []GearOption{pants}) != map[bool]float64{false: 0, true: -1000}[female] {
			t.Fatal("nudity", l)
		}
	}
	p := GearLoadoutInput{Female: true, Role: GearRoleInput{DraftedSquad: true}, Options: []GearOption{shirt, pants, vest, duster}}
	l, err := PlanGearLoadout(p)
	if err != nil || len(l.Target) != 4 {
		t.Fatal(l, err)
	}
	if GearConflicts(shirt, pants) || GearConflicts(vest, duster) {
		t.Fatal("compatible layers")
	}
	robe := loadoutOption("robe", GearOuter)
	robe.Layers = []string{"OnSkin"}
	robe.Groups = []string{"Torso", "Legs"}
	if !GearConflicts(robe, shirt) || !GearConflicts(robe, pants) {
		t.Fatal("layer AND group")
	}
	p = GearLoadoutInput{Nudist: true, Options: []GearOption{shirt, pants}}
	l, err = PlanGearLoadout(p)
	if err != nil || len(l.Target) != 0 {
		t.Fatal("nudist", l, err)
	}
	kid := pants
	kid.ID = "kid"
	kid.Definition = "KidPants"
	p = GearLoadoutInput{Role: GearRoleInput{Child: true}, Options: []GearOption{pants, kid}}
	l, err = PlanGearLoadout(p)
	if err != nil || len(l.Target) != 1 || l.Target[0].ID != "kid" {
		t.Fatal("child", l, err)
	}
	shirt.Source = GearWorn
	shirt.Locked = true
	shirt.Condition = .1
	p = GearLoadoutInput{Female: true, Worn: []GearOption{shirt}, Options: []GearOption{robe}}
	l, err = PlanGearLoadout(p)
	if err != nil || len(l.Target) != 1 || l.Target[0].ID != "shirt" {
		t.Fatal("locked", l, err)
	}
}

func TestGearTainted(t *testing.T) {
	for n, want := range []float64{0, 5, 8, 11, 14, 14} {
		items := []GearOption{}
		for i := 0; i < n; i++ {
			o := loadoutOption("tainted", GearBelt)
			o.Tainted = true
			items = append(items, o)
		}
		p := GearLoadoutInput{Nudist: true}
		if got := gearEnsembleScore(p, items); got != -want {
			t.Fatal(n, got)
		}
		p.Bloodlust = true
		if gearEnsembleScore(p, items) != 0 {
			t.Fatal("bloodlust")
		}
		p.Bloodlust = false
		p.Inhuman = true
		if gearEnsembleScore(p, items) != 0 {
			t.Fatal("inhuman")
		}
	}
}

func TestGearDemandAndReview(t *testing.T) {
	pants := loadoutOption("pants", GearSkinLegs)
	pants.Stuff = "Leather"
	loose := pants
	loose.ID = "loose"
	loose.Source = GearLoose
	p := GearLoadoutInput{Options: []GearOption{pants, loose}}
	pawn := func(id PawnID) GearPawn {
		return GearPawn{Pawn: id, Loadout: "snapshot", LoadoutModel: domain.Known(p)}
	}
	rows := []GearPawn{pawn("c"), pawn("b"), pawn("a")}
	ls, d, err := PlanColonyGear(rows)
	if err != nil {
		t.Fatal(err)
	}
	want := domain.Known([]GearDemand{{Definition: "pants", Stuff: "Leather", Count: 2}})
	if !reflect.DeepEqual(d, want) || ls[0].Pawn != "a" || ls[0].Gaps[0].Source != GearLoose {
		t.Fatal(ls, d)
	}
	review, err := ReviewGear(domain.Known(GearObservation{Pawns: rows}))
	if err != nil || review.Recovered != domain.Known(false) || !reflect.DeepEqual(review.Demand, want) {
		t.Fatal(review, err)
	}
	p.Worn = []GearOption{pants}
	p.Worn[0].Source = GearWorn
	p.Options = nil
	rows = []GearPawn{pawn("a")}
	review, err = ReviewGear(domain.Known(GearObservation{Pawns: rows}))
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal(review, err)
	}
	// Unknown rich inputs preserve the old native census recovery contract.
	rows[0].LoadoutModel = domain.Unknown[GearLoadoutInput]()
	rows[0].Deficit = domain.Known(false)
	rows[0].Candidates = domain.Known([]GearCandidate{})
	review, err = ReviewGear(domain.Known(GearObservation{Pawns: rows}))
	if err != nil || review.Recovered != domain.Known(true) {
		t.Fatal(review, err)
	}
	if _, known := review.Demand.Value(); known {
		t.Fatal("invented demand")
	}
}

func TestGearLoadoutValidation(t *testing.T) {
	o := loadoutOption("pants", GearSkinLegs)
	for _, mutate := range []func(*GearOption){func(o *GearOption) { o.Quality = 7 }, func(o *GearOption) { o.Condition = math.NaN() }, func(o *GearOption) { o.Cost = -1 }, func(o *GearOption) { o.Slot = "bad" }, func(o *GearOption) { o.Source = "bad" }} {
		bad := o
		mutate(&bad)
		if _, err := PlanGearLoadout(GearLoadoutInput{Options: []GearOption{bad}}); err == nil {
			t.Fatal(bad)
		}
	}
}

func TestGearModelMethodsUseNativeAdmissionAndModelGain(t *testing.T) {
	r := gearFixture()
	v, _ := r.Observation.Value()
	worn := loadoutOption("worn", GearSkinTorso)
	worn.Source = GearWorn
	worn.Condition = .4
	weak := loadoutOption("weak", GearSkinTorso)
	weak.Source = GearLoose
	weak.Definition = "Parka"
	weak.Sharp = .1
	strong := weak
	strong.ID = "strong"
	strong.Sharp = 1
	v.Pawns[0].LoadoutModel = domain.Known(GearLoadoutInput{Female: true, Worn: []GearOption{worn}, Options: []GearOption{weak, strong}})
	v.Pawns[0].Candidates = domain.Known([]GearCandidate{{Target: "weak", Definition: "Parka", Gain: 100}, {Target: "strong", Definition: "Parka", Gain: .1}})
	r.Observation = domain.Known(v)
	m, err := SelectGearMethod(r)
	if err != nil || m.Kind != GearReplace || m.Target != "strong" {
		t.Fatal(m, err)
	}
	v.Pawns[0].Candidates = domain.Known([]GearCandidate{{Target: "weak", Definition: "Parka", Gain: 100}})
	r.Observation = domain.Known(v)
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearBlocked {
		t.Fatal("bypassed admission", m, err)
	}
	strong.Source = GearBillSource
	strong.Stuff = "Cloth"
	v.Pawns[0].LoadoutModel = domain.Known(GearLoadoutInput{Female: true, Worn: []GearOption{worn}, Options: []GearOption{strong}})
	v.Pawns[0].Candidates = domain.Known([]GearCandidate{})
	r.Observation = domain.Known(v)
	m, err = SelectGearMethod(r)
	if err != nil || m.Kind != GearProduce || m.Need.Stuff != "Cloth" {
		t.Fatal(m, err)
	}
	if got := GearReplacementNeeds(r.Observation); !reflect.DeepEqual(got, []Resource{"Parka"}) {
		t.Fatal(got)
	}
}

func TestGearDemandMaterialsQualityAndThreshold(t *testing.T) {
	o := loadoutOption("pants", GearSkinLegs)
	o.Stuff = "Cloth"
	gap := func(stuff Resource, source GearSource, quality int, gain float64) GearGap {
		copy := o
		copy.Stuff = stuff
		copy.Quality = quality
		return GearGap{Slot: o.Slot, Wanted: copy, Source: source, Gain: gain}
	}
	rows := []GearLoadout{{Role: GearWorker, Gaps: []GearGap{gap("Cloth", GearBillSource, 2, 1), gap("Cloth", GearBillSource, 5, 1), gap("Leather", GearBillSource, 2, 1), gap("Cloth", GearStored, 2, 1), gap("Cloth", GearBillSource, 2, .1)}}, {Role: GearSoldier, Gaps: []GearGap{gap("Cloth", GearBillSource, 2, .1)}}}
	want := []GearDemand{{"pants", "Cloth", 3}, {"pants", "Leather", 1}}
	if got := GearProductionDemand(rows); !reflect.DeepEqual(got, want) {
		t.Fatal(got)
	}
}

func TestGearWornOutAndCoverageAffectRecovery(t *testing.T) {
	shirt := loadoutOption("shirt", GearSkinTorso)
	shirt.Source = GearWorn
	shirt.Condition = .4
	fresh := shirt
	fresh.ID = "fresh"
	fresh.Source = GearBillSource
	fresh.Condition = 1
	pants := loadoutOption("pants", GearSkinLegs)
	p := GearPawn{Pawn: "p", Loadout: "snapshot", Deficit: domain.Known(false), Candidates: domain.Known([]GearCandidate{}), Apparel: domain.Known([]GearApparel{{Definition: "shirt", Condition: .4, Groups: []string{"Torso"}}}), LoadoutModel: domain.Known(GearLoadoutInput{Worn: []GearOption{shirt}, Options: []GearOption{fresh, pants}})}
	r, err := ReviewGear(domain.Known(GearObservation{Pawns: []GearPawn{p}}))
	if err != nil || r.Recovered != domain.Known(false) || r.WornOut != domain.Known(1.0) || r.Uncovered != domain.Known(1.0) || len(r.Loadouts[0].Gaps) != 2 {
		t.Fatal(r, err)
	}
	if r.Loadouts[0].Gaps[0].Slot != GearSkinLegs {
		t.Fatal("coverage must precede wear", r.Loadouts)
	}
}

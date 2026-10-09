package policy

import "testing"

func constrainedOptions() []GearOption {
	shirt := loadoutOption("shirt", GearSkinTorso)
	shirt.Cost, shirt.Cold = 10, 2
	pants := loadoutOption("pants", GearSkinLegs)
	pants.Cost, pants.Cold = 10, 1
	coat := loadoutOption("coat", GearOuter)
	coat.Cost, coat.Cold = 50, 18
	parka := loadoutOption("parka", GearOuter)
	parka.Cost, parka.Cold = 300, 40
	return []GearOption{shirt, pants, coat, parka}
}

func constrainedPick(t *testing.T, p GearLoadoutInput) map[string]bool {
	t.Helper()
	l, err := PlanGearLoadout(p)
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]bool{}
	for _, o := range l.Target {
		got[o.ID] = true
	}
	return got
}

// Slaves, prisoners and unrevealed creepjoiners cover legs and torso,
// meet the thermal need, then pay the least. The old slave score left a cold
// slave in the cheapest shirt and pants.
func TestGearConstraintModeDressesWarmAtMinimumCost(t *testing.T) {
	for name, role := range map[string]GearRoleInput{"slave": {Slave: true}, "prisoner": {Prisoner: true}, "creepjoiner": {UnrevealedCreepjoiner: true}} {
		t.Run(name, func(t *testing.T) {
			p := GearLoadoutInput{Role: role, Ambient: -10, ComfortableMin: 10, ComfortableMax: 30, Options: constrainedOptions()}
			// Need 20: shirt 2 + pants 1 + coat 18 meets it for 70; the parka costs 300.
			got := constrainedPick(t, p)
			if len(got) != 3 || !got["shirt"] || !got["pants"] || !got["coat"] {
				t.Fatal(got)
			}
			// A mild day needs no outerwear: cover legs and torso, nothing more.
			p.Ambient = 20
			if got := constrainedPick(t, p); len(got) != 2 || !got["shirt"] || !got["pants"] {
				t.Fatal("mild", got)
			}
			// The torso is covered for any wearer, not only women.
			p.Options = p.Options[:1]
			if got := constrainedPick(t, p); len(got) != 1 || !got["shirt"] {
				t.Fatal("torso", got)
			}
		})
	}
}

func TestGearConstraintModeMeetsCapacityWhenNeedExceedsIt(t *testing.T) {
	scarf := loadoutOption("scarf", GearHeadgear)
	scarf.Cost, scarf.Cold = 5, 1
	opts := append(constrainedOptions()[:2], scarf)
	got := constrainedPick(t, GearLoadoutInput{Role: GearRoleInput{Slave: true}, Ambient: -10, ComfortableMin: 40, ComfortableMax: 50, Options: opts})
	if len(got) != 3 {
		t.Fatal("takes all the insulation the options offer", got)
	}
}

func TestGearConstraintModeExcludesMoodHurtingApparel(t *testing.T) {
	pants := loadoutOption("pants", GearSkinLegs)
	pants.Cost = 5
	dead := pants
	dead.ID, dead.Tainted, dead.Cost = "dead", true, 1
	shirt := loadoutOption("shirt", GearSkinTorso)
	shirt.Cost = 5
	collar := pants
	collar.ID, collar.SlaveOnly, collar.Cost = "slavepants", true, 1
	opts := []GearOption{pants, dead, collar, shirt}
	for _, tt := range []struct {
		name string
		p    GearLoadoutInput
		want string
	}{
		{"slave", GearLoadoutInput{Role: GearRoleInput{Slave: true}}, "slavepants"},
		{"prisoner", GearLoadoutInput{Role: GearRoleInput{Prisoner: true}}, "pants"},
		{"creepjoiner", GearLoadoutInput{Role: GearRoleInput{UnrevealedCreepjoiner: true}}, "pants"},
		{"taint free prisoner", GearLoadoutInput{Role: GearRoleInput{Prisoner: true}, TaintFree: true}, "dead"},
	} {
		t.Run(tt.name, func(t *testing.T) {
			tt.p.Options = opts
			got := constrainedPick(t, tt.p)
			if len(got) != 2 || !got[tt.want] || !got["shirt"] {
				t.Fatal(got)
			}
		})
	}
	// Worn tainted legs are replaced rather than kept for being free.
	worn := dead
	worn.Source = GearWorn
	l, err := PlanGearLoadout(GearLoadoutInput{Role: GearRoleInput{Prisoner: true}, Worn: []GearOption{worn}, Options: []GearOption{pants, shirt}})
	if err != nil || len(l.Gaps) != 2 {
		t.Fatal(l, err)
	}
}

func TestGearPrisonerRole(t *testing.T) {
	for _, in := range []GearRoleInput{{Prisoner: true}, {UnrevealedCreepjoiner: true}, {Prisoner: true, DraftedSquad: true}} {
		if DeriveGearRole(in) != GearPrisoner {
			t.Fatal(in)
		}
	}
	if DeriveGearRole(GearRoleInput{Prisoner: true, Slave: true}) != GearSlave || DeriveGearRole(GearRoleInput{Prisoner: true, Child: true}) != GearChild {
		t.Fatal("slave and child precede prisoner")
	}
}

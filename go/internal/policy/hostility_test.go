package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hostilityPawn(current domain.HostilityResponse) HostilityPawn {
	return HostilityPawn{ID: "p", Current: domain.Known(current), ViolenceCapable: domain.Known(true), Age: domain.Known(30.0), Health: domain.Known(1.0)}
}

func TestDefaultHostility(t *testing.T) {
	for name, c := range map[string]struct {
		edit func(*HostilityPawn)
		want domain.HostilityResponse
		ok   bool
	}{
		"fighter attacks":      {func(*HostilityPawn) {}, domain.HostilityAttack, true},
		"pacifist flees":       {func(p *HostilityPawn) { p.ViolenceCapable = domain.Known(false) }, domain.HostilityFlee, true},
		"child flees":          {func(p *HostilityPawn) { p.Age = domain.Known(9.0) }, domain.HostilityFlee, true},
		"teen attacks":         {func(p *HostilityPawn) { p.Age = domain.Known(15.0) }, domain.HostilityAttack, true},
		"blood loss flees":     {func(p *HostilityPawn) { p.BloodLoss = domain.Known(0.35) }, domain.HostilityFlee, true},
		"minor bleed attacks":  {func(p *HostilityPawn) { p.BloodLoss = domain.Known(0.1) }, domain.HostilityAttack, true},
		"badly hurt flees":     {func(p *HostilityPawn) { p.Health = domain.Known(0.5) }, domain.HostilityFlee, true},
		"unknown violence":     {func(p *HostilityPawn) { p.ViolenceCapable = domain.Unknown[bool]() }, "", false},
		"unknown but pacifist": {func(p *HostilityPawn) { p.ViolenceCapable, p.Health = domain.Known(false), domain.Unknown[float64]() }, domain.HostilityFlee, true},
	} {
		t.Run(name, func(t *testing.T) {
			p := hostilityPawn(domain.HostilityAttack)
			c.edit(&p)
			got, ok := DefaultHostility(p)
			if got != c.want || ok != c.ok {
				t.Fatal(got, ok)
			}
		})
	}
}

func jellyJob(cell domain.Cell) domain.Fact[PawnJob] {
	return domain.Known(PawnJob{Def: "HaulToCell", Work: "Hauling", Target: domain.Known(JobTarget{Thing: "InsectJelly1", Cell: domain.Known(cell)})})
}

func hive(cell domain.Cell, passive bool) EmergencyThreat {
	return EmergencyThreat{ID: "Hive1", Kind: HostileBuilding, Dead: domain.Known(false), Passive: domain.Known(passive), Cells: []domain.Cell{cell}, Distance: domain.Known(10.0)}
}

// Ignore is task-scoped (#1299): set while a work job sits beside a
// sleeping hive, restored when the job ends, the hive wakes or an engaging
// hostile comes near.
func TestHostilityIgnoreAndRestore(t *testing.T) {
	site := domain.Cell{X: 50, Z: 50}
	sleeping := []EmergencyThreat{hive(domain.Cell{X: 55, Z: 52}, true)}
	jelly := hostilityPawn(domain.HostilityAttack)
	jelly.Job = jellyJob(site)
	if got := HostilityChanges([]HostilityPawn{jelly}, sleeping); len(got) != 1 || got[0].Hostility() != domain.HostilityIgnore {
		t.Fatal("jelly beside a sleeping hive", got)
	}
	ignoring := jelly
	ignoring.Current = domain.Known(domain.HostilityIgnore)
	if got := HostilityChanges([]HostilityPawn{ignoring}, sleeping); len(got) != 0 {
		t.Fatal("held Ignore rewritten", got)
	}
	restore := func(name string, p HostilityPawn, threats []EmergencyThreat) {
		got := HostilityChanges([]HostilityPawn{p}, threats)
		if len(got) != 1 || got[0].Hostility() != domain.HostilityAttack {
			t.Fatal(name, got)
		}
	}
	ended := ignoring
	ended.Job = domain.Known(PawnJob{Def: "LayDown"})
	restore("job ended", ended, sleeping)
	moved := ignoring
	moved.Job = jellyJob(domain.Cell{X: 5, Z: 5})
	restore("job moved away", moved, sleeping)
	restore("hive woke", ignoring, []EmergencyThreat{hive(domain.Cell{X: 55, Z: 52}, false)})
	raider := EmergencyThreat{ID: "Raider", Kind: Hostile, Dead: domain.Known(false), Position: domain.Known(domain.Cell{X: 48, Z: 60})}
	restore("threatened", ignoring, append([]EmergencyThreat{raider}, sleeping...))
	pacifist := jelly
	pacifist.ViolenceCapable = domain.Known(false)
	if got := HostilityChanges([]HostilityPawn{pacifist}, sleeping); len(got) != 1 || got[0].Hostility() != domain.HostilityFlee {
		t.Fatal("a Flee pawn keeps fleeing beside sleeping hostiles", got)
	}
}

func TestHostilityOwedUnknownOwesNothing(t *testing.T) {
	if _, known := HostilityOwed(domain.Unknown[[]HostilityPawn](), nil).Value(); known {
		t.Fatal("unknown census owed")
	}
	unknown := hostilityPawn(domain.HostilityFlee)
	unknown.Current = domain.Unknown[domain.HostilityResponse]()
	if owed, _ := HostilityOwed(domain.Known([]HostilityPawn{unknown}), nil).Value(); owed {
		t.Fatal("pawn without a configurable response owed")
	}
	if owed, _ := HostilityOwed(domain.Known([]HostilityPawn{hostilityPawn(domain.HostilityFlee)}), nil).Value(); !owed {
		t.Fatal("fleeing fighter not owed")
	}
}

// A dormant mech cluster is a sleeping hostile like a hive (#1335): a
// salvage haul within reach of its sleeping mech gets Ignore, and the pawn
// reverts to Attack once the cluster wakes.
func TestHostilityDormantMechCluster(t *testing.T) {
	site := domain.Cell{X: 50, Z: 50}
	mech := func(passive bool) []EmergencyThreat {
		return []EmergencyThreat{{ID: "Mech_Scyther1", Kind: Hostile, Dead: domain.Known(false), Passive: domain.Known(passive),
			Position: domain.Known(domain.Cell{X: 60, Z: 45}), Distance: domain.Known(12.0)}}
	}
	hauler := hostilityPawn(domain.HostilityAttack)
	hauler.Job = domain.Known(PawnJob{Def: "HaulToCell", Work: "Hauling", Target: domain.Known(JobTarget{Thing: "Steel1", Cell: domain.Known(site)})})
	if got := HostilityChanges([]HostilityPawn{hauler}, mech(true)); len(got) != 1 || got[0].Hostility() != domain.HostilityIgnore {
		t.Fatal("haul beside a dormant mech", got)
	}
	hauler.Current = domain.Known(domain.HostilityIgnore)
	if got := HostilityChanges([]HostilityPawn{hauler}, mech(false)); len(got) != 1 || got[0].Hostility() != domain.HostilityAttack {
		t.Fatal("cluster woke", got)
	}
}

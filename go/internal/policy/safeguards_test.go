package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSafeguardsVetoByPauseAndEmergency(t *testing.T) {
	calm := SafeguardContext{Enabled: true}
	fire := SafeguardContext{Enabled: true, Emergency: []ConcernID{MaintainFireSafety}}
	for _, c := range []struct {
		name string
		ctx  SafeguardContext
		p    SafeguardProposal
		want string
	}{
		{"calm admits", calm, SafeguardProposal{Need: MaintainResource, Priority: 3}, ""},
		{"pause vetoes routine work", SafeguardContext{}, SafeguardProposal{Need: MaintainResource, Priority: 3}, "control paused"},
		{"pause vetoes urgent work too", SafeguardContext{}, SafeguardProposal{Need: ActiveCombat, Priority: 0}, "control paused"},
		{"emergency vetoes priority 2", fire, SafeguardProposal{Need: EnsureCooking, Priority: 2}, "emergency MaintainFireSafety"},
		{"priority below 2 is exempt", fire, SafeguardProposal{Need: RestoreWorkers, Priority: 1}, ""},
		{"the emergency need is exempt", fire, SafeguardProposal{Need: MaintainFireSafety, Priority: 4}, ""},
	} {
		if got := VetoProposal(c.ctx, c.p); !strings.HasPrefix(got, c.want) || (c.want == "") != (got == "") {
			t.Errorf("%s: veto %q, want %q", c.name, got, c.want)
		}
	}
}

func billActions(t *testing.T, mode domain.BillMode) []domain.Action {
	t.Helper()
	bill, err := domain.NewProductionBill("bench", "Make_Bow_Short", mode, 1)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("a-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	return []domain.Action{action}
}

// The hunters' weapon craft is the one proposal an emergency does not
// suspend: a gear-batch bill owned by EnsureFoodSupply (a priority-2 need, so
// ownership alone cannot exempt it). Every other priority >= 2 need stays
// suspended, food's own work included, whatever it proposes.
func TestEmergencyExemptsOnlyTheHunterWeaponCraft(t *testing.T) {
	fire := SafeguardContext{Enabled: true, Emergency: []ConcernID{MaintainFireSafety}}
	craft := billActions(t, domain.GearBatch)
	if !HunterWeaponCraft(EnsureFoodSupply, craft) {
		t.Fatal("food's gear-batch bill is the hunters' weapon craft")
	}
	for name, ok := range map[string]bool{
		"other need": HunterWeaponCraft(MaintainEquipment, craft),
		"other bill": HunterWeaponCraft(EnsureFoodSupply, billActions(t, domain.StockTarget)),
		"no actions": HunterWeaponCraft(EnsureFoodSupply, nil),
	} {
		if ok {
			t.Errorf("%s is not the hunters' weapon craft", name)
		}
	}
	if got := VetoProposal(fire, SafeguardProposal{Need: EnsureFoodSupply, Priority: 2, HunterWeapons: HunterWeaponCraft(EnsureFoodSupply, craft)}); got != "" {
		t.Errorf("the hunters' weapon craft is vetoed: %q", got)
	}
	for _, need := range []ConcernID{EnsureFoodSupply, MaintainEquipment, EnsureCooking, MaintainResource, EnsureBasicDefense} {
		for _, priority := range []int{2, 3, 4} {
			p := SafeguardProposal{Need: need, Priority: priority, HunterWeapons: HunterWeaponCraft(need, billActions(t, domain.StockTarget))}
			if VetoProposal(fire, p) == "" {
				t.Errorf("%s at priority %d is un-suspended", need, priority)
			}
		}
	}
	if VetoProposal(fire, SafeguardProposal{Need: MaintainEquipment, Priority: 3, HunterWeapons: HunterWeaponCraft(MaintainEquipment, craft)}) == "" {
		t.Error("the equipment craft is un-suspended")
	}
}

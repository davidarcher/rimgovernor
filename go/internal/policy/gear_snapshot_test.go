package policy_test

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Replaces the weapon half of the native gear/soldier case (#471, #748):
// two unarmed soldiers, Thing_Human724 at Shooting 12 and Thing_Human726
// at Shooting 8 (test/gear_area_prepare mode "soldier" on the 11x11
// starter site), with a bolt-action rifle and a pump shotgun loose. The
// equip step's weapon assignment reads outside the routine review, so its
// AssignEquip input was recorded by a temporary dump at the policy call
// (buildingruntime RoutineEquipPlanner) during `acceptance run
// gear/soldier` at 4e86b4663. In that run both soldiers were downed when
// the equip step first planned (the case failed: nobody ended armed or
// armored), so the recording hands the guns to others; the test stands
// the soldiers back up and asserts the fit the case asserted. The armor
// half is snapshot.TestGearSoldierDraftedPlansFlakVestAndHelmet (#979).
func TestGearSoldierWeaponFitBySkill(t *testing.T) {
	var in struct {
		Pawns       []policy.EquipCandidatePawn
		Weapons     []policy.EquipCandidateWeapon
		Assignments []policy.EquipAssignment
	}
	loadRecorded(t, "testdata/gear-soldier-equip.json", &in)
	const marksman, second = "Thing_Human724", "Thing_Human726"
	for i := range in.Pawns {
		if p := &in.Pawns[i]; p.Pawn == marksman || p.Pawn == second {
			if downed, _ := p.Downed.Value(); !downed {
				t.Fatalf("%s is not downed in the recording; drop the variant", p.Pawn)
			}
			p.Downed = domain.Known(false)
		}
	}
	// The recording predates the weapon def rows (#1723): state the three
	// defs it holds as the Core XML derives them.
	facts := map[string]policy.WeaponDef{
		"Gun_BoltActionRifle": {Ranged: true, Range: 36.9, DPS: 5.625, AP: .27, Precision: true},
		"Gun_PumpShotgun":     {Ranged: true, Range: 15.9, DPS: 8.372, AP: .14},
		"WoodLog":             {Melee: true, Blunt: true, DPS: 5.75, AP: .15},
	}
	for i := range in.Weapons {
		in.Weapons[i].Facts = facts[in.Weapons[i].Definition]
	}
	for i := range in.Pawns {
		if cur := in.Pawns[i].Current; cur != nil {
			cur.Facts = facts[cur.Definition]
		}
	}
	held := map[domain.PawnID]string{}
	for _, a := range policy.AssignEquip(in.Pawns, in.Weapons) {
		held[a.Pawn] = a.Weapon.Definition
	}
	if held[marksman] != "Gun_BoltActionRifle" || held[second] != "Gun_PumpShotgun" {
		t.Fatalf("Shooting 12 gets %q and Shooting 8 gets %q; want the rifle then the shotgun", held[marksman], held[second])
	}
}

package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Burning the incinerator: the room is meant to be full
// and burned whole, so MaintainIncineration lights it once per batch, never per
// item. PlanBurn decides one burn from the census; the planner turns a ready
// order into equip (when the burner holds no molotov), draft and ignite
// with the existing action kinds, the combat planner's undraft
// releasing the burner once the plan settles.

const (
	// MolotovDef is the burner's weapon, stocked loose by the armory.
	MolotovDef = "Weapon_GrenadeMolotov"
	// AshDef is the filth a burned room leaves behind.
	AshDef = "Filth_Ash"
	// BurnStoredCells is the batch: interior cells holding things (two
	// thirds of the 3x3 room) before the room is lit.
	BurnStoredCells = 6
)

// BurnVerdict is why a burn is or is not ordered.
type BurnVerdict string

const (
	BurnReady      BurnVerdict = "ready"
	BurnBelowBatch BurnVerdict = "below_batch"
	BurnFireActive BurnVerdict = "fire_active"
	BurnOccupied   BurnVerdict = "room_occupied"
	BurnNoBurner   BurnVerdict = "no_burner"
	BurnNoStandby  BurnVerdict = "no_firefighting_standby"
	BurnNoMolotov  BurnVerdict = "no_molotov"
)

// BurnCandidate is one colonist as a burner and as a firefighter.
type BurnCandidate struct {
	Arm  EquipCandidatePawn
	Fire FireSafetyPawnFacts
	// Holds is a molotov in the primary slot already.
	Holds bool
}

// BurnRequest is the incinerator's census. Fire is true while any fire is
// burning or the fire census is unread.
type BurnRequest struct {
	Interior Rectangle
	Stored   int
	Fire     bool
	Pawns    []BurnCandidate
	// Molotovs are the loose molotovs on the map.
	Molotovs []EquipCandidateWeapon
}

// BurnOrder is a ready burn: the burner, the loose molotov it picks up (nil
// when it holds one) and the cell it throws at, the room's centre.
type BurnOrder struct {
	Burner  domain.PawnID
	Molotov *EquipCandidateWeapon
	Target  domain.Cell
}

// PlanBurn orders one burn when the batch is met, nothing burns and no
// colonist stands in the room. The burner is the lowest-ID colonist able to
// fire a molotov (a holder first), and only while another colonist stands by
// to fight fire. Native does not re-check occupancy; this census (colonists)
// is the only guard.
func PlanBurn(r BurnRequest) (BurnOrder, BurnVerdict) {
	switch {
	case r.Stored < BurnStoredCells:
		return BurnOrder{}, BurnBelowBatch
	case r.Fire:
		return BurnOrder{}, BurnFireActive
	}
	for _, p := range r.Pawns {
		if r.inside(p.Arm.Position) {
			return BurnOrder{}, BurnOccupied
		}
	}
	pawns := append([]BurnCandidate(nil), r.Pawns...)
	sort.Slice(pawns, func(i, j int) bool {
		if pawns[i].Holds != pawns[j].Holds {
			return pawns[i].Holds
		}
		return pawns[i].Arm.Pawn < pawns[j].Arm.Pawn
	})
	verdict := BurnNoBurner
	for _, burner := range pawns {
		if !canBurn(burner.Arm) {
			continue
		}
		if !standby(pawns, burner.Arm.Pawn) {
			verdict = BurnNoStandby
			continue
		}
		order := BurnOrder{Burner: burner.Arm.Pawn, Target: domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}}
		if burner.Holds {
			return order, BurnReady
		}
		molotov, ok := r.molotovFor(burner.Arm.Pawn)
		if !ok {
			return BurnOrder{}, BurnNoMolotov
		}
		order.Molotov = &molotov
		return order, BurnReady
	}
	return BurnOrder{}, verdict
}

func (r BurnRequest) inside(c domain.Cell) bool {
	i := r.Interior
	return c.X >= i.X && c.X < i.X+i.Width && c.Z >= i.Z && c.Z < i.Z+i.Height
}

// canBurn is a colonist standing, free, able to fire and allowed arms.
func canBurn(p EquipCandidatePawn) bool {
	dead, dk := p.Dead.Value()
	downed, wk := p.Downed.Value()
	drafted, tk := p.Drafted.Value()
	mental, mk := p.MentalState.Value()
	incapable, vk := p.IncapableOfViolence.Value()
	return dk && wk && tk && mk && vk && !dead && !downed && !drafted && !mental && !incapable && !p.ShootingDisabled && p.NoArms == ""
}

// standby reports a colonist other than burner who would fight a fire.
func standby(pawns []BurnCandidate, burner domain.PawnID) bool {
	for _, p := range pawns {
		if p.Arm.Pawn != burner && fireSafetyEligible(p.Fire) {
			return true
		}
	}
	return false
}

// molotovFor is the lowest-ID loose molotov the burner may carry.
func (r BurnRequest) molotovFor(burner domain.PawnID) (EquipCandidateWeapon, bool) {
	var best EquipCandidateWeapon
	found := false
	for _, w := range r.Molotovs {
		if w.Definition != MolotovDef || w.Biocoded && w.BiocodedTo != burner {
			continue
		}
		if !found || w.Thing < best.Thing {
			best, found = w, true
		}
	}
	return best, found
}

// IncineratorAsh is the ash lying in the interior, by ID: the cleanup a burn
// leaves.
func IncineratorAsh(filth []UpkeepFilth, interior Rectangle) []UpkeepFilth {
	room := BurnRequest{Interior: interior}
	var out []UpkeepFilth
	for _, f := range filth {
		if f.Definition == AshDef && room.inside(f.Cell) {
			out = append(out, f)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EquipPawnUnavailable mirrors HaulerUnavailable/RescuerUnavailable: the pawn
// is occupied, mid a conflicting player order, or already equipping.
const EquipPawnUnavailable Reason = "equip_pawn_unavailable"

// EquipCandidatePawn describes an observed colonist and optional combat facts.
// Selection is a proposal; native admission rechecks the exact pair.
type EquipCandidatePawn struct {
	Pawn                               domain.PawnID
	Dead, Downed, Drafted, MentalState domain.Fact[bool]
	IncapableOfViolence, Armed         domain.Fact[bool]
	Position                           domain.Cell
	Profile                            PawnProfile
	ShootingDisabled                   bool
	Role                               WeaponRole
	// LoneFighter must be explicitly known before assigning area-fire weapons.
	LoneFighter bool
	RaidArmor   domain.Fact[float64]
	// Current is scored only when automation owns the exact equipped identity.
	Current         *EquipCandidateWeapon
	AutomationOwned bool
}

// EquipCandidateWeapon describes one already-observed loose weapon.
type EquipCandidateWeapon struct {
	Thing, Definition string
	Cell              domain.Cell
	Class             WeaponClass
	BiocodedTo        domain.PawnID
}

// WeaponClass ranks a loose equippable by what it is for. The native
// "weapons" census is ThingDef.IsWeapon, which includes anything a pawn can
// swing (a wood log, a beer): colony-6 handed a colonist the wood log at its
// feet with two short bows a few cells away. The class is the definition's
// observed flags (#287), never its name: IsMeleeWeapon is true of every
// equippable that is not ranged, so a melee weapon by trade is one the
// Weapons thing category lists.
type WeaponClass int

const (
	// WeaponMakeshift: equippable but not a weapon by trade (WoodLog, Beer).
	WeaponMakeshift WeaponClass = iota
	// WeaponMelee: IsMeleeWeapon and in the Weapons category.
	WeaponMelee
	// WeaponRanged: IsRangedWeapon; what hunting needs.
	WeaponRanged
)

// ClassifyWeapon maps the observed definition flags onto a class.
func ClassifyWeapon(byTrade, ranged, melee bool) WeaponClass {
	switch {
	case ranged:
		return WeaponRanged
	case melee && byTrade:
		return WeaponMelee
	}
	return WeaponMakeshift
}

// SelectEquip returns the first pair from the colony-wide assignment.
// Call AssignEquip to dispatch the whole wave. Admission remains native.
func SelectEquip(pawns []EquipCandidatePawn, weapons []EquipCandidateWeapon) (domain.PawnID, EquipCandidateWeapon, bool) {
	pairs := AssignEquip(pawns, weapons)
	if len(pairs) == 0 {
		return "", EquipCandidateWeapon{}, false
	}
	return pairs[0].Pawn, pairs[0].Weapon, true
}

func distanceSquared(a, b domain.Cell) int64 {
	dx, dz := int64(a.X-b.X), int64(a.Z-b.Z)
	return dx*dx + dz*dz
}

// EquipPawnFacts describes the one already-selected undrafted pawn.
type EquipPawnFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
	ExistingJobDef        domain.Fact[string]
}

type EquipFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  EquipPawnFacts
	ThingSnapshotToken    string
	NativeCanTry          domain.Fact[bool]
}

type EquipRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       EquipFacts
}

// EvaluateEquip re-validates one already-selected pawn/weapon pair immediately
// before dispatch. Admission proves eligibility now; it does not prove the
// equip job will be issued, accepted or that the pawn ends up holding the
// weapon (that is observed later).
func EvaluateEquip(r EquipRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	equip, ok := r.Action.Equip()
	canonical, err := domain.NewEquipAction(r.Action.ID(), equip)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	// The admission anchors on the preview tick, the inspection's one live
	// read; the pawn row may come from the step's fact cache up to the
	// planning tolerance behind it under a running window (#306, #323).
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.PawnTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != equip.Pawn() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.ThingSnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingJob, known := f.Pawn.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	if dead || downed {
		return refuse(EquipPawnUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	if existingJob == "Equip" {
		return refuse(EquipPawnUnavailable)
	}
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}

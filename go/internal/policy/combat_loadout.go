package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LoadoutThreat is the threat a fight's loadout answers.
type LoadoutThreat string

const (
	LoadoutNone LoadoutThreat = ""
	// LoadoutPods: drop pods inside the base; close range, high DPS.
	LoadoutPods LoadoutThreat = "pods"
	// LoadoutTribal: tribal raiders; out-range their bows with 32+ tiles.
	LoadoutTribal LoadoutThreat = "tribal"
	// LoadoutEMP: mechs or shielded melee; one EMP carrier per squad.
	LoadoutEMP LoadoutThreat = "emp"
	// LoadoutPrisonBreak: wardens take blunt weapons to down, not kill.
	LoadoutPrisonBreak LoadoutThreat = "prison_break"
)

// Loadout thresholds from the wiki's defense tactics guide.
const (
	loadoutTribalRange = 32 // out-ranges a great bow (30)
	loadoutPodsRange   = 26 // a close-range weapon: shotgun, SMG, pistol
)

// ClassifyLoadoutThreat names the loadout a fight's view calls for. A
// prison break is not in the combat view; the caller says so.
func ClassifyLoadoutThreat(view CombatView, prisonBreak bool) LoadoutThreat {
	if prisonBreak {
		return LoadoutPrisonBreak
	}
	if _, ok := view.Pods.Value(); ok {
		return LoadoutPods
	}
	hostile := map[domain.PawnID]bool{}
	for _, t := range view.Threats {
		hostile[domain.PawnID(t.ID)] = !t.Building && !positive(t.Dead) && !positive(t.Downed)
	}
	tribal := false
	for _, p := range view.Pawns {
		if !hostile[p.ID] || p.Dead || p.Downed {
			continue
		}
		if p.Mech {
			return LoadoutEMP
		}
		if _, shielded := p.Shield.Value(); shielded && !p.WeaponFacts.Ranged {
			return LoadoutEMP
		}
		tribal = tribal || strings.HasPrefix(p.Kind, "Tribal_")
	}
	if tribal {
		return LoadoutTribal
	}
	return LoadoutNone
}

// LoadoutDefender is one defender the loadout may re-equip.
type LoadoutDefender struct {
	EquipCandidatePawn
	// Squad groups defenders for the one-EMP-carrier rule; "" is one squad.
	Squad string
	// Warden is a pawn with Warden work enabled (the prison-break lane).
	Warden bool
	// Primary is the equipped primary's def, "" unarmed; ShieldBelt a worn
	// shield belt.
	Primary    string
	ShieldBelt bool
	// PrimaryFacts is the primary's def rows.
	PrimaryFacts WeaponDef
}

// LoadoutApparel is one stocked, unworn shield belt.
type LoadoutApparel struct {
	Thing, Definition string
	Cell              domain.Cell
}

// LoadoutOrder is one equip (Wear false) or wear (Wear true) order, sent
// through the existing equip and gear actions.
type LoadoutOrder struct {
	Pawn              domain.PawnID
	Thing, Definition string
	Cell              domain.Cell
	Wear              bool
}

// ThreatLoadout is the loadout decision: (threat, stocked
// gear, defenders) → equip orders before a fight's first order. Each thing
// and pawn is used once; ties break by pawn then thing id.
//   - pods: every non-melee defender takes the highest-DPS weapon of range
//     at most loadoutPodsRange that beats its primary.
//   - tribal: every non-melee defender whose primary is under
//     loadoutTribalRange takes the highest-DPS weapon of at least that range.
//   - emp: each squad without an EMP primary gets one carrier, its
//     best shooter among non-melee defenders, holding an EMP weapon.
//   - prison break: wardens not holding a blunt weapon take one.
//
// Whatever the threat, a melee fighter without a shield belt wears a
// stocked one (a shield belt blocks its wearer's own shots).
func ThreatLoadout(threat LoadoutThreat, defenders []LoadoutDefender, weapons []EquipCandidateWeapon, belts []LoadoutApparel) []LoadoutOrder {
	defenders = append([]LoadoutDefender(nil), defenders...)
	sort.Slice(defenders, func(i, j int) bool { return defenders[i].Pawn < defenders[j].Pawn })
	weapons = append([]EquipCandidateWeapon(nil), weapons...)
	sort.Slice(weapons, func(i, j int) bool { return weapons[i].Thing < weapons[j].Thing })
	used := map[string]bool{}
	var out []LoadoutOrder
	take := func(d LoadoutDefender, fits func(EquipCandidateWeapon) bool, better func(a, b EquipCandidateWeapon) bool) {
		best := -1
		for i, w := range weapons {
			if used[w.Thing] || !fits(w) || ScoreWeapon(loadoutPawn(d), w) <= 0 && !w.Facts.EMP {
				continue
			}
			if best < 0 || better(w, weapons[best]) {
				best = i
			}
		}
		if best >= 0 {
			w := weapons[best]
			used[w.Thing] = true
			out = append(out, LoadoutOrder{Pawn: d.Pawn, Thing: w.Thing, Definition: w.Definition, Cell: w.Cell})
		}
	}
	byDPS := func(a, b EquipCandidateWeapon) bool { return a.Facts.DPS > b.Facts.DPS }
	switch threat {
	case LoadoutPods, LoadoutTribal:
		for _, d := range defenders {
			if !loadoutReady(d) || loadoutMelee(d) {
				continue
			}
			current := d.PrimaryFacts
			var fits func(EquipCandidateWeapon) bool
			if threat == LoadoutPods {
				fits = func(w EquipCandidateWeapon) bool {
					p := w.Facts
					return w.Class == WeaponRanged && !p.ForcedMiss && p.Range <= loadoutPodsRange && p.DPS > current.DPS
				}
			} else {
				if d.Primary != "" && current.Range >= loadoutTribalRange {
					continue
				}
				fits = func(w EquipCandidateWeapon) bool {
					p := w.Facts
					return w.Class == WeaponRanged && !p.ForcedMiss && p.Range >= loadoutTribalRange
				}
			}
			take(d, fits, byDPS)
		}
	case LoadoutEMP:
		carried := map[string]bool{}
		for _, d := range defenders {
			if d.PrimaryFacts.EMP {
				carried[d.Squad] = true
			}
		}
		carrier := map[string]LoadoutDefender{}
		for _, d := range defenders {
			if carried[d.Squad] || !loadoutReady(d) || loadoutMelee(d) {
				continue
			}
			if c, ok := carrier[d.Squad]; !ok || shooting(d) > shooting(c) {
				carrier[d.Squad] = d
			}
		}
		squads := make([]string, 0, len(carrier))
		for s := range carrier {
			squads = append(squads, s)
		}
		sort.Strings(squads)
		for _, s := range squads {
			// The grenade first (EMP is thrown as the carrier's equipped
			// primary): the shorter-ranged EMP weapon is the grenade.
			take(carrier[s], func(w EquipCandidateWeapon) bool { return w.Facts.EMP }, func(a, b EquipCandidateWeapon) bool {
				return a.Facts.Range < b.Facts.Range
			})
		}
	case LoadoutPrisonBreak:
		for _, d := range defenders {
			if d.Warden && loadoutReady(d) && !d.PrimaryFacts.Blunt {
				take(d, func(w EquipCandidateWeapon) bool { return w.Facts.Blunt }, byDPS)
			}
		}
	}
	belts = append([]LoadoutApparel(nil), belts...)
	sort.Slice(belts, func(i, j int) bool { return belts[i].Thing < belts[j].Thing })
	next := 0
	for _, d := range defenders {
		if next >= len(belts) || d.ShieldBelt || !loadoutReady(d) || !loadoutMelee(d) {
			continue
		}
		b := belts[next]
		next++
		out = append(out, LoadoutOrder{Pawn: d.Pawn, Thing: b.Thing, Definition: b.Definition, Cell: b.Cell, Wear: true})
	}
	return out
}

// loadoutPawn scores a loadout pick as a lone fighter so the EMP and area
// weapons the threat rules name are not zeroed out.
func loadoutPawn(d LoadoutDefender) EquipCandidatePawn {
	p := d.EquipCandidatePawn
	p.LoneFighter = true
	return p
}

// loadoutReady is a live, capable defender not in a mental state. Drafted
// is fine: the loadout runs as the fight starts.
func loadoutReady(d LoadoutDefender) bool {
	for _, f := range []domain.Fact[bool]{d.Dead, d.Downed, d.MentalState, d.IncapableOfViolence} {
		if v, known := f.Value(); !known || v {
			return false
		}
	}
	return d.Role != WeaponRoleNonCombatant && d.NoArms == ""
}

// loadoutMelee is a melee fighter: the melee role, a melee-only pawn, or
// one holding a melee weapon.
func loadoutMelee(d LoadoutDefender) bool {
	if d.Role == WeaponRoleMelee || d.Profile.Effects.MeleeOnly || d.ShootingDisabled {
		return true
	}
	return d.PrimaryFacts.Melee
}

func shooting(d LoadoutDefender) int { return d.Profile.Skills["Shooting"].Level }

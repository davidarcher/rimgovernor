package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HuntProjectile is a verb's default projectile class (Catalog.HuntWeapon):
// Bullet is exactly the game's Bullet class (arrows included), Other any
// other class; None is no projectile.
type HuntProjectile string

const (
	HuntProjectileNone   HuntProjectile = ""
	HuntProjectileBullet HuntProjectile = "bullet"
	HuntProjectileOther  HuntProjectile = "other"
)

// flameDamageWorker is the damage worker class of incendiary shots.
const flameDamageWorker = "Verse.DamageWorker_Flame"

// huntReach is the squared distance within which a colonist hunts food prey
// (100 cells); a pest is hunted wherever it is on the map.
const huntReachSquared = 10000

// HuntVerb is one verb of a hunter's primary weapon, raw.
type HuntVerb struct {
	Melee, AIWeapon                bool
	Range, ExplosionRadius, Warmup float64
	Projectile                     HuntProjectile
	DamageDef, DamageWorker        string
}

// HuntWeapon is a hunter's primary weapon, raw; a hunter without one is unarmed.
type HuntWeapon struct {
	DefName       string
	Ranged, Melee bool
	Verbs         []HuntVerb
}

// Hunts reports the vanilla hunting weapon: ranged, with at least one attack
// verb and every attack verb a damaging projectile (bullet or arrow, no blast
// radius, no flame damage).
func (w HuntWeapon) Hunts() bool {
	if !w.Ranged {
		return false
	}
	n := 0
	for _, v := range w.Verbs {
		if v.Melee || !v.AIWeapon {
			continue
		}
		n++
		if v.Projectile != HuntProjectileBullet || v.ExplosionRadius != 0 || v.DamageDef == "" || v.DamageWorker == flameDamageWorker {
			return false
		}
	}
	return n > 0
}

// HuntHunter is one free colonist's raw hunting facts. HuntingPriority is the
// work priority (0 is off), HuntingActive the game's WorkIsActive. HasHuntingWeapon and
// RangedBlockingShield mirror the game's own hunting-weapon and shield checks. The Route*Prey sets carry
// native's per-row route verdict; ReachableBenches the butcher benches the colonist can reach.
type HuntHunter struct {
	ID                                            string
	Cell                                          domain.Cell
	Downed, MentalState, Drafted                  bool
	HuntingPriority                               int
	HuntingActive, HuntingDisabled, CookingActive bool
	Weapon                                        *HuntWeapon
	HasHuntingWeapon, RangedBlockingShield        bool
	ReachableBenches                              map[string]bool
	// Route evidence per hunt row id: RouteSafePrey and RouteUnsafePrey are
	// evaluated answers, RouteSkippedPrey ran out of the native budget. A row
	// in none of the three was not evaluated.
	RouteSafePrey, RouteUnsafePrey, RouteSkippedPrey map[string]bool
}

// HuntBill is a butcher-flesh bill on a bench, raw. Product is the bill's
// product count, known for a target-count bill only.
type HuntBill struct {
	Suspended, Paused bool
	Repeat            HuntRepeat
	Count, Target     int
	Product           *int
	AllowedCorpses    map[string]bool
}

// HuntRepeat is a bill's repeat mode.
type HuntRepeat string

const (
	HuntRepeatForever HuntRepeat = "forever"
	HuntRepeatCount   HuntRepeat = "count"
	HuntRepeatTarget  HuntRepeat = "target"
)

// HuntBench is a bench carrying butcher-flesh bills.
type HuntBench struct {
	ID     string
	Usable bool
	Bills  []HuntBill
}

// HuntCensus is native's raw hunt facts: every free colonist and every butcher bench.
type HuntCensus struct {
	Hunters []HuntHunter
	Benches []HuntBench
}

// Hunt hold reasons, stable words; no_hunter carries each colonist's own reason.
const (
	HuntHoldFogged      = "fogged"
	HuntHoldNotSafePrey = "not_safe_prey"
	HuntHoldNoButcher   = "no_butcher_bill"
	HuntHoldNoColonist  = "no_colonist"
	HuntHoldNoHunter    = "no_hunter"
	huntHunterDowned    = "downed"
	huntHunterMental    = "mental_state"
	huntHunterInactive  = "hunting_inactive"
	huntHunterNoWeapon  = "no_hunting_weapon"
	huntHunterShield    = "ranged_blocking_shield"
	huntHunterTooFar    = "too_far"
	huntHunterNoRoute   = "no_safe_route"
	// route_skipped: native's route budget ran out before this pair;
	// route_unevaluated: native emitted no verdict for it.
	huntHunterRouteSkipped     = "route_skipped"
	huntHunterRouteUnevaluated = "route_unevaluated"
)

// HuntHold is a hunt row policy offers no hunt for: the first gate it fails,
// and for no_hunter each colonist's reason ("<id> <reason>").
type HuntHold struct {
	ID, Reason string
	Detail     []string `json:",omitempty"`
	// Source is the held prey, set by the census decoder, so the plan can
	// price the prerequisite its hold waits on (HuntPrerequisiteCandidates).
	Source AcquisitionSource `json:"-"`
}

// HuntPrey is a hunt row with the raw prey flags that are not part of the
// source: fogged and in a mental state.
type HuntPrey struct {
	Source         AcquisitionSource
	Fogged, Mental bool
}

// HuntVerdict is the gate's answer for one hunt row. A nil Hold offers the row,
// with WeaponRange the longest reach among the colonists who can hunt it with a
// ranged weapon.
type HuntVerdict struct {
	Hold        *HuntHold
	WeaponRange float64
}

// pest is the pest rule: a pest race that is not in a mental state.
func (p HuntPrey) pest() bool { return p.Source.Pest && !p.Mental }

// ButcherReady reports a usable bench with a running butcher bill that accepts the corpse
// and a Cooking worker (standing, active, able to reach the bench).
func (c HuntCensus) ButcherReady(corpse string) bool {
	for _, bench := range c.Benches {
		if !bench.Usable {
			continue
		}
		for _, bill := range bench.Bills {
			if !bill.running(corpse) {
				continue
			}
			for _, h := range c.Hunters {
				if !h.Downed && !h.MentalState && h.CookingActive && h.ReachableBenches[bench.ID] {
					return true
				}
			}
		}
	}
	return false
}

func (b HuntBill) running(corpse string) bool {
	if b.Suspended || b.Paused || !b.AllowedCorpses[corpse] {
		return false
	}
	switch b.Repeat {
	case HuntRepeatForever:
		return true
	case HuntRepeatCount:
		return b.Count > 0
	case HuntRepeatTarget:
		return b.Product != nil && *b.Product < b.Target
	}
	return false
}

// hunterReason is why a colonist cannot hunt the prey, "" when they can; order
// is the gate's: downed, mental state, Hunting off, weapon (a shield blocks a
// ranged one), reach, route.
func (c HuntCensus) hunterReason(h HuntHunter, prey HuntPrey) string {
	meleeable := prey.Source.MeleeOnly
	shooter := h.Weapon != nil && h.Weapon.Hunts()
	armed := shooter && !h.RangedBlockingShield || meleeable && (h.Weapon == nil || h.Weapon.Melee)
	dx, dz := int64(h.Cell.X-prey.Source.Cell.X), int64(h.Cell.Z-prey.Source.Cell.Z)
	switch {
	case h.Downed:
		return huntHunterDowned
	case h.MentalState:
		return huntHunterMental
	case !h.HuntingActive:
		return huntHunterInactive
	case !armed && shooter:
		return huntHunterShield
	case !armed:
		return huntHunterNoWeapon
	case !prey.pest() && dx*dx+dz*dz > huntReachSquared:
		return huntHunterTooFar
	case h.RouteSafePrey[prey.Source.ID]:
		return ""
	case h.RouteUnsafePrey[prey.Source.ID]:
		return huntHunterNoRoute
	case h.RouteSkippedPrey[prey.Source.ID]:
		return huntHunterRouteSkipped
	default:
		return huntHunterRouteUnevaluated
	}
}

// Gate decides whether the hunt row is offered: the first failing gate holds it.
// Gates in order: fogged, safe prey (a pest waives it), a running butcher bill and
// Cooking worker (a pest waives it), a colonist, then a colonist who can hunt it.
func (c HuntCensus) Gate(prey HuntPrey) HuntVerdict {
	hold := func(reason string, detail ...string) HuntVerdict {
		return HuntVerdict{Hold: &HuntHold{ID: prey.Source.ID, Reason: reason, Detail: detail}}
	}
	if prey.Fogged {
		return hold(HuntHoldFogged)
	}
	if !prey.pest() {
		if prey.Mental || !prey.Source.Food {
			return hold(HuntHoldNotSafePrey)
		}
		if !c.ButcherReady(prey.Source.Resource) {
			return hold(HuntHoldNoButcher)
		}
	}
	if len(c.Hunters) == 0 {
		return hold(HuntHoldNoColonist)
	}
	var detail []string
	reach, offered := 0.0, false
	for _, h := range c.Hunters {
		reason := c.hunterReason(h, prey)
		if reason != "" {
			detail = append(detail, h.ID+" "+reason)
			continue
		}
		offered = true
		if h.Weapon != nil && h.Weapon.Hunts() {
			for _, v := range h.Weapon.Verbs {
				if !v.Melee && v.AIWeapon {
					reach = max(reach, v.Range)
				}
			}
		}
	}
	if !offered {
		return hold(HuntHoldNoHunter, detail...)
	}
	return HuntVerdict{WeaponRange: reach}
}

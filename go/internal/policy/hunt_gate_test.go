package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func gateRifle() *HuntWeapon {
	return &HuntWeapon{DefName: "Gun_BoltActionRifle", Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 36, Projectile: HuntProjectileBullet, DamageDef: "Bullet"}}}
}

func gateDeer() HuntPrey {
	return HuntPrey{Source: AcquisitionSource{ID: "deer", Resource: "Corpse_Deer", Hunt: true, Food: true, Cell: domain.Cell{X: 10, Z: 10}}}
}

// gateCensus is a colony the deer is huntable in: a running butcher bill, a cook and a rifleman with a safe route.
func gateCensus() HuntCensus {
	return HuntCensus{
		Benches: []HuntBench{{ID: "bench", Usable: true, Bills: []HuntBill{{Repeat: HuntRepeatForever, AllowedCorpses: map[string]bool{"Corpse_Deer": true}}}}},
		Hunters: []HuntHunter{{ID: "ann", Cell: domain.Cell{X: 20, Z: 10}, HuntingActive: true, CookingActive: true, Weapon: gateRifle(),
			RouteSafePrey: map[string]bool{"deer": true}, ReachableBenches: map[string]bool{"bench": true}}},
	}
}

func TestHuntGateOffersARowWithReachOfTheQualifyingHunters(t *testing.T) {
	v := gateCensus().Gate(gateDeer())
	if v.Hold != nil || v.WeaponRange != 36 {
		t.Fatal(v)
	}
}

// A hunt row stands with no butcher bill: policy holds it with a stable reason.
func TestHuntGateHoldsARowWithNoButcherBill(t *testing.T) {
	c := gateCensus()
	c.Benches = nil
	if v := c.Gate(gateDeer()); v.Hold == nil || v.Hold.Reason != HuntHoldNoButcher || v.Hold.ID != "deer" {
		t.Fatal(v)
	}
}

func TestHuntGateButcherBillRules(t *testing.T) {
	product := func(n int) *int { return &n }
	for name, mutate := range map[string]func(*HuntCensus){
		"bench unusable": func(c *HuntCensus) { c.Benches[0].Usable = false },
		"bill suspended": func(c *HuntCensus) { c.Benches[0].Bills[0].Suspended = true },
		"bill paused":    func(c *HuntCensus) { c.Benches[0].Bills[0].Paused = true },
		"corpse refused": func(c *HuntCensus) { c.Benches[0].Bills[0].AllowedCorpses = map[string]bool{} },
		"count spent":    func(c *HuntCensus) { c.Benches[0].Bills[0].Repeat = HuntRepeatCount },
		"target met": func(c *HuntCensus) {
			b := &c.Benches[0].Bills[0]
			b.Repeat, b.Target, b.Product = HuntRepeatTarget, 5, product(5)
		},
		"cook inactive":     func(c *HuntCensus) { c.Hunters[0].CookingActive = false },
		"cook downed":       func(c *HuntCensus) { c.Hunters[0].Downed = true },
		"cook cannot reach": func(c *HuntCensus) { c.Hunters[0].ReachableBenches = nil },
	} {
		c := gateCensus()
		mutate(&c)
		if v := c.Gate(gateDeer()); v.Hold == nil || v.Hold.Reason != HuntHoldNoButcher {
			t.Fatal(name, v)
		}
	}
	c := gateCensus()
	b := &c.Benches[0].Bills[0]
	b.Repeat, b.Count = HuntRepeatCount, 2
	if v := c.Gate(gateDeer()); v.Hold != nil {
		t.Fatal(v)
	}
	b.Repeat, b.Target, b.Product = HuntRepeatTarget, 5, product(4)
	if v := c.Gate(gateDeer()); v.Hold != nil {
		t.Fatal(v)
	}
}

// The first colonist reason a hunter fails is the one reported, in the gate's order.
func TestHuntGateNoHunterNamesEachColonistsFirstReason(t *testing.T) {
	c := gateCensus()
	c.Hunters = []HuntHunter{
		{ID: "a", Downed: true},
		{ID: "b", MentalState: true},
		{ID: "c"},
		{ID: "d", HuntingActive: true},
		{ID: "e", HuntingActive: true, Weapon: gateRifle(), Cell: domain.Cell{X: 500, Z: 500}},
		{ID: "f", HuntingActive: true, Weapon: gateRifle(), Cell: domain.Cell{X: 12, Z: 10}, RouteUnsafePrey: map[string]bool{"deer": true}},
		{ID: "g", HuntingActive: true, Weapon: gateRifle(), Cell: domain.Cell{X: 12, Z: 10}, RouteSkippedPrey: map[string]bool{"deer": true}},
		{ID: "h", HuntingActive: true, Weapon: gateRifle(), Cell: domain.Cell{X: 12, Z: 10}},
	}
	// The cook is a colonist too; every colonist is listed.
	c.Hunters[3].CookingActive, c.Hunters[3].ReachableBenches = true, map[string]bool{"bench": true}
	v := c.Gate(gateDeer())
	want := []string{"a downed", "b mental_state", "c hunting_inactive", "d no_hunting_weapon", "e too_far", "f no_safe_route", "g route_skipped", "h route_unevaluated"}
	if v.Hold == nil || v.Hold.Reason != HuntHoldNoHunter || !reflect.DeepEqual(v.Hold.Detail, want) {
		t.Fatal(v.Hold)
	}
}

// Hunting priority 0 is the gate that fires first on a colony that never turned Hunting on.
func TestHuntGateHuntingInactiveBeforeWeapon(t *testing.T) {
	c := gateCensus()
	c.Hunters[0].HuntingActive, c.Hunters[0].HuntingPriority, c.Hunters[0].Weapon = false, 0, nil
	if v := c.Gate(gateDeer()); v.Hold == nil || v.Hold.Detail[0] != "ann hunting_inactive" {
		t.Fatal(v.Hold)
	}
}

func TestHuntGateGatesRunInOrder(t *testing.T) {
	prey := gateDeer()
	prey.Fogged, prey.Mental = true, true
	if v := (HuntCensus{}).Gate(prey); v.Hold.Reason != HuntHoldFogged {
		t.Fatal(v.Hold)
	}
	prey.Fogged = false
	if v := (HuntCensus{}).Gate(prey); v.Hold.Reason != HuntHoldNotSafePrey {
		t.Fatal(v.Hold)
	}
	prey.Mental = false
	if v := (HuntCensus{}).Gate(prey); v.Hold.Reason != HuntHoldNoButcher {
		t.Fatal(v.Hold)
	}
	// No colonist means no cook: only a pest reaches the colonist gate.
	pest := HuntPrey{Source: AcquisitionSource{ID: "beaver", Hunt: true, Pest: true}}
	if v := (HuntCensus{}).Gate(pest); v.Hold.Reason != HuntHoldNoColonist {
		t.Fatal(v.Hold)
	}
}

func TestHuntGateWeaponRules(t *testing.T) {
	arrow := &HuntWeapon{Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 25, Projectile: HuntProjectileArrow, DamageDef: "Arrow"}}}
	grenade := &HuntWeapon{Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 12, Projectile: HuntProjectileOther, ExplosionRadius: 2}}}
	incendiary := &HuntWeapon{Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 20, Projectile: HuntProjectileBullet, DamageDef: "Flame", DamageWorker: flameDamageWorker}}}
	mixed := &HuntWeapon{Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Projectile: HuntProjectileBullet, DamageDef: "Bullet"}, {AIWeapon: true, Projectile: HuntProjectileOther, ExplosionRadius: 1}}}
	harmless := &HuntWeapon{Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 20, Projectile: HuntProjectileBullet}}}
	noVerb := &HuntWeapon{Ranged: true}
	knife := &HuntWeapon{Melee: true}
	for name, tc := range map[string]struct {
		weapon *HuntWeapon
		prey   func(*HuntPrey)
		offer  bool
	}{
		"arrow":                        {arrow, nil, true},
		"non-damaging projectile":      {harmless, nil, false},
		"grenade":                      {grenade, nil, false},
		"incendiary":                   {incendiary, nil, false},
		"mixed verbs":                  {mixed, nil, false},
		"no attack verb":               {noVerb, nil, false},
		"melee against deer":           {knife, nil, false},
		"melee against meleeable":      {knife, func(p *HuntPrey) { p.Source.MeleeOnly = true }, true},
		"bare hands against meleeable": {nil, func(p *HuntPrey) { p.Source.MeleeOnly = true }, true},
		"bare hands against deer":      {nil, nil, false},
	} {
		c := gateCensus()
		c.Hunters[0].Weapon = tc.weapon
		prey := gateDeer()
		if tc.prey != nil {
			tc.prey(&prey)
		}
		if v := c.Gate(prey); (v.Hold == nil) != tc.offer {
			t.Fatal(name, v.Hold)
		}
	}
}

// A shield that blocks ranged weapons holds the shooter, with its own reason; a bow hunter
// without one qualifies, and the offered reach is the bow's.
func TestHuntGateBowHunterQualifiesAndShieldDoesNot(t *testing.T) {
	c := gateCensus()
	c.Hunters[0].Weapon = &HuntWeapon{DefName: "Bow_Short", Ranged: true, Verbs: []HuntVerb{{AIWeapon: true, Range: 25.9, Projectile: HuntProjectileArrow, DamageDef: "Arrow"}}}
	if v := c.Gate(gateDeer()); v.Hold != nil || v.WeaponRange != 25.9 {
		t.Fatal(v)
	}
	c.Hunters[0].RangedBlockingShield = true
	v := c.Gate(gateDeer())
	if v.Hold == nil || v.Hold.Reason != HuntHoldNoHunter || !reflect.DeepEqual(v.Hold.Detail, []string{"ann ranged_blocking_shield"}) {
		t.Fatal(v.Hold)
	}
}

// A pest skips the safe-prey and butcher gates and is hunted at any distance; a pest in a mental state does not.
func TestHuntGatePestWaivesFoodRules(t *testing.T) {
	c := gateCensus()
	c.Benches = nil
	c.Hunters[0].Cell = domain.Cell{X: 900, Z: 900}
	c.Hunters[0].RouteSafePrey = map[string]bool{"beaver": true}
	pest := HuntPrey{Source: AcquisitionSource{ID: "beaver", Resource: "Corpse_Alphabeaver", Hunt: true, Pest: true}}
	if v := c.Gate(pest); v.Hold != nil {
		t.Fatal(v.Hold)
	}
	pest.Mental = true
	if v := c.Gate(pest); v.Hold == nil || v.Hold.Reason != HuntHoldNotSafePrey {
		t.Fatal(v.Hold)
	}
}

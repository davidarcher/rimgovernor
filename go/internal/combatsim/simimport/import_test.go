package simimport

import (
	"errors"
	"fmt"
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// The defs below are the recorded game catalog's. A test that names one is
// asserting the importer reads it, not that the package holds it.
const (
	testRifle = "Gun_AssaultRifle"
	testKind  = "Mercenary_Gunner"
)

func cell(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }

// duelSpec is two riflemen a side on a 40x14 map with a sandbag line.
func duelSpec() Spec {
	s := Spec{
		Name: "duel", Width: 40, Height: 14, Terrain: "Soil", Weather: "Clear",
		FriendlyFireChanceFactor: domain.Known(0.4), StepTicks: 6, MaxTicks: 6000,
		DeathOnDowned: domain.Known(DeathOnDowned{EnemyDeathOnDownedChanceFactor: 1}),
	}
	for x := int32(5); x <= 9; x++ {
		s.Things = append(s.Things, Thing{Def: "Sandbags", Cell: cell(x, 6)})
	}
	for i, z := range []int32{4, 8} {
		for _, side := range []string{combatmetrics.Colonist, combatmetrics.Hostile} {
			x, prefix := int32(7), "c"
			if side == combatmetrics.Hostile {
				x, prefix = 30, "h"
			}
			s.Pawns = append(s.Pawns, Pawn{
				ID: domain.PawnID(fmt.Sprintf("%s%d", prefix, i)), Side: side, Kind: testKind, Cell: cell(x, z),
				Weapon: testRifle, Worn: domain.Known([]Wear{}), Skills: map[string]int32{"Shooting": 8, "Melee": 8},
			})
		}
	}
	return s
}

func traced(t *testing.T, r Result, subject, field string) Trace {
	t.Helper()
	for _, tr := range r.Trace {
		if tr.Subject == subject && tr.Field == field {
			if tr.Source == "" {
				t.Errorf("%s %s traced to nothing", subject, field)
			}
			return tr
		}
	}
	t.Fatalf("%s %s has no trace", subject, field)
	return Trace{}
}

func TestImportDerivesEveryValueFromTheCatalog(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	res, err := Import(cat, duelSpec())
	if err != nil {
		t.Fatal(err)
	}
	sc := res.Fight.Scenario
	if len(sc.Things) != 1 || sc.Things[0].Name != "Sandbags" || sc.Things[0].Passability != combatsim.PassThroughOnly ||
		sc.Things[0].Fillage != combatsim.FillPartial || sc.Things[0].FillPercent != float64(cat.ThingDef("Sandbags").GetFillPercent()) ||
		sc.Things[0].PathCost != int(cat.ThingDef("Sandbags").GetPathCost()) {
		t.Errorf("sandbag def = %+v", sc.Things)
	}
	if len(sc.Terrain) != 1 || sc.Terrain[0].PathCost != int(cat.TerrainDef("Soil").GetPathCost()) || !sc.Terrain[0].Passable {
		t.Errorf("terrain = %+v", sc.Terrain)
	}
	weather, _ := sc.WeatherMoveSpeed.Value()
	if weather != 1 {
		t.Errorf("clear weather move speed = %v", weather)
	}
	for _, p := range sc.Pawns {
		cs := p.Combat
		id := string(p.ID)
		if got, _ := cs.BodySize.Value(); got != 1 {
			t.Errorf("%s body size = %v", id, got)
		}
		// 150 * HealthScale 1, and pain shock 0.8 over a bullet's 0.0125 pain per severity.
		if kill, _ := cs.DamageToKill.Value(); kill != 150 {
			t.Errorf("%s DamageToKill = %v", id, kill)
		}
		if down, _ := cs.DamageToDown.Value(); math.Abs(down-64) > 1e-3 {
			t.Errorf("%s DamageToDown = %v", id, down)
		}
		if !cs.CanBenefitFromCover {
			t.Errorf("%s cannot benefit from cover", id)
		}
		if armor, ok := cs.Armor.Value(); !ok || len(armor) != 1 || armor[0] != 0 {
			t.Errorf("%s armor = %v %v: want the natural layer only", id, armor, ok)
		}
		w := cs.Weapon
		if w == nil || w.Name != testRifle || !(w.Range > 0) || w.DamageAmount < 1 || !(w.ProjectileSpeed > 0) || w.BurstShotCount < 1 {
			t.Errorf("%s weapon = %+v", id, w)
		}
		if speed, _ := p.MoveSpeed.Value(); math.Abs(speed-4.6) > 1e-6 {
			t.Errorf("%s move speed = %v", id, speed)
		}
		for _, field := range []string{
			"MoveSpeed", "BodySize", "CanBenefitFromCover", "DamageToDown", "DamageToKill", "Armor.natural", "Facts.Armor", "Facts.MeleePower",
			"Weapon.Range", "Weapon.MinRange", "Weapon.WarmupSeconds", "Weapon.CooldownSeconds", "Weapon.BurstShotCount", "Weapon.TicksBetweenBurstShots",
			"Weapon.AccuracyTouch", "Weapon.AccuracyShort", "Weapon.AccuracyMedium", "Weapon.AccuracyLong", "Weapon.ProjectileSpeed",
			"Weapon.DamageAmount", "Weapon.ArmorPenetration", "Weapon.ForcedMissRadius",
			"Shooter." + statShootAccuracy, "Shooter." + statAimingDelay, "Shooter." + statRangedCooldown,
			"Shooter." + statShootingTouch, "Shooter." + statShootingShort, "Shooter." + statShootingMedium, "Shooter." + statShootingLong,
		} {
			traced(t, res, id, field)
		}
	}
	for _, field := range []string{"Terrain", "Things.Sandbags", "WeatherMoveSpeed", "WeatherAccuracy"} {
		traced(t, res, "map", field)
	}
	if tr := traced(t, res, "c0", "DamageToDown"); !strings.Contains(tr.Source, "painPerSeverity") {
		t.Errorf("DamageToDown source = %q", tr.Source)
	}
	if len(res.Unmodeled) != 0 {
		t.Errorf("unmodeled = %v", res.Unmodeled)
	}
}

func TestImportedFightRunsToResolution(t *testing.T) {
	res, err := Import(recordedcatalog.Catalog(t), duelSpec())
	if err != nil {
		t.Fatal(err)
	}
	f := res.Fight
	f.Enemy = nearestEnemy(f.Scenario)
	got, err := combatsim.RunFight(f, 7, policy.DefaultCombatTuning())
	if err != nil {
		t.Fatal(err)
	}
	if !got.Resolved || got.Metrics.FirstContactTick < 0 {
		t.Fatalf("fight did not resolve: %+v", got)
	}
}

// nearestEnemy orders every standing hostile to shoot the nearest standing
// colonist; the policy drives the colonists.
func nearestEnemy(sc combatsim.Scenario) func(combatsim.State) []combatsim.Order {
	side := map[domain.PawnID]string{}
	for _, p := range sc.Pawns {
		side[p.ID] = p.Side
	}
	return func(st combatsim.State) []combatsim.Order {
		var out []combatsim.Order
		for _, p := range st.Pawns {
			if side[p.ID] != combatmetrics.Hostile || p.Downed || p.Dead {
				continue
			}
			best, bd := domain.PawnID(""), int32(math.MaxInt32)
			for _, q := range st.Pawns {
				if side[q.ID] != combatmetrics.Colonist || q.Downed || q.Dead {
					continue
				}
				dx, dz := q.Pos.X-p.Pos.X, q.Pos.Z-p.Pos.Z
				if dd := dx*dx + dz*dz; dd < bd {
					best, bd = q.ID, dd
				}
			}
			if best != "" {
				out = append(out, combatsim.Order{Pawn: p.ID, Kind: combatsim.OrderAttack, Target: best})
			}
		}
		return out
	}
}

func TestImportFailsClosed(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	cases := []struct {
		name string
		edit func(*Spec)
	}{
		{"unknown terrain", func(s *Spec) { s.Terrain = "NoSuchTerrain" }},
		{"unknown weather", func(s *Spec) { s.Weather = "NoSuchWeather" }},
		{"unknown thing", func(s *Spec) { s.Things[0].Def = "NoSuchThing" }},
		{"turret", func(s *Spec) { s.Things[0].Def = "Turret_MiniTurret" }},
		{"two things on a cell", func(s *Spec) { s.Things = append(s.Things, Thing{Def: "Wall", Cell: s.Things[0].Cell}) }},
		{"thing off the map", func(s *Spec) { s.Things[0].Cell = cell(99, 99) }},
		{"unknown friendly fire factor", func(s *Spec) { s.FriendlyFireChanceFactor = domain.Fact[float64]{} }},
		{"unknown death on downed inputs", func(s *Spec) { s.DeathOnDowned = domain.Fact[DeathOnDowned]{} }},
		{"negative death on downed factor", func(s *Spec) {
			s.DeathOnDowned = domain.Known(DeathOnDowned{EnemyDeathOnDownedChanceFactor: -1})
		}},
		{"unknown kind", func(s *Spec) { s.Pawns[0].Kind = "NoSuchKind" }},
		{"animal kind", func(s *Spec) { s.Pawns[0].Kind = "Muffalo" }},
		{"unknown weapon", func(s *Spec) { s.Pawns[0].Weapon = "NoSuchGun" }},
		{"unknown worn apparel", func(s *Spec) { s.Pawns[0].Worn = domain.Fact[[]Wear]{} }},
		{"worn def missing", func(s *Spec) { s.Pawns[0].Worn = domain.Known([]Wear{{Def: "NoSuchCoat"}}) }},
		{"unknown skills", func(s *Spec) { s.Pawns[0].Skills = nil }},
		{"skill the stats read missing", func(s *Spec) { s.Pawns[0].Skills = map[string]int32{"Melee": 8} }},
		{"bad side", func(s *Spec) { s.Pawns[0].Side = "neutral" }},
		{"second damage def", func(s *Spec) { s.Pawns[1].Weapon = "Bow_Short" }},
		{"explosive weapon", func(s *Spec) { s.Pawns[1].Weapon = "Weapon_GrenadeFrag" }},
		{"no ranged weapon at all", func(s *Spec) {
			for i := range s.Pawns {
				s.Pawns[i].Weapon = ""
			}
		}},
		{"zero step", func(s *Spec) { s.StepTicks = 0 }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			s := duelSpec()
			tc.edit(&s)
			_, err := Import(cat, s)
			if !errors.Is(err, combatsim.ErrUnsupported) {
				t.Fatalf("err = %v, want ErrUnsupported", err)
			}
		})
	}
}

// TestImportDerivesDeathOnDownedFromTheCatalogCurve checks the chance
// Pawn_HealthTracker.CheckForStateChange rolls for a downed non-colony
// humanlike against the points of the game's own curves (HealthTuning: the
// wavering-prisoners curve is 0.85 at intent 0 and 0.62 at intent 1; the other
// is 0.8 and 0.5657), scaled by the difficulty factor. A colonist is never
// rolled.
func TestImportDerivesDeathOnDownedFromTheCatalogCurve(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	for _, tc := range []struct {
		name string
		in   DeathOnDowned
		want float64
	}{
		{"intent 0, wavering", DeathOnDowned{PopulationIntent: 0, EnemyDeathOnDownedChanceFactor: 1}, 0.85},
		{"intent 1, wavering", DeathOnDowned{PopulationIntent: 1, EnemyDeathOnDownedChanceFactor: 1}, 0.62},
		{"intent 0, unwavering", DeathOnDowned{PopulationIntent: 0, EnemyDeathOnDownedChanceFactor: 1, UnwaveringPrisoners: true}, 0.8},
		{"intent 1, unwavering", DeathOnDowned{PopulationIntent: 1, EnemyDeathOnDownedChanceFactor: 1, UnwaveringPrisoners: true}, 0.5657},
		{"between points", DeathOnDowned{PopulationIntent: 0.5, EnemyDeathOnDownedChanceFactor: 1}, 0.735},
		{"easy difficulty", DeathOnDowned{PopulationIntent: 0, EnemyDeathOnDownedChanceFactor: 0.5}, 0.425},
		{"factor past one is capped", DeathOnDowned{PopulationIntent: 0, EnemyDeathOnDownedChanceFactor: 2}, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			s := duelSpec()
			s.DeathOnDowned = domain.Known(tc.in)
			res, err := Import(cat, s)
			if err != nil {
				t.Fatal(err)
			}
			for _, p := range res.Fight.Scenario.Pawns {
				want := tc.want
				if p.Side == combatmetrics.Colonist {
					want = 0
				}
				if got := p.Combat.DeathOnDownedChance; math.Abs(got-want) > 1e-6 {
					t.Errorf("%s (%s) death on downed = %v, want %v", p.ID, p.Side, got, want)
				}
			}
			traced(t, res, "h0", "DeathOnDownedChance")
		})
	}
}

// TestImportDerivesHitLayersFromTheBody checks the human body's hit spread:
// the chances sum to one, most bullets land on an outside part (no extra
// injury), and a hit on an inside part adds one injury per outside layer.
func TestImportDerivesHitLayersFromTheBody(t *testing.T) {
	res, err := Import(recordedcatalog.Catalog(t), duelSpec())
	if err != nil {
		t.Fatal(err)
	}
	layers := res.Fight.Scenario.Pawns[0].Combat.HitLayers
	sum, mean, inside := 0.0, 0.0, 0.0
	for _, l := range layers {
		sum += l.Chance
		mean += l.Chance * float64(1+l.Copies)
		if l.Copies > 0 {
			inside += l.Chance
		}
	}
	if len(layers) < 2 || layers[0].Copies != 0 || math.Abs(sum-1) > 1e-9 {
		t.Fatalf("layers = %+v", layers)
	}
	// The real lab ledger shows a doubled hit (22 for a rifle's 11) on about
	// 40% of hits: inside parts' share of the body's hit weight.
	if inside < 0.3 || inside > 0.5 || mean < 1.3 || mean > 1.6 {
		t.Errorf("inside-part share %.3f, mean injuries per hit %.3f: want about 0.4 and 1.4", inside, mean)
	}
	traced(t, res, "h0", "HitLayers")
}

func TestImportLeavesMeleeUnmodeledNotGuessed(t *testing.T) {
	s := duelSpec()
	s.Pawns[1].Weapon, s.Pawns[1].WeaponStuff = "MeleeWeapon_Club", "WoodLog"
	res, err := Import(recordedcatalog.Catalog(t), s)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Unmodeled) != 1 || !strings.Contains(res.Unmodeled[0], "MeleeWeapon_Club") {
		t.Errorf("unmodeled = %v", res.Unmodeled)
	}
	got := res.Fight.Scenario.Pawns[1]
	if got.Combat.Weapon != nil {
		t.Errorf("melee pawn imported armed: %+v", got.Combat.Weapon)
	}
	if power, ok := res.Fight.Facts[got.ID].MeleePower.Value(); !ok || !(power > 0) {
		t.Errorf("melee power = %v %v", power, ok)
	}
}

func TestImportSuppliesTheRaidLordFromTheCatalog(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	spec := duelSpec()
	if res, err := Import(cat, spec); err != nil || res.Fight.EnemyAI != nil {
		t.Fatalf("a spec without a raid: err %v, EnemyAI %+v, want none", err, res.Fight.EnemyAI)
	}
	spec.Raid = &Raid{CanTimeoutOrFlee: true, Exit: domain.Known(cell(39, 7))}
	res, err := Import(cat, spec)
	if err != nil {
		t.Fatal(err)
	}
	cfg := res.Fight.EnemyAI
	g, err := cat.GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	want := g.GetLordJob_AssaultColony().GetAssaultTimeBeforeGiveUp()
	if cfg == nil || cfg.GiveUpMinTicks != int(want.GetMin()) || cfg.GiveUpMaxTicks != int(want.GetMax()) || cfg.GiveUpMinTicks <= 0 {
		t.Fatalf("give-up range = %+v, want the catalog's %v-%v", cfg, want.GetMin(), want.GetMax())
	}
	// Two colonists at 100 each plus the sandbags' hit points.
	if got, ok := cfg.ColonyHealthTotal.Value(); !ok || got <= 200 {
		t.Errorf("colony health total = %v %v, want above 200 (sandbag hit points)", got, ok)
	}
	traced(t, res, "raid", "GiveUpTicks")
	traced(t, res, "raid", "ColonyHealthTotal")
	if _, err := combatsim.RunFight(res.Fight, 3, policy.DefaultCombatTuning()); err != nil {
		t.Fatalf("the imported raid does not run: %v", err)
	}

	spec.Raid = &Raid{CanTimeoutOrFlee: true}
	res, err = Import(cat, spec)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := combatsim.RunFight(res.Fight, 3, policy.DefaultCombatTuning()); !errors.Is(err, combatsim.ErrUnsupported) {
		t.Errorf("a fleeing raid with no exit: err = %v, want ErrUnsupported", err)
	}
}

package combatlab

import (
	"errors"
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim/simimport"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// statedEnv is LabSimEnv with the two facts it leaves unknown stated by the
// test: a friendly fire factor, and colonists in no armor-rated apparel. Both
// are test inputs, not lab readings (the lab does not strip colonists).
func statedEnv() SimEnv {
	env := LabSimEnv()
	env.FriendlyFireChanceFactor = domain.Known(0.4)
	env.DeathOnDowned = domain.Known(simimport.DeathOnDowned{EnemyDeathOnDownedChanceFactor: 1})
	env.ColonistWorn = domain.Known([]simimport.Wear{})
	return env
}

// hostileHook orders each standing hostile to shoot the nearest standing
// colonist, or walk toward it when it carries no ranged weapon, until the
// enemy AI (#2842) exists.
func hostileHook(sc combatsim.Scenario) func(combatsim.State) []combatsim.Order {
	side := map[domain.PawnID]string{}
	reach := map[domain.PawnID]float64{} // weapon range, 0 when unarmed
	for _, p := range sc.Pawns {
		side[p.ID] = p.Side
		if p.Combat != nil && p.Combat.Weapon != nil {
			reach[p.ID] = p.Combat.Weapon.Range
		}
	}
	return func(st combatsim.State) []combatsim.Order {
		var out []combatsim.Order
		for _, p := range st.Pawns {
			if side[p.ID] != combatmetrics.Hostile || p.Downed || p.Dead {
				continue
			}
			var near combatsim.PawnState
			best := math.MaxFloat64
			for _, q := range st.Pawns {
				if side[q.ID] != combatmetrics.Colonist || q.Downed || q.Dead {
					continue
				}
				dx, dz := float64(q.Pos.X-p.Pos.X), float64(q.Pos.Z-p.Pos.Z)
				if d := dx*dx + dz*dz; d < best {
					best, near = d, q
				}
			}
			switch {
			case best == math.MaxFloat64:
			case reach[p.ID] > 0 && best <= (reach[p.ID]-2)*(reach[p.ID]-2):
				out = append(out, combatsim.Order{Pawn: p.ID, Kind: combatsim.OrderAttack, Target: near.ID})
			case !p.Moving: // out of reach or unarmed: close in
				out = append(out, combatsim.Order{Pawn: p.ID, Kind: combatsim.OrderMove, To: near.Pos})
			}
		}
		return out
	}
}

func TestLabFixturesImportAndRunInTheSim(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	cx, cz := labCenter()
	for _, name := range []string{"lab-ranged", "lab-choke"} {
		t.Run(name, func(t *testing.T) {
			f, err := Build(name, cx, cz)
			if err != nil {
				t.Fatal(err)
			}
			spec, err := SimSpec(f, statedEnv())
			if err != nil {
				t.Fatal(err)
			}
			res, err := simimport.Import(cat, spec)
			if err != nil {
				t.Fatal(err)
			}
			if name == "lab-choke" && len(res.Unmodeled) != 3+6 {
				t.Errorf("choke melee pawns left unmodeled = %v, want 3 brawlers and 6 slashers", res.Unmodeled)
			}
			fight := res.Fight
			if fight.EnemyAI == nil || !fight.EnemyAI.CanTimeoutOrFlee || fight.EnemyAI.GiveUpMinTicks <= 0 || fight.EnemyAI.GiveUpMaxTicks < fight.EnemyAI.GiveUpMinTicks {
				t.Fatalf("the import left the raid lord unset: %+v", fight.EnemyAI)
			}
			if name == "lab-choke" {
				// The enemy AI drives ranged raiders only (E8), so the melee fixture
				// keeps the scripted opponent that walks them in.
				fight.EnemyAI = nil
				fight.Enemy = hostileHook(fight.Scenario)
			}
			got, err := combatsim.RunFight(fight, 1, policy.DefaultCombatTuning())
			if err != nil {
				t.Fatal(err)
			}
			// Who wins lab-ranged is the calibration gate's business (it does not
			// match the real baseline); this asserts the closed loop fights.
			if got.Decisions == 0 || got.Metrics.FirstContactTick < 0 || got.Metrics.ColonistDamage+float64(got.Metrics.EnemyDowns) == 0 {
				t.Fatalf("the policy never fought: %+v", got.Metrics)
			}
			switch name {
			case "lab-ranged":
				if !got.Resolved {
					t.Fatalf("ranged fight did not resolve: %+v", got.Metrics)
				}
			case "lab-choke":
				// The fixture is a melee fight: the brawlers and slashers are imported
				// unarmed (Result.Unmodeled), so the armed colonists, who shoot on their
				// own once idle (A21), down every raider and win without a scratch.
				if !got.Resolved || got.Metrics.Winner != "colonist" || got.Metrics.ColonistDowns != 0 {
					t.Fatalf("choke fight: %+v", got.Metrics)
				}
			}
		})
	}
}

func TestLabSimImportFailsClosedOnWhatTheFixtureCannotState(t *testing.T) {
	cat := recordedcatalog.Catalog(t)
	cx, cz := labCenter()
	f, err := Build("lab-ranged", cx, cz)
	if err != nil {
		t.Fatal(err)
	}
	for name, env := range map[string]SimEnv{
		"worn apparel unknown":       func() SimEnv { e := statedEnv(); e.ColonistWorn = domain.Fact[[]simimport.Wear]{}; return e }(),
		"friendly fire not stated":   func() SimEnv { e := statedEnv(); e.FriendlyFireChanceFactor = domain.Fact[float64]{}; return e }(),
		"death on downed not stated": func() SimEnv { e := statedEnv(); e.DeathOnDowned = domain.Fact[simimport.DeathOnDowned]{}; return e }(),
	} {
		spec, err := SimSpec(f, env)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := simimport.Import(cat, spec); !errors.Is(err, combatsim.ErrUnsupported) {
			t.Errorf("%s: err = %v, want ErrUnsupported", name, err)
		}
	}
}

func TestSimSpecRefusesWhatTheSimDoesNotModel(t *testing.T) {
	cx, cz := labCenter()
	for _, name := range []string{"lab-pods", "lab-siege", "lab-breach", "lab-infestation", "lab-ranged-shield", "lab-manhunter", "lab-mech"} {
		f, err := Build(name, cx, cz)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := SimSpec(f, statedEnv()); err == nil || !strings.Contains(err.Error(), "does not model") {
			t.Errorf("%s: err = %v, want a refusal", name, err)
		}
	}
}

package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDefenseCapacity(t *testing.T) {
	colonist := func(id domain.PawnID, downed bool, melee, ranged float64) SquadDefenderFacts {
		return SquadDefenderFacts{ID: id, Dead: domain.Known(false), Downed: domain.Known(downed), ViolenceCapable: domain.Known(true), MeleePower: domain.Known(melee), RangedDPS: domain.Known(ranged)}
	}
	turret := func(id string, powered domain.Fact[bool], dps float64) DefenseTurretFacts {
		return DefenseTurretFacts{ID: id, Definition: "Turret_MiniTurret", Powered: powered, DPS: domain.Known(dps)}
	}
	armed := []SquadDefenderFacts{colonist("a", false, 2, 10), colonist("b", false, 3, 8), colonist("c", false, 1, 6)}
	turrets := []DefenseTurretFacts{turret("t1", domain.Known(true), 4), turret("t2", domain.Known(true), 5)}
	cases := []struct {
		name      string
		defenders []SquadDefenderFacts
		turrets   []DefenseTurretFacts
		want      float64
		known     bool
	}{
		{"three armed colonists and two turrets", armed, turrets, (2 + 10 + 3 + 8 + 1 + 6 + 4 + 5) * DefensePointsPerDPS, true},
		{"downed colonist excluded", append(append([]SquadDefenderFacts{}, armed...), colonist("d", true, 50, 50)), turrets, (2 + 10 + 3 + 8 + 1 + 6 + 4 + 5) * DefensePointsPerDPS, true},
		{"unpowered turret excluded", armed, []DefenseTurretFacts{turret("t1", domain.Known(true), 4), turret("t2", domain.Known(false), 5)}, (2 + 10 + 3 + 8 + 1 + 6 + 4) * DefensePointsPerDPS, true},
		{"unknown turret power", armed, []DefenseTurretFacts{turret("t1", domain.Unknown[bool](), 4)}, 0, false},
		{"unknown turret dps", armed, []DefenseTurretFacts{{ID: "t1", Powered: domain.Known(true)}}, 0, false},
		{"unknown ranged dps", []SquadDefenderFacts{{ID: "a", Dead: domain.Known(false), Downed: domain.Known(false), ViolenceCapable: domain.Known(true), MeleePower: domain.Known(2.0)}}, nil, 0, false},
		{"pacifist excluded", []SquadDefenderFacts{{ID: "a", Dead: domain.Known(false), Downed: domain.Known(false), ViolenceCapable: domain.Known(false)}}, nil, 0, true},
	}
	for _, tc := range cases {
		got, known := DefenseCapacity(domain.Known(tc.defenders), domain.Known(tc.turrets)).Value()
		if known != tc.known || got != tc.want {
			t.Errorf("%s: got %v (known %v), want %v (known %v)", tc.name, got, known, tc.want, tc.known)
		}
	}
	if _, known := DefenseCapacity(domain.Known(armed), domain.Unknown[[]DefenseTurretFacts]()).Value(); known {
		t.Error("unknown turret census gave a known capacity")
	}
}

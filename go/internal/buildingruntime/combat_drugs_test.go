package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	m "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	"google.golang.org/protobuf/proto"
)

func TestCombatPawnCarriedDrugs(t *testing.T) {
	for _, tc := range []struct {
		name      string
		inventory *m.CarriedDrugs
		known     bool
		want      []string
	}{
		{"unknown", nil, false, nil},
		{"empty", &m.CarriedDrugs{}, true, nil},
		{"carried", &m.CarriedDrugs{Defs: []string{"GoJuice", "Yayo"}}, true, []string{"GoJuice", "Yayo"}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			combat := bridge.Combat{Pawns: []*m.CombatPawn{{Id: proto.String("p0"), CarriedDrugs: tc.inventory}}}
			states := combatPawnStates(combat, nil, map[string]policy.WeaponDef{})
			got, known := states[0].CarriedDrugs.Value()
			if known != tc.known || !reflect.DeepEqual(got, tc.want) {
				t.Fatalf("carried = %v, known = %v; want %v, %v", got, known, tc.want, tc.known)
			}
		})
	}
}

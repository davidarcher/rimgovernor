package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestCaravanReturnAndContinuationKeepExactCrewAndPack(t *testing.T) {
	for name, cargo := range map[string][]domain.CargoItem{"return loot": {{Definition: "Steel", Count: 17}}, "existing caravan route": nil} {
		t.Run(name, func(t *testing.T) {
			departure, err := domain.NewCaravanDeparture([]domain.PawnID{"survivor", "wounded"}, cargo, 42)
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewCaravanDepartureAction("return", departure)
			if err != nil {
				t.Fatal(err)
			}
			wire, err := caravanDepartureAction(action)
			if err != nil {
				t.Fatal(err)
			}
			intent := wire.GetFormCaravan()
			if intent.GetDestinationTile() != 42 || !reflect.DeepEqual(intent.PawnIds, []string{"survivor", "wounded"}) {
				t.Fatal(intent)
			}
			if len(intent.Cargo) != len(cargo) {
				t.Fatal("routing acquired cargo", intent)
			}
			if len(cargo) > 0 && (intent.Cargo[0].GetDefName() != "Steel" || intent.Cargo[0].GetCount() != 17) {
				t.Fatal("reform lost selected loot", intent)
			}
		})
	}
}

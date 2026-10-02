package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// wasteAction is the GiveJobIntent HaulWaste of one pawn and one exposed
// waste item. Native checks the pawn, the item's protection and a separated
// destination live; the game's Hauling WorkGiver builds the job, and a pawn
// already hauling the item applies again (NativeWasteOperations.cs).
func wasteAction(action domain.Action) (*o.Action, error) {
	w, ok := action.Waste()
	if !ok {
		return nil, contract("not a waste action")
	}
	return giveJob(w.Pawn(), JobHaulWaste, w.Target())
}

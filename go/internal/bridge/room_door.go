package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// RoomDoorFacts keeps absent native fields unknown for both routine and combat.
func RoomDoorFacts(door *o.RoomDoor) policy.RoomDoor {
	fact := func(v *bool) domain.Fact[bool] {
		if v == nil {
			return domain.Unknown[bool]()
		}
		return domain.Known(*v)
	}
	return policy.RoomDoor{ID: door.GetId(), Cell: domain.Cell{X: door.GetCell().GetX(), Z: door.GetCell().GetZ()}, Outside: domain.Cell{X: door.GetOutside().GetX(), Z: door.GetOutside().GetZ()}, Outdoors: fact(door.Outdoors), PlayerOwned: fact(door.PlayerOwned), Open: fact(door.Open), HoldOpen: fact(door.HoldOpen), BlockedOpen: fact(door.BlockedOpen), Forbidden: fact(door.Forbidden)}
}

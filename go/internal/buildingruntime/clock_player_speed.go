package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// followPlayerSpeed is start at the speed the player last chose:
// Ultrafast when native reports none, and player acceleration exactly when
// that speed is Ultrafast.
func followPlayerSpeed(start bridge.ClockStart, status *k.Status) bridge.ClockStart {
	start.Speed = k.Speed_SPEED_ULTRAFAST
	if status.PlayerSpeed != nil {
		start.Speed = status.GetPlayerSpeed()
	}
	start.PlayerAccelerated = start.Speed == k.Speed_SPEED_ULTRAFAST
	return start
}

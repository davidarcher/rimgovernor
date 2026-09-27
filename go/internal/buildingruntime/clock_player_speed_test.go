package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// A window starts at the player's speed; none chosen is paced Ultrafast,
// and only a paced window keeps the frame budget (#875).
func TestFollowPlayerSpeed(t *testing.T) {
	base := bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL}
	if got := followPlayerSpeed(base, &k.Status{}); got.Speed != k.Speed_SPEED_ULTRAFAST || !got.PlayerAccelerated {
		t.Fatalf("unset: %+v", got)
	}
	if got := followPlayerSpeed(base, &k.Status{PlayerSpeed: k.Speed_SPEED_FAST.Enum()}); got.Speed != k.Speed_SPEED_FAST || got.PlayerAccelerated {
		t.Fatalf("fast: %+v", got)
	}
	if got := followPlayerSpeed(base, &k.Status{PlayerSpeed: k.Speed_SPEED_ULTRAFAST.Enum()}); got.Speed != k.Speed_SPEED_ULTRAFAST || !got.PlayerAccelerated {
		t.Fatalf("ultrafast: %+v", got)
	}
}

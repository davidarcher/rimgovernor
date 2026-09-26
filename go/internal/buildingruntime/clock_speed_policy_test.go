package buildingruntime

import (
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

func TestSpeedPolicyRaisesWhileCalmAndDropsOnLatency(t *testing.T) {
	config := DefaultSpeedPolicy(100 * time.Millisecond)
	config.AllowTestAcceleration = true
	p := speedPolicy{config: config}
	floor := speedLevel(k.Speed_SPEED_FAST, false)
	calm := speedLatency{Readmit: 50 * time.Millisecond, Observe: 20 * time.Millisecond}
	var level int
	for range 3 {
		level, p = p.next(floor, calm)
	}
	if level != int(k.Speed_SPEED_SUPERFAST) {
		t.Fatalf("after 3 calm windows level = %d", level)
	}
	for range 9 {
		level, p = p.next(floor, calm)
	}
	if speed, accel := speedOfLevel(level); speed != k.Speed_SPEED_ULTRAFAST || !accel {
		t.Fatalf("top = %s accel=%v", speed, accel)
	}
	level, p = p.next(floor, speedLatency{Readmit: time.Second})
	if speed, accel := speedOfLevel(level); speed != k.Speed_SPEED_ULTRAFAST || accel {
		t.Fatalf("late readmit did not drop a step: %s accel=%v", speed, accel)
	}
	level, _ = p.next(floor, speedLatency{Observe: time.Second})
	if level != int(k.Speed_SPEED_SUPERFAST) {
		t.Fatalf("slow observe did not drop a step: %d", level)
	}
	floorOnly := speedPolicy{config: config}
	if level, _ = floorOnly.next(floor, speedLatency{Readmit: time.Second}); level != floor {
		t.Fatalf("dropped below the floor: %d", level)
	}
}

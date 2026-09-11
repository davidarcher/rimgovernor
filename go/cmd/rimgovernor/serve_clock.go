package main

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

type serviceClockReads interface {
	buildingruntime.ClockWindowNative
	buildingruntime.ClockEventNative
}

func serviceClockConfig(profile string) buildingruntime.ClockSchedulerConfig {
	return buildingruntime.ClockSchedulerConfig{
		Profile: profile, MaxAge: 5 * time.Second,
		Start: bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, LeaseMS: 30000, MaxTicks: 600,
			Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(),
				HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.5),
				HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}},
	}
}

// Session owns the attached worker's drain, including failed startup cleanup.
// Starting these loops does not enable Player or acquire native authority.
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, profile string, timeout time.Duration) error {
	scheduler, err := buildingruntime.NewClockScheduler(player, session, reads, serviceClockConfig(profile), wallClock{})
	if err != nil {
		return err
	}
	_, err = buildingruntime.NewClockWorker(ctx, scheduler, reads, buildingruntime.ClockWorkerConfig{
		PollInterval: time.Second, RenewInterval: 5 * time.Second, StepInterval: time.Second,
		MaxBackoff: 10 * time.Second, CallTimeout: timeout, PageLimit: 128,
	})
	return err
}

package main

import (
	"context"
	"errors"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

type serviceClockReview struct {
	journal *store.Store
	profile string
}

func (s serviceClockReview) Read(ctx context.Context) (store.ClockReviewState, error) {
	return s.journal.ReadClockReview(ctx, s.profile)
}
func (s serviceClockReview) Acknowledge(ctx context.Context, ack store.ClockAcknowledgement) (store.ClockReviewState, error) {
	return s.journal.AcknowledgeClockEvents(ctx, s.profile, ack)
}

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
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, profile string, timeout time.Duration, routine, sleeping bool) error {
	config := serviceClockConfig(profile)
	if sleeping && !routine {
		return errors.New("sleeping plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return errors.New("routine reviews require typed colony and emergency observations")
		}
		reviewer, err := buildingruntime.NewRoutineReviewer(player, native, wallClock{}, policy.DefaultRoutinePolicy(), config.MaxAge)
		if err != nil {
			return err
		}
		config.Routine = reviewer
		if sleeping {
			source, ok := reads.(buildingruntime.SleepingMethodSource)
			if !ok {
				return errors.New("sleeping plans require typed placement previews")
			}
			config.Sleeping, err = buildingruntime.NewRoutineSleepingPlanner(reviewer, source)
			if err != nil {
				return err
			}
		}
	}
	scheduler, err := buildingruntime.NewClockScheduler(player, session, reads, config, wallClock{})
	if err != nil {
		return err
	}
	_, err = buildingruntime.NewClockWorker(ctx, scheduler, reads, buildingruntime.ClockWorkerConfig{
		PollInterval: time.Second, RenewInterval: 5 * time.Second, StepInterval: time.Second,
		MaxBackoff: 10 * time.Second, CallTimeout: timeout, PageLimit: 128,
	})
	return err
}

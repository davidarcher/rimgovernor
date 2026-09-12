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
func startServiceClock(ctx context.Context, player *buildingruntime.Player, session *buildingruntime.Session, reads serviceClockReads, profile string, timeout time.Duration, routine, sleeping, cooking, shelter, comfort, expansion, power bool, projectLimit int) error {
	config := serviceClockConfig(profile)
	config.RoutineMethods = session.RoutineMethodsEnabled()
	if (sleeping || cooking || shelter || comfort || expansion || power) && !routine {
		return errors.New("building plans require routine reviews")
	}
	if routine {
		native, ok := reads.(observation.RoutineSource)
		if !ok {
			return errors.New("routine reviews require typed colony and emergency observations")
		}
		thresholds := policy.DefaultRoutinePolicy()
		thresholds.MaxDevelopmentProjects = projectLimit
		capabilities := buildingruntime.RoutineCapabilities{}
		if power {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureBasicPower)
		}
		if comfort {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureComfort)
		}
		if expansion {
			capabilities.Methods = append(capabilities.Methods, policy.EnsureExpansion)
		}
		reviewer, err := buildingruntime.NewRoutineReviewer(player, native, wallClock{}, thresholds, config.MaxAge, capabilities)
		if err != nil {
			return err
		}
		config.Routine = reviewer
		if sleeping || cooking || shelter || comfort || expansion || power {
			source, ok := reads.(buildingruntime.RoutineBuildingSource)
			if !ok {
				return errors.New("building plans require typed placement previews")
			}
			if shelter {
				config.Sleeping, err = buildingruntime.NewRoutineShelterPlanner(reviewer, source)
				if err != nil {
					return err
				}
			} else if sleeping {
				config.Sleeping, err = buildingruntime.NewRoutineSleepingPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if power {
				config.Power, err = buildingruntime.NewRoutinePowerPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if cooking {
				config.Cooking, err = buildingruntime.NewRoutineCookingPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if expansion {
				config.Expansion, err = buildingruntime.NewRoutineExpansionPlanner(reviewer, source)
				if err != nil {
					return err
				}
			}
			if comfort {
				config.Comfort, err = buildingruntime.NewRoutineComfortPlanner(reviewer, source)
				if err != nil {
					return err
				}
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

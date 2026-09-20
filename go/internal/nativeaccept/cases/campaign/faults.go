package campaign

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func init() {
	for _, c := range []cases.Case{faultOptionalPlanner(), faultLLMDown(), faultViewerDisconnect(), faultDashboardStalled(), faultCriticalGuard(), faultAuthorityLoss()} {
		cases.Register(c)
	}
}

// faultWindow is a fault case's window: a third of the foothold window
// (one game day by default), long enough for every planner class to run
// several steps and for a dropped lease (30 s) to lapse many times over.
func faultWindow() uint64 { return footholdWindow() / 3 }

const faultBudget = 25 * time.Minute
const faultReason = "one game day at Ultrafast on the player path with every family and a viewer, plus the reload"

// faultCase is a fault injection played for one window on the fresh
// baseline: setup is the roster read, the fault is armed on the launch
// (spec) or acted mid-phase (during), and verdict judges the phase.
func faultCase(name, scope, injection string, opts playOptions, verdict func(context.Context, cases.Session, *campaign, *phase, []string) error) cases.Case {
	window := faultWindow()
	return campaignCase(name, scope, faultReason, faultBudget, func(ctx context.Context, s cases.Session) error {
		c, err := newCampaign(s)
		if err != nil {
			return err
		}
		initial, err := initialColonists(ctx, s.Harness(), c.report)
		if err != nil {
			return err
		}
		c.setupDone()
		c.inject(injection, nil)
		watch := opts.Watch
		watch.Watch, watch.Window, watch.PollTicks, watch.Goal, watch.Extra = 18*time.Minute, window, 2500, policy.EnsureFoodSupply, campaignGoals
		watch.FailFast = sustainedfood.FailFast{Disabled: true}
		opts.Watch = watch
		p, err := c.play(ctx, "fault", opts)
		if err != nil && !opts.NoFailFast {
			return err
		}
		if p == nil {
			return err
		}
		c.milestone("fault_window_end", p.lastTick())
		return verdict(ctx, s, c, p, initial)
	})
}

// keepsPlaying is the "must keep playing" verdict: the window was played
// through in automate, goal progress advanced, the colony ended fed,
// sheltered and whole, and the harness never helped.
func keepsPlaying(ctx context.Context, s cases.Session, c *campaign, p *phase, initial []string, window uint64) error {
	return errors.Join(p.assertPlaying(window), p.assertProgress(), auditColony(ctx, s.Harness(), s, c.report, "end_state", initial), c.assertUnassisted())
}

// withFault arms a serve-side fault (buildingruntime.FaultsEnv) on launch.
func withFault(spec string) func(*na.ServeSpec) {
	return func(s *na.ServeSpec) {
		s.Env = append(s.Env, buildingruntime.FaultsEnv+"="+spec)
	}
}

// faultOptionalPlanner is campaign/fault-optional-planner: the lighting
// planner (optional class, #623) fails every step; the failure is isolated
// (#62) and the campaign plays on.
func faultOptionalPlanner() cases.Case {
	return faultCase("campaign/fault-optional-planner", "An optional planner failing every step leaves the campaign playing.", "planner:lighting=fail",
		playOptions{Spec: withFault("planner:lighting=fail")},
		func(ctx context.Context, s cases.Session, c *campaign, p *phase, initial []string) error {
			// The scheduler logs each isolated failure (#62) with the
			// fault's own text.
			failures := logLines(p.Stderr, "fault injected: lighting fails every step")
			c.report["isolated_planner_failures"] = failures
			if failures == 0 {
				return fmt.Errorf("the service log never recorded the injected lighting failure (%s)", p.Stderr)
			}
			return keepsPlaying(ctx, s, c, p, initial, faultWindow())
		})
}

// faultLLMDown is campaign/fault-llm-down: serve is configured with a chat
// model at an endpoint nothing listens on; a chat request during the phase
// fails, and the campaign plays on without it.
func faultLLMDown() cases.Case {
	return faultCase("campaign/fault-llm-down", "A chat model endpoint that is down leaves the campaign playing.", "chat endpoint http://127.0.0.1:1 (nothing listens)",
		playOptions{
			Spec: func(s *na.ServeSpec) {
				s.Extra = append(s.Extra, "--chat-model", "campaign-fault", "--chat-base-url", "http://127.0.0.1:1/v1")
			},
			During: func(ctx context.Context, service *na.ServiceProcess, _ *na.ViewerClient) (map[string]any, error) {
				// Ask mid-phase, once play is under way.
				if !sleep(ctx, 90*time.Second) {
					return nil, nil
				}
				body := map[string]any{"requestId": "campaign-fault-chat", "expected": service.Identity, "message": "status?"}
				reply, status, err := service.API("POST", "/api/chat", body, service.Token)
				out := map[string]any{"status": status, "reply": reply}
				if err != nil {
					out["error"] = err.Error()
					return out, nil
				}
				if status < 500 {
					return out, fmt.Errorf("/api/chat answered %d against a dead endpoint, want a 5xx failure", status)
				}
				return out, nil
			},
		},
		func(ctx context.Context, s cases.Session, c *campaign, p *phase, initial []string) error {
			return keepsPlaying(ctx, s, c, p, initial, faultWindow())
		})
}

// faultViewerDisconnect is campaign/fault-viewer-disconnect: the dashboard
// viewer drops its socket and lease mid-run and comes back; the campaign
// plays on through both.
func faultViewerDisconnect() cases.Case {
	return faultCase("campaign/fault-viewer-disconnect", "A dashboard viewer disconnecting mid-run leaves the campaign playing.", "viewer stop and restart mid-phase",
		playOptions{
			During: func(ctx context.Context, service *na.ServiceProcess, viewer *na.ViewerClient) (map[string]any, error) {
				if !sleep(ctx, 2*time.Minute) {
					return nil, nil
				}
				first := viewer.Stop()
				if !sleep(ctx, 30*time.Second) {
					return map[string]any{"first": first}, nil
				}
				second := na.StartViewerWith(ctx, service, service.Token, na.ViewerOptions{ID: "campaign-viewer-2"})
				<-ctx.Done()
				return map[string]any{"first": first, "second": second.Stop()}, nil
			},
		},
		func(ctx context.Context, s cases.Session, c *campaign, p *phase, initial []string) error {
			return keepsPlaying(ctx, s, c, p, initial, faultWindow())
		})
}

// faultDashboardStalled is campaign/fault-dashboard-stalled: the viewer
// holds its socket and lease but never reads a frame; the campaign plays
// on regardless of the backed-up stream.
func faultDashboardStalled() cases.Case {
	return faultCase("campaign/fault-dashboard-stalled", "A dashboard client that stops consuming frames leaves the campaign playing.", "viewer connected and never reading",
		playOptions{Viewer: &na.ViewerOptions{ID: "campaign-viewer-stalled", Stalled: true}},
		func(ctx context.Context, s cases.Session, c *campaign, p *phase, initial []string) error {
			if stalled, _ := na.AsBool(p.Viewer["stalled"]); !stalled || na.AsNumber(p.Viewer["connects"]) == 0 {
				return fmt.Errorf("stalled viewer never connected: %v", p.Viewer)
			}
			return keepsPlaying(ctx, s, c, p, initial, faultWindow())
		})
}

// faultCriticalGuard is campaign/fault-critical-guard: the defense planner
// (critical class) never returns; the step holds past its wall budget,
// admits nothing (#623) and the game tick must not advance.
func faultCriticalGuard() cases.Case {
	return faultCase("campaign/fault-critical-guard", "A critical planner that hangs stops the campaign instead of admitting blind windows.", "planner:defense=hang",
		playOptions{Spec: withFault("planner:defense=hang"), NoFailFast: true},
		func(_ context.Context, _ cases.Session, c *campaign, p *phase, _ []string) error {
			held := flightHeldBy(p.Flight)
			admitted := stepsAdmitted(p.Flight)
			c.report["held_by"] = held
			c.report["steps_admitted"] = admitted
			var failures []error
			if held["defense"] == 0 {
				failures = append(failures, fmt.Errorf("no clock_step row held by defense (%d rows)", len(p.Flight)))
			}
			if admitted > 0 {
				failures = append(failures, fmt.Errorf("%d window(s) admitted under a hung critical planner", admitted))
			}
			failures = append(failures, p.assertStopped(faultWindow()))
			return errors.Join(failures...)
		})
}

// faultAuthorityLoss is campaign/fault-authority-loss: the epoch renewal
// is dropped, the native lease lapses and authority is revoked; the
// service must fall to manual and the tick stop. The keep-alive's
// re-acquisition is held off, since a hand that resumes play is the
// intervention this case forbids.
func faultAuthorityLoss() cases.Case {
	return faultCase("campaign/fault-authority-loss", "A lapsed native lease revokes authority and stops the campaign.", "renewal=drop",
		playOptions{
			Spec: withFault("renewal=drop"), NoFailFast: true,
			During: func(ctx context.Context, service *na.ServiceProcess, _ *na.ViewerClient) (map[string]any, error) {
				// Hold re-acquisition from the moment authority is granted.
				for ctx.Err() == nil {
					service.HoldAuthority(true)
					st, _, err := service.API("GET", "/api/state", nil, "")
					if err == nil && na.AsString(st["mode"]) == "automate" {
						break
					}
					sleep(ctx, 500*time.Millisecond)
				}
				<-ctx.Done()
				return nil, nil
			},
		},
		func(_ context.Context, _ cases.Session, c *campaign, p *phase, _ []string) error {
			var failures []error
			if p.Mode == "automate" {
				failures = append(failures, fmt.Errorf("service still in automate after the lease lapsed"))
			}
			if n := na.AsNumber(p.Keep["reacquired"]); n > 0 {
				failures = append(failures, fmt.Errorf("keep-alive re-acquired %v time(s); the hold was meant to stop it", n))
			}
			failures = append(failures, p.assertStopped(faultWindow()))
			return errors.Join(failures...)
		})
}

func sleep(ctx context.Context, d time.Duration) bool {
	select {
	case <-ctx.Done():
		return false
	case <-time.After(d):
		return true
	}
}

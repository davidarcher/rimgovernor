package mood

import (
	"context"
	"fmt"
	"math"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// epicFamilies serves the mood epic's cases (#2532): the family under test
// plus the tend and rescue pair that keeps a food-poisoning down from freezing
// the clock. Callers add the family their claim lives in.
func epicFamilies(family routinefamily.Family) []routinefamily.Family {
	return []routinefamily.Family{family, routinefamily.Tend, routinefamily.Rescue}
}

// drive is one serve-driven case's service across native fixture steps: the
// harness and the service cannot hold the game's sole bridge slot together, so
// a native step stops the service, reattaches, changes the game and launches
// the service again on the same journal.
type drive struct {
	s       cases.Session
	service *na.ServiceProcess
	journal *store.Store
}

// start launches the service (the first time, or again after native) and
// acquires authority; its journal opens on the same state path.
func (d *drive) start(ctx context.Context) error {
	var err error
	if d.service == nil {
		d.service, err = d.s.Serve(ctx, d.s.Spec())
	} else {
		if err = d.s.Release(); err != nil {
			return err
		}
		d.service.Identity = d.s.Identity()
		d.service, err = d.service.Restart(ctx)
	}
	if err != nil {
		return err
	}
	if _, err = d.service.Acquire(); err != nil {
		return err
	}
	d.service.KeepAuthority(ctx)
	d.journal, err = d.service.Store(ctx)
	return err
}

// native stops the service, takes the bridge slot back and pauses the game so
// the fixture op reads and writes a still world.
func (d *drive) native(ctx context.Context, label string) (*na.Harness, error) {
	d.service.Stop()
	h, err := d.s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	if _, err = h.Call(ctx, label+"-pause", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	return h, nil
}

// wait polls probe until it reports done, bounded by a ceiling and a stall.
func (d *drive) wait(ctx context.Context, ceiling time.Duration, probe na.Probe) error {
	return na.WaitProgress(ctx, na.Wait{Ceiling: ceiling, Stall: 2 * time.Minute, Interval: time.Second, Terminal: d.service.Exited}, probe)
}

// world is the loaded journal's world key.
func (d *drive) world(ctx context.Context) (store.Rounds, store.World, error) {
	r, err := d.journal.LoadRounds(ctx)
	if err != nil {
		return store.Rounds{}, store.World{}, err
	}
	return r, store.World{Colony: r.Snapshot.Colony, Load: r.Snapshot.Load, Map: r.Snapshot.Map}, nil
}

// fixtureCall runs a test op and requires its success flag.
func fixtureCall(ctx context.Context, h *na.Harness, label, op string, args map[string]any) (map[string]any, error) {
	reply, err := h.Call(ctx, label, op, args)
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s: %s refused: %#v", label, op, reply)
	}
	return reply, nil
}

// near is whether two readings agree to within tol.
func near(a, b, tol float64) bool { return math.Abs(a-b) <= tol }

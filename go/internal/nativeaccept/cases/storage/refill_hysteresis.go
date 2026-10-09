// Package storage holds the storage hauling cases.
package storage

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

const (
	prepareOp = "test/refill_hysteresis_prepare"
	takeOp    = "test/refill_hysteresis_take"
	readOp    = "test/refill_hysteresis_read"
	// idleTicks is long enough for an idle hauler to walk to the loose stack
	// and carry it, so an unchanged store means the haul was withheld.
	idleTicks = 1500
	// refillWindow bounds the refill once a stack's worth of room is open.
	refillWindow = 3000
)

func init() {
	cases.Register(cases.Case{
		Name: "storage/refill-hysteresis",
		Scope: "A full Steel stockpile (a stockpile zone is also a bench input store) with a loose stack waiting: under supervision " +
			"taking one item out starts no refill haul, taking a stack's worth out starts it (#2519). Native: the HaulToStorageJob " +
			"postfix and the idle hauler's work scanner, which no Go snapshot reaches.",
		Start:       cases.Fixture{Op: prepareOp, On: cases.LabStart()},
		RequiredOps: []string{prepareOp, takeOp, readOp},
		QuietWorld:  true,
		Budget:      6 * time.Minute,
		Crew:        cases.Crew{Size: 3},
		Run:         run,
	})
}

type state struct{ store, loose int }

func run(ctx context.Context, s cases.Session) error {
	h, report := s.Harness(), s.Report()
	prepared := s.Prepared()
	stack, store := int(na.AsNumber(prepared["stackLimit"])), int(na.AsNumber(prepared["store"]))
	if stack <= 1 || store != 2*stack {
		return fmt.Errorf("prepare: unexpected fixture reply %#v", prepared)
	}
	read := func(label string) (state, error) {
		reply, err := h.Call(ctx, label, readOp, map[string]any{})
		if err != nil {
			return state{}, err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return state{}, fmt.Errorf("%s refused: %#v", readOp, reply)
		}
		return state{int(na.AsNumber(reply["store"])), int(na.AsNumber(reply["loose"]))}, nil
	}
	take := func(label string, n int) error {
		reply, err := h.Call(ctx, label, takeOp, map[string]any{"count": n})
		if err != nil {
			return err
		}
		if ok, _ := na.AsBool(reply["success"]); !ok {
			return fmt.Errorf("%s %d refused: %#v", takeOp, n, reply)
		}
		return nil
	}
	start, err := read("start")
	if err != nil {
		return err
	}
	if start.store != store {
		return fmt.Errorf("the store starts at %d, want %d: %#v", start.store, store, start)
	}

	// One item out: room below a stack, so the loose stack stays put.
	if err := take("take-one", 1); err != nil {
		return err
	}
	if _, err := s.Advance(ctx, idleTicks); err != nil {
		return err
	}
	idle, err := read("after-one")
	if err != nil {
		return err
	}
	report["after_one"] = idle
	if idle.store != store-1 || idle.loose != start.loose {
		return fmt.Errorf("one item out of a full store started a refill: %#v, want store %d loose %d", idle, store-1, start.loose)
	}

	// A stack's worth out: the hauler refills.
	if err := take("take-stack", stack-1); err != nil {
		return err
	}
	for advanced := 0; ; advanced += 300 {
		now, err := read(fmt.Sprintf("refill-%d", advanced))
		if err != nil {
			return err
		}
		if now.loose < start.loose {
			report["refill_ticks"] = advanced
			return nil
		}
		if advanced >= refillWindow {
			return fmt.Errorf("a stack's worth of room opened and no refill started in %d ticks: %#v", advanced, now)
		}
		if _, err := s.Advance(ctx, 300); err != nil {
			return err
		}
	}
}

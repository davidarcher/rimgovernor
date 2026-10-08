package main

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

func TestGoAutosaverIntervalRotationAndFailure(t *testing.T) {
	var names []string
	var fail error
	a := newGoAutosaver(func(_ context.Context, r *l.SaveRequest) (*l.SaveReply, bridge.Result, error) {
		if fail != nil {
			return nil, bridge.Result{}, fail
		}
		names = append(names, r.GetSaveName())
		return &l.SaveReply{}, bridge.Result{}, nil
	})
	ctx := context.Background()
	a.AtStop(ctx, nil, 1000) // baseline
	a.AtStop(ctx, nil, 1000+autosaveIntervalTicks-1)
	if len(names) != 0 {
		t.Fatalf("saved before the interval: %v", names)
	}
	fail = bridge.ErrSaveUncertain
	a.AtStop(ctx, nil, 1000+autosaveIntervalTicks)
	if len(names) != 0 || a.last != 1000 {
		t.Fatalf("failed save moved the mark: names=%v last=%d", names, a.last)
	}
	fail = errors.New("flush failed")
	a.AtStop(ctx, nil, 1000+autosaveIntervalTicks+10)
	fail = nil
	tick := int64(1000)
	for i := 0; i < 7; i++ {
		tick += autosaveIntervalTicks
		a.AtStop(ctx, nil, tick)
		a.AtStop(ctx, nil, tick+1) // same interval: no second save
	}
	want := []string{"Autosave-1", "Autosave-2", "Autosave-3", "Autosave-4", "Autosave-5", "Autosave-1", "Autosave-2"}
	if len(names) != len(want) {
		t.Fatalf("saves = %v, want %v", names, want)
	}
	for i := range want {
		if names[i] != want[i] {
			t.Fatalf("saves = %v, want %v", names, want)
		}
	}
	// A rewound tick (a load) re-baselines without saving.
	a.AtStop(ctx, nil, 5)
	a.AtStop(ctx, nil, 5+autosaveIntervalTicks-1)
	if len(names) != len(want) {
		t.Fatalf("saved after a rewind: %v", names)
	}
}

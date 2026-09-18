package main

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	commonpb "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	lifecyclepb "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// longitudeIdentityFake is the observation.Source readMoodReliefLongitude
// needs: the tick read that names the colony identity.
type longitudeIdentityFake struct{ err error }

func (f longitudeIdentityFake) Tick(context.Context) (*lifecyclepb.TickReply, bridge.Result, error) {
	if f.err != nil {
		return nil, bridge.Result{}, f.err
	}
	return &lifecyclepb.TickReply{Outcome: &lifecyclepb.TickReply_Loaded{Loaded: &lifecyclepb.LoadedTick{Context: serviceContext()}}}, bridge.Result{}, nil
}

var _ observation.Source = longitudeIdentityFake{}

// longitudeWorldFake is a moodReliefWorldSource fake that records which tile
// ReadWorld was called with, so a test can prove the home map's Tile (not
// some other map's) drove the lookup.
type longitudeWorldFake struct {
	progression    bridge.WorldProgressionRead
	progressionErr error
	world          bridge.WorldRead
	worldErr       error
	requestedTile  int32
}

func (f *longitudeWorldFake) ReadWorldProgression(context.Context, *commonpb.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	if f.progressionErr != nil {
		return bridge.WorldProgressionRead{}, bridge.Result{}, f.progressionErr
	}
	return f.progression, bridge.Result{}, nil
}
func (f *longitudeWorldFake) ReadWorld(_ context.Context, _ *commonpb.Identity, tile int32, _ float64) (bridge.WorldRead, bridge.Result, error) {
	f.requestedTile = tile
	if f.worldErr != nil {
		return bridge.WorldRead{}, bridge.Result{}, f.worldErr
	}
	return f.world, bridge.Result{}, nil
}

var _ moodReliefWorldSource = &longitudeWorldFake{}

func TestReadMoodReliefLongitudeSucceedsFromHomeMap(t *testing.T) {
	world := &longitudeWorldFake{
		progression: bridge.WorldProgressionRead{Maps: []bridge.WorldMap{
			{ID: 1, Tile: 7, Home: false},
			{ID: 2, Tile: 42, Home: true},
		}},
		world: bridge.WorldRead{Longitude: domain.Known(12.5)},
	}
	got := readMoodReliefLongitude(context.Background(), longitudeIdentityFake{}, world)
	value, known := got.Value()
	if !known || value != 12.5 {
		t.Fatalf("longitude = %+v", got)
	}
	if world.requestedTile != 42 {
		t.Fatalf("requested tile = %d, want home tile 42", world.requestedTile)
	}
}

func TestReadMoodReliefLongitudeStaysUnknownOnFailure(t *testing.T) {
	cases := map[string]struct {
		identity observation.Source
		world    moodReliefWorldSource
	}{
		"nil sources":         {nil, nil},
		"identity read fails": {longitudeIdentityFake{err: errors.New("boom")}, &longitudeWorldFake{}},
		"no home map": {longitudeIdentityFake{}, &longitudeWorldFake{
			progression: bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 1, Tile: 7, Home: false}}},
		}},
		"progression read fails": {longitudeIdentityFake{}, &longitudeWorldFake{progressionErr: errors.New("boom")}},
		"world read fails": {longitudeIdentityFake{}, &longitudeWorldFake{
			progression: bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 1, Tile: 7, Home: true}}},
			worldErr:    errors.New("boom"),
		}},
		"world read returns unknown longitude": {longitudeIdentityFake{}, &longitudeWorldFake{
			progression: bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 1, Tile: 7, Home: true}}},
			world:       bridge.WorldRead{Longitude: domain.Unknown[float64]()},
		}},
	}
	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			got := readMoodReliefLongitude(context.Background(), tc.identity, tc.world)
			if _, known := got.Value(); known {
				t.Fatalf("longitude = %+v, want unknown", got)
			}
		})
	}
}

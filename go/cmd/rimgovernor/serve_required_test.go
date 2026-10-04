package main

import (
	"context"
	"errors"
	"io"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

var errUnusedFakeRead = errors.New("fake native read is unused")

// The serve fakes answer every typed read serve requires at startup (#1670)
// with an error; tests that exercise a route override it on a wrapper type.
func (*serviceFake) ReadCamera(context.Context, *p.ReadRequest) (*p.CameraReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (*serviceFake) ReadSelection(context.Context, *p.ReadRequest) (*p.SelectionReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (*serviceFake) ReadColonistRoster(context.Context, *p.ColonistRosterRequest) (*p.ColonistRosterReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (*serviceFake) ReadRenderState(context.Context, *p.ReadRequest) (*p.RenderReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (*serviceFake) ReadNotifications(context.Context, *p.NotificationsRequest) (*p.NotificationsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}

func (*buildingReadFake) FlushSnapshot(context.Context) error { return nil }
func (*buildingReadFake) GovernorState(context.Context) (map[string]string, error) {
	return map[string]string{}, nil
}
func (*buildingReadFake) PutGovernorState(context.Context, string, string) error { return nil }
func (*buildingReadFake) ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{}, bridge.Result{}, errUnusedFakeRead
}
func (*buildingReadFake) ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}

func (unusedBuildingCapabilities) Identity(context.Context) (*lifecyclepb.IdentityReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (unusedBuildingCapabilities) ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	return bridge.WorldProgressionRead{}, bridge.Result{}, errUnusedFakeRead
}
func (unusedBuildingCapabilities) ReadColonyFacts(context.Context, *c.Identity, bool) (*n.ColonyFactsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (unusedBuildingCapabilities) ReadHomeColonists(context.Context, *c.Identity) (*n.ListPawnsReply, bridge.Result, error) {
	return nil, bridge.Result{}, errUnusedFakeRead
}
func (unusedBuildingCapabilities) ReadTradeSession(context.Context, *c.Identity) (bridge.TradeSessionRead, bridge.Result, error) {
	return bridge.TradeSessionRead{}, bridge.Result{}, errUnusedFakeRead
}

type unusedAttention struct{}

func (unusedAttention) AckAttention(context.Context, string) error { return errUnusedFakeRead }

// completeBuildingBridge is a client carrying every capability serve requires.
func completeBuildingBridge(reads serviceBridge, caps unusedBuildingCapabilities) buildingServiceBridge {
	return buildingServiceBridge{reads: reads, native: caps, authority: caps, writes: caps,
		movement:  &buildingruntime.MovementCapabilities{Writer: caps},
		trade:     &buildingruntime.TradeCapabilities{Native: caps, Writer: caps},
		attention: unusedAttention{}}
}

// The wrappers below hide typed reads from a complete fake: a struct
// embedding only an interface promotes just that interface's methods.

// bareReads exposes only the observation source.
type bareReads struct{ serviceBridge }

type withoutPresentation struct {
	serviceBridge
	httpapi.NotificationReader
}
type withoutNotifications struct {
	serviceBridge
	httpapi.PresentationReader
}

// withoutFlush has every typed read except FlushSnapshot.
type withoutFlush struct {
	serviceBridge
	httpapi.PresentationReader
	httpapi.NotificationReader
	governorStateNative
	buildingruntime.BreakResponseSource
}

type withoutState struct {
	serviceBridge
	httpapi.PresentationReader
	httpapi.NotificationReader
	buildingruntime.BreakResponseSource
}

func (withoutState) FlushSnapshot(context.Context) error { return nil }

type withoutBreaks struct {
	serviceBridge
	httpapi.PresentationReader
	httpapi.NotificationReader
	governorStateNative
}

func (withoutBreaks) FlushSnapshot(context.Context) error { return nil }

// Every native or client serve used to treat as optional fails startup with
// its name (#1670).
func TestServeRefusesAClientMissingARequiredNative(t *testing.T) {
	caps := unusedBuildingCapabilities{}
	fake := &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 2)}}
	cases := []struct {
		name   string
		want   string
		client func() buildingServiceBridge
	}{
		{"presentation reader", "the presentation reader", func() buildingServiceBridge {
			return completeBuildingBridge(withoutPresentation{fake, fake}, caps)
		}},
		{"notification reader", "the notification reader", func() buildingServiceBridge {
			return completeBuildingBridge(withoutNotifications{fake, fake}, caps)
		}},
		{"attention acknowledger", "the attention acknowledger", func() buildingServiceBridge {
			b := completeBuildingBridge(fake, caps)
			b.attention = nil
			return b
		}},
		{"movement capabilities", "typed movement capabilities", func() buildingServiceBridge {
			b := completeBuildingBridge(fake, caps)
			b.movement = nil
			return b
		}},
		{"trade capabilities", "typed trade capabilities", func() buildingServiceBridge {
			b := completeBuildingBridge(fake, caps)
			b.trade = nil
			return b
		}},
		{"snapshot flush", "the snapshot flush", func() buildingServiceBridge {
			return completeBuildingBridge(withoutFlush{fake, fake, fake, fake, fake}, caps)
		}},
		{"governor state", "the governor state component", func() buildingServiceBridge {
			return completeBuildingBridge(withoutState{fake, fake, fake, fake}, caps)
		}},
		{"break response reads", "the break response reads", func() buildingServiceBridge {
			return completeBuildingBridge(withoutBreaks{fake, fake, fake, fake}, caps)
		}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			config := serveConfig{playerControl: true, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
			err := serveBuildingWithBridge(context.Background(), config, io.Discard, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
				return tc.client(), nil
			})
			if err == nil || !strings.Contains(err.Error(), "serve requires "+tc.want) {
				t.Fatalf("startup error %v, want serve requires %s", err, tc.want)
			}
		})
	}
}

func TestReadOnlyServeRefusesAClientWithoutPresentationReaders(t *testing.T) {
	dir := t.TempDir()
	config := serveConfig{state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: time.Second, bridge: bridge.ProcessConfig{Timeout: time.Second}}
	err := serveWithBridge(context.Background(), config, io.Discard, func(context.Context, bridge.ProcessConfig) (serviceBridge, error) {
		return bareReads{&serviceFake{}}, nil
	})
	if err == nil || !strings.Contains(err.Error(), "serve requires the presentation reader") {
		t.Fatalf("startup error %v", err)
	}
}

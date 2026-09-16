package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

func TestBuildingServeRequiresAbsoluteProfile(t *testing.T) {
	dir := t.TempDir()
	withRoutineFamilies(t, "", false)
	config, err := parseServe(append(serveBase(dir), "--profile", dir), io.Discard)
	if err != nil || !config.playerControl || config.profile != dir {
		t.Fatalf("config: %+v %v", config, err)
	}
	for _, args := range [][]string{append(serveBase(dir), "--profile", "relative"), serveBase(dir)} {
		if _, err := parseServe(args, io.Discard); err == nil {
			t.Fatalf("accepted %v", args)
		}
	}
}

type buildingAddressWriter chan string

func (w buildingAddressWriter) Write(data []byte) (int, error) {
	w <- strings.TrimSpace(strings.TrimPrefix(string(data), "RimGovernor Go player service: "))
	return len(data), nil
}

type buildingReadFake struct {
	serviceFake
	block atomic.Bool
}

func (f *buildingReadFake) Identity(ctx context.Context) (*lifecyclepb.IdentityReply, bridge.Result, error) {
	f.active.Add(1)
	defer f.active.Add(-1)
	if f.block.Load() {
		select {
		case f.entered <- struct{}{}:
		default:
		}
		<-ctx.Done()
		return nil, bridge.Result{}, ctx.Err()
	}
	return &lifecyclepb.IdentityReply{Outcome: &lifecyclepb.IdentityReply_Loaded{Loaded: &lifecyclepb.LoadedIdentity{Context: serviceContext(), Paused: proto.Bool(true)}}}, bridge.Result{}, nil
}

// Any native control/placement call panics. Startup and submission must only read.
type unusedBuildingCapabilities struct {
	boundary.Native
	buildingruntime.NativeAuthority
	boundary.BuildingWriter
}

type unusedDraftCapabilities struct {
	draft.DraftNative
	draft.DraftWriter
	draft.DraftCleanupWriter
}

func unusedDrafts() *draft.DraftCapabilities {
	caps := unusedDraftCapabilities{}
	return &draft.DraftCapabilities{Native: caps, Writer: caps, Cleanup: caps}
}

type unusedQuestAcceptCapabilities struct {
	buildingruntime.QuestAcceptNative
	buildingruntime.QuestAcceptWriter
}

func unusedQuestAccept() *buildingruntime.QuestAcceptCapabilities {
	caps := unusedQuestAcceptCapabilities{}
	return &buildingruntime.QuestAcceptCapabilities{Native: caps, Writer: caps}
}

type unusedSettlementGiftCapabilities struct {
	buildingruntime.SettlementGiftNative
	buildingruntime.SettlementGiftWriter
}

func unusedSettlementGift() *buildingruntime.SettlementGiftCapabilities {
	caps := unusedSettlementGiftCapabilities{}
	return &buildingruntime.SettlementGiftCapabilities{Native: caps, Writer: caps}
}

type unusedCaravanDepartureCapabilities struct {
	buildingruntime.CaravanDepartureNative
	buildingruntime.CaravanDepartureWriter
}

func unusedCaravanDeparture() *buildingruntime.CaravanDepartureCapabilities {
	caps := unusedCaravanDepartureCapabilities{}
	return &buildingruntime.CaravanDepartureCapabilities{Native: caps, Writer: caps, Policy: defaultCaravanDeparturePolicy}
}

func TestBuildingServiceSubmissionDoesNotAcquireAndShutdownJoins(t *testing.T) {
	dir := t.TempDir()
	fake := &buildingReadFake{serviceFake: serviceFake{entered: make(chan struct{}, 2)}}
	caps := unusedBuildingCapabilities{}
	config := serveConfig{playerControl: true, profile: dir, state: filepath.Join(dir, "state.db"), listen: "127.0.0.1:0", refresh: 20 * time.Millisecond, bridge: bridge.ProcessConfig{Timeout: time.Second}}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	addresses := make(buildingAddressWriter, 1)
	done := make(chan error, 1)
	go func() {
		done <- serveBuildingWithBridge(ctx, config, addresses, func(context.Context, bridge.ProcessConfig) (buildingServiceBridge, error) {
			return buildingServiceBridge{reads: fake, native: caps, authority: caps, writes: caps, draft: unusedDrafts(), questAccept: unusedQuestAccept(), settlementGift: unusedSettlementGift(), caravanDeparture: unusedCaravanDeparture()}, nil
		})
	}()
	var address string
	select {
	case address = <-addresses:
	case err := <-done:
		t.Fatalf("startup: %v", err)
	case <-time.After(4 * time.Second):
		t.Fatal("startup timeout")
	}
	if other, err := runtimeowner.Acquire(context.Background(), dir); err == nil {
		other.Close()
		t.Fatal("profile has two owners")
	}
	client := &http.Client{Timeout: 2 * time.Second}
	read := func(path string) []byte {
		t.Helper()
		response, err := client.Get(address + path)
		if err != nil {
			t.Fatal(err)
		}
		defer response.Body.Close()
		data, err := io.ReadAll(response.Body)
		if err != nil || response.StatusCode != 200 {
			t.Fatalf("read %s: %d %s %v", path, response.StatusCode, data, err)
		}
		return data
	}
	var bootstrap struct {
		Token string `json:"token"`
	}
	if err := json.Unmarshal(read("/api/player/session"), &bootstrap); err != nil || len(bootstrap.Token) != 64 {
		t.Fatal("bootstrap", err)
	}
	payload := `{"requestId":"submit-one","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"building":{"defName":"Wall","stuff":"WoodLog","x":0,"z":0,"rotation":"north"}}`
	request, _ := http.NewRequest(http.MethodPost, address+"/api/buildings/plans", strings.NewReader(payload))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-RimGovernor-Player", bootstrap.Token)
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	data, err := io.ReadAll(response.Body)
	response.Body.Close()
	if err != nil || response.StatusCode != 201 {
		t.Fatalf("submit %d %s %v", response.StatusCode, data, err)
	}
	if !strings.Contains(string(read("/api/buildings/submission?requestId=submit-one")), `"requestId":"submit-one"`) {
		t.Fatal("submission not durable")
	}
	draftPayload := `{"requestId":"submit-draft","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"draft":{"pawnId":"pawn-one"}}`
	request, _ = http.NewRequest(http.MethodPost, address+"/api/drafts/plans", strings.NewReader(draftPayload))
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-RimGovernor-Player", bootstrap.Token)
	response, err = client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	data, err = io.ReadAll(response.Body)
	response.Body.Close()
	if err != nil || response.StatusCode != 201 {
		t.Fatalf("draft submission %d %s %v", response.StatusCode, data, err)
	}
	var draft struct {
		PlanID string `json:"planId"`
	}
	if err = json.Unmarshal(data, &draft); err != nil || draft.PlanID == "" {
		t.Fatal("draft plan", err)
	}
	if !strings.Contains(string(read("/api/drafts/submission?requestId=submit-draft")), `"pawnId":"pawn-one"`) {
		t.Fatal("draft submission not durable")
	}
	plan := string(read("/api/plan?id=" + draft.PlanID))
	if !strings.Contains(plan, `"kind":"owned_draft"`) || !strings.Contains(plan, `"stage":"pending"`) || strings.Contains(plan, `"building":`) {
		t.Fatal("draft projection", plan)
	}
	if !strings.Contains(string(read("/api/player/control")), `"enabled":false`) {
		t.Fatal("submission enabled authority")
	}
	var state httpapi.State
	if err := json.Unmarshal(read("/api/state"), &state); err != nil || state.Mode != "manual" || state.Generation != nil {
		t.Fatalf("startup state %+v %v", state, err)
	}
	fake.block.Store(true)
	select {
	case <-fake.entered:
	case <-time.After(2 * time.Second):
		t.Fatal("refresh did not start")
	}
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(4 * time.Second):
		t.Fatal("shutdown did not join")
	}
	if !fake.closed.Load() || fake.closeWhileReading.Load() {
		t.Fatal("bridge closed before readers joined")
	}
	owner, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal("profile retained after close", err)
	}
	owner.Close()
}

type fixedSnapshots struct{ value httpapi.Snapshot }

func (s fixedSnapshots) Snapshot(context.Context) (httpapi.Snapshot, error) { return s.value, nil }

type fixedControl struct{ value buildingruntime.ControlState }

func (s *fixedControl) State() buildingruntime.ControlState { return s.value }
func TestBuildingSnapshotUsesCurrentPermissionAndMatchingWorld(t *testing.T) {
	identity := observation.Identity{Colony: "colony", Map: 0, Load: "load", Tick: 1}
	control := &fixedControl{value: buildingruntime.ControlState{Enabled: true, ObservationKnown: true, Snapshot: domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Direction: 1, Native: 2}}}
	view := buildingSnapshots{fixedSnapshots{httpapi.Snapshot{Connected: true, Identity: domain.Known(identity)}}, control}
	state, err := view.Snapshot(context.Background())
	if err != nil || state.Mode != "automate" {
		t.Fatal(state, err)
	}
	if plan, known := state.ActivePlanID.Value(); !known || plan != "plan" {
		t.Fatal("missing live plan")
	}
	control.value.Enabled = false
	state, _ = view.Snapshot(context.Background())
	if state.Mode != "manual" {
		t.Fatal("historical scope enabled control")
	}
	control.value.Enabled = true
	control.value.Snapshot.Load = "other-load"
	state, _ = view.Snapshot(context.Background())
	if _, known := state.Generation.Value(); known {
		t.Fatal("mixed world generation")
	}
	if state.Mode != "manual" || state.Status == "Player execution enabled" {
		t.Fatal("enabled status attributed to another world")
	}
	control.value.Snapshot.Load = "load"
	view.reads = fixedSnapshots{httpapi.Snapshot{Connected: true, Stale: true, Identity: domain.Known(identity)}}
	state, _ = view.Snapshot(context.Background())
	if state.Mode != "manual" {
		t.Fatal("stale observations claimed enabled execution")
	}
}

type retryBuildingClose struct{ calls int }

func (c *retryBuildingClose) Close(context.Context) error {
	c.calls++
	if c.calls == 1 {
		return errors.New("join still pending")
	}
	return nil
}
func TestBuildingCleanupRetriesJoinBeforeReturning(t *testing.T) {
	c := &retryBuildingClose{}
	if err := drainBuilding(c); err == nil || c.calls != 2 {
		t.Fatal("cleanup did not retain first failure and join", c.calls, err)
	}
}

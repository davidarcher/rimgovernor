package observation

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

const identityJSON = `{"success":true,"colonyId":"colony","loadToken":"load","mapId":0,"tick":0,"observationBatchVersion":1,"placementPreviewBatchVersion":1}`
const statusJSON = `{"success":true,"status":"game_loaded","time":{"ticksGame":999,"paused":true,"forcePaused":false,"pausedByPlayer":true,"timeSpeed":"Paused"},"skipped":[],"colonists":[],"operation":{"id":"receipt"}}`

func TestIdentityRequiredFieldsAndZeroMapTick(t *testing.T) {
	identity, err := DecodeIdentity(json.RawMessage(identityJSON))
	if err != nil {
		t.Fatal(err)
	}
	if identity.Map != 0 || identity.Tick != 0 || identity.Load != "load" {
		t.Fatalf("identity %+v", identity)
	}
	for _, input := range []string{
		strings.Replace(identityJSON, `"tick":0`, `"tick":1.0`, 1),
		strings.Replace(identityJSON, `"tick":0`, `"tick":-1`, 1),
		strings.Replace(identityJSON, `"mapId":0`, `"mapId":null`, 1),
		strings.Replace(identityJSON, `"mapId":0`, `"mapId":0,"mapId":1`, 1),
		strings.Replace(identityJSON, `"loadToken":"load",`, "", 1),
		strings.Replace(identityJSON, `"loadToken":"load"`, `"loadToken":"\ud800"`, 1),
		strings.Replace(identityJSON, `"success":true`, `"success":false`, 1),
		identityJSON + `{}`,
	} {
		if _, err := DecodeIdentity(json.RawMessage(input)); !errors.Is(err, ErrContract) {
			t.Fatalf("accepted %s: %v", input, err)
		}
	}
}

func TestClockProjectionKeepsUnavailableFactsUnknown(t *testing.T) {
	status, err := DecodeStatus(json.RawMessage(statusJSON))
	if err != nil {
		t.Fatal(err)
	}
	if paused, known := status.Paused.Value(); !known || !paused {
		t.Fatal("known pause missing")
	}
	if _, known := status.ForcePaused.Value(); known {
		t.Fatal("native false fallback treated as known")
	}
	for _, input := range []string{
		`{"success":true,"status":"no_game","time":{"paused":true,"timeSpeed":"Paused"},"skipped":[]}`,
		`{"success":true,"status":"no_map","time":{"paused":true,"timeSpeed":"Paused"},"skipped":[]}`,
		`{"success":true,"status":"game_loaded","time":null,"skipped":[]}`,
		`{"success":true,"status":"game_loaded","time":{"paused":false,"forcePaused":false},"skipped":[]}`,
		`{"success":true,"status":"game_loaded","time":{"paused":true},"skipped":[{"field":"time","reason":"unreadable"}]}`,
		`{"success":true,"status":"game_loaded","time":{"paused":true}}`,
	} {
		got, err := DecodeStatus(json.RawMessage(input))
		if err != nil {
			t.Fatal(err)
		}
		if _, known := got.Paused.Value(); known {
			t.Fatalf("invented pause fact from %s", input)
		}
		if _, known := got.Speed.Value(); known {
			t.Fatalf("invented speed fact from %s", input)
		}
	}
	normal := `{"success":true,"status":"game_loaded","time":{"paused":false,"forcePaused":false,"timeSpeed":"Normal"},"skipped":[{"field":"time.ticksAbs","reason":"unavailable"}]}`
	got, err := DecodeStatus(json.RawMessage(normal))
	if err != nil {
		t.Fatal(err)
	}
	if speed, known := got.Speed.Value(); !known || speed != Normal {
		t.Fatal("calendar unavailability erased actual speed")
	}
	if _, known := got.Paused.Value(); known {
		t.Fatal("speed control assumed absence of forced pause")
	}
	force := strings.Replace(normal, `"forcePaused":false`, `"forcePaused":true`, 1)
	got, err = DecodeStatus(json.RawMessage(force))
	if err != nil {
		t.Fatal(err)
	}
	if paused, known := got.Paused.Value(); !known || !paused {
		t.Fatal("forced pause not represented")
	}
	for _, input := range []string{`{"success":true,"status":"unknown","skipped":[]}`, strings.Replace(statusJSON, `"paused":true`, `"paused":"true"`, 1), strings.Replace(statusJSON, `"timeSpeed":"Paused"`, `"timeSpeed":"Fastest"`, 1), strings.Replace(statusJSON, `"skipped":[]`, `"skipped":false`, 1)} {
		if _, err := DecodeStatus(json.RawMessage(input)); !errors.Is(err, ErrContract) {
			t.Fatalf("accepted malformed status %s: %v", input, err)
		}
	}
}

type source struct {
	identities []string
	status     string
	err        error
	calls      int
}

func (s *source) Identity(context.Context) (bridge.Result, error) {
	s.calls++
	if s.err != nil {
		return bridge.Result{}, s.err
	}
	if len(s.identities) == 0 {
		return bridge.Result{}, errors.New("unexpected identity call")
	}
	raw := s.identities[0]
	s.identities = s.identities[1:]
	return bridge.Result{Structured: json.RawMessage(raw), Envelope: json.RawMessage(raw)}, nil
}
func (s *source) Status(context.Context) (bridge.Result, error) {
	s.calls++
	return bridge.Result{Structured: json.RawMessage(s.status), Envelope: json.RawMessage(s.status)}, s.err
}

func TestBracketedSnapshotAndFreshness(t *testing.T) {
	now := time.Date(2026, 9, 10, 12, 0, 0, 0, time.UTC)
	clock := testkit.NewManualClock(now)
	reading, err := Observe(context.Background(), &source{identities: []string{identityJSON, identityJSON}, status: statusJSON}, clock)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := reading.Snapshot
	if !snapshot.SameTick() || snapshot.After.Tick != 0 || len(reading.Receipts[1].Envelope) == 0 {
		t.Fatal("bracket or receipt lost")
	}
	if err = snapshot.CheckFresh(now, time.Second, snapshot.After); err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		now     time.Time
		age     time.Duration
		current Identity
	}{
		{now.Add(-time.Second), time.Second, snapshot.After},
		{now.Add(2 * time.Second), time.Second, snapshot.After},
		{now, -1, snapshot.After},
	} {
		if err = snapshot.CheckFresh(test.now, test.age, test.current); !errors.Is(err, ErrStale) {
			t.Fatalf("accepted stale snapshot: %v", err)
		}
	}
	for _, change := range []func(*Identity){func(i *Identity) { i.Load = "other" }, func(i *Identity) { i.Map = 1 }, func(i *Identity) { i.Colony = "other" }} {
		current := snapshot.After
		change(&current)
		if err = snapshot.CheckFresh(now, time.Second, current); !errors.Is(err, ErrChanged) {
			t.Fatal("missed context change", err)
		}
	}
	future := snapshot
	future.ObservedAt = now.Add(time.Second)
	if err = future.CheckFresh(now, time.Second, future.After); !errors.Is(err, ErrStale) {
		t.Fatal("accepted future observation")
	}
	advanced := strings.Replace(identityJSON, `"tick":0`, `"tick":2`, 1)
	reading, err = Observe(context.Background(), &source{identities: []string{identityJSON, advanced}, status: statusJSON}, clock)
	if err != nil || reading.Snapshot.SameTick() {
		t.Fatal("tick interval misrepresented", err)
	}
	old := reading.Snapshot.After
	old.Tick = domain.Tick(1)
	if err = reading.Snapshot.CheckFresh(now, time.Second, old); !errors.Is(err, ErrChanged) {
		t.Fatal("missed tick rewind")
	}
}

func TestObservationFailsWithoutInventingFacts(t *testing.T) {
	clock := testkit.NewManualClock(time.Now())
	for _, after := range []string{strings.Replace(identityJSON, `"loadToken":"load"`, `"loadToken":"new-load"`, 1), strings.Replace(identityJSON, `"mapId":0`, `"mapId":1`, 1)} {
		if _, err := Observe(context.Background(), &source{identities: []string{identityJSON, after}, status: statusJSON}, clock); !errors.Is(err, ErrChanged) {
			t.Fatal("context churn accepted", err)
		}
	}
	nativeFailure := errors.New("native transport lost")
	result, err := Observe(context.Background(), &source{err: nativeFailure}, clock)
	if !errors.Is(err, nativeFailure) {
		t.Fatal("failure erased", err)
	}
	if _, known := result.Snapshot.Status.Paused.Value(); known {
		t.Fatal("failed read produced known pause")
	}
}

func TestMain(m *testing.M) {
	if len(os.Args) > 2 && os.Args[1] == "server" && os.Args[2] == "stdio" {
		server := mcp.NewServer(&mcp.Implementation{Name: "observation-fixture", Version: "1"}, nil)
		for _, name := range []string{"games_tool_detail", "games_call_tool"} {
			server.AddTool(&mcp.Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)}, func(_ context.Context, request *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
				raw := `{"inputSchema":{"type":"object","properties":{}}}`
				if request.Params.Name == "games_call_tool" {
					var args struct {
						Tool string `json:"tool"`
					}
					if err := json.Unmarshal(request.Params.Arguments, &args); err != nil {
						return nil, err
					}
					switch args.Tool {
					case "home/colony_identity":
						raw = identityJSON
					case "home/status":
						raw = statusJSON
					default:
						return nil, errors.New("unreviewed native call")
					}
				}
				return &mcp.CallToolResult{StructuredContent: json.RawMessage(raw)}, nil
			})
		}
		if err := server.Run(context.Background(), &mcp.StdioTransport{}); err != nil {
			os.Exit(2)
		}
		os.Exit(0)
	}
	os.Exit(m.Run())
}
func TestObserveThroughRealSDKSubprocess(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	client, err := bridge.Open(context.Background(), bridge.ProcessConfig{Executable: executable, ConfigDir: t.TempDir(), GameID: "fixture", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	reading, err := Observe(context.Background(), client, testkit.NewManualClock(time.Now()))
	if err != nil {
		t.Fatal(err)
	}
	if reading.Snapshot.After.Map != 0 || reading.Snapshot.After.Tick != 0 {
		t.Fatal("wrong source tick/map")
	}
	if known, ok := reading.Snapshot.Status.Paused.Value(); !ok || !known {
		t.Fatal("SDK observation lost pause")
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
}

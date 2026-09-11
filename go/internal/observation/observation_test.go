package observation

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func contextFixture(tick int64) *c.ObservationContext {
	return &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(18446744073709551615)}
}
func identityFixture(tick int64) *l.IdentityReply {
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: contextFixture(tick), Paused: proto.Bool(false)}}}
}
func statusFixture(tick int64) *o.StatusReply {
	return &o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: &o.StatusSnapshot{Context: contextFixture(tick)}}}
}

type source struct {
	ids    []*l.IdentityReply
	status *o.StatusReply
	index  int
	err    error
}

func (s *source) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	if err := ctx.Err(); err != nil {
		return nil, bridge.Result{}, err
	}
	if s.err != nil {
		return nil, bridge.Result{Envelope: json.RawMessage(`{"error":true}`)}, s.err
	}
	v := s.ids[s.index]
	s.index++
	return v, bridge.Result{Envelope: json.RawMessage(`{"identity":true}`)}, nil
}
func (s *source) Status(ctx context.Context, identity *c.Identity) (*o.StatusReply, bridge.Result, error) {
	if identity.GetMapId() != 0 {
		return nil, bridge.Result{}, errors.New("wrong identity")
	}
	return s.status, bridge.Result{Envelope: json.RawMessage(`{"status":true}`)}, ctx.Err()
}
func TestIdentityPresenceAndFalsePause(t *testing.T) {
	value, err := DecodeIdentity(identityFixture(0))
	if err != nil || value.Map != 0 || value.Tick != 0 {
		t.Fatalf("zero identity: %+v %v", value, err)
	}
	if paused, known := value.Paused.Value(); !known || paused {
		t.Fatal("present false lost")
	}
	if generation, known := value.NativeGeneration.Value(); !known || generation != domain.NativeGeneration(^uint64(0)) {
		t.Fatal("generation precision lost")
	}
	for _, change := range []func(*l.IdentityReply){func(r *l.IdentityReply) { r.GetLoaded().Context.Identity.MapId = nil }, func(r *l.IdentityReply) { r.GetLoaded().Context.Tick = nil }, func(r *l.IdentityReply) { r.GetLoaded().Context.Tick = proto.Int64(-1) }, func(r *l.IdentityReply) { r.GetLoaded().Context.NativeGeneration = proto.Uint64(0) }, func(r *l.IdentityReply) { r.GetLoaded().Context.Identity.ColonyId = proto.String("a\x00b") }} {
		r := identityFixture(0)
		change(r)
		if _, err := DecodeIdentity(r); err == nil {
			t.Fatal("invalid identity accepted")
		}
	}
	r := identityFixture(0)
	r.GetLoaded().Paused = nil
	r.GetLoaded().Context.NativeGeneration = nil
	value, err = DecodeIdentity(r)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := value.Paused.Value(); known {
		t.Fatal("missing pause became known")
	}
	if _, known := value.NativeGeneration.Value(); known {
		t.Fatal("generation invented")
	}
}
func TestBracketedSnapshotAndFreshness(t *testing.T) {
	now := time.Now()
	s := &source{ids: []*l.IdentityReply{identityFixture(10), identityFixture(12)}, status: statusFixture(11)}
	reading, err := Observe(context.Background(), s, testkit.NewManualClock(now))
	if err != nil {
		t.Fatal(err)
	}
	snapshot := reading.Snapshot
	if err = snapshot.CheckFresh(now, time.Second, snapshot.After); err != nil {
		t.Fatal(err)
	}
	if paused, known := snapshot.Status.Paused.Value(); !known || paused {
		t.Fatal("pause fact lost")
	}
	if _, known := snapshot.Status.Speed.Value(); known {
		t.Fatal("speed invented")
	}
	if _, known := snapshot.Status.ForcePaused.Value(); known {
		t.Fatal("force pause invented")
	}
	for _, change := range []func(*Snapshot){func(s *Snapshot) { s.ObservedAt = now.Add(time.Second) }, func(s *Snapshot) { s.StartedAt = now.Add(-time.Hour) }, func(s *Snapshot) { s.After.Load = "other" }, func(s *Snapshot) { s.After.Tick = 9 }} {
		bad := snapshot
		change(&bad)
		if err = bad.CheckFresh(now, time.Second, snapshot.After); err == nil {
			t.Fatal("stale snapshot accepted")
		}
	}
	if err = snapshot.CheckFresh(now, -1, snapshot.After); !errors.Is(err, ErrStale) {
		t.Fatal(err)
	}
	for _, tick := range []int64{9, 13} {
		s := &source{ids: []*l.IdentityReply{identityFixture(10), identityFixture(12)}, status: statusFixture(tick)}
		if _, err := Observe(context.Background(), s, testkit.NewManualClock(now)); !errors.Is(err, ErrChanged) {
			t.Fatalf("status outside bracket: %v", err)
		}
	}
}
func TestObservationUnavailableAndFailureRetainReceipt(t *testing.T) {
	s := &source{err: bridge.ErrUnavailable}
	reading, err := Observe(context.Background(), s, testkit.NewManualClock(time.Now()))
	if !errors.Is(err, bridge.ErrUnavailable) || len(reading.Receipts[0].Envelope) == 0 {
		t.Fatal("lost failure receipt")
	}
	if _, err := DecodeStatus(&o.StatusReply{Outcome: &o.StatusReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatal(err)
	}
}
func TestMain(m *testing.M) {
	if len(os.Args) > 2 && os.Args[1] == "server" && os.Args[2] == "stdio" {
		server := mcp.NewServer(&mcp.Implementation{Name: "observation-fixture", Version: "1"}, nil)
		for _, name := range []string{"games_tool_detail", "games_call_tool"} {
			server.AddTool(&mcp.Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)}, func(_ context.Context, request *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
				raw := json.RawMessage(`{"inputSchema":{"type":"object","properties":{"request":{"type":"object"}},"additionalProperties":false}}`)
				if request.Params.Name == "games_call_tool" {
					var args struct {
						Tool string `json:"tool"`
					}
					if err := json.Unmarshal(request.Params.Arguments, &args); err != nil {
						return nil, err
					}
					var message proto.Message
					switch args.Tool {
					case "rimgovernor/lifecycle_read_identity":
						r := identityFixture(0)
						r.GetLoaded().Paused = proto.Bool(true)
						message = r
					case "rimgovernor/observations_read_status":
						message = statusFixture(0)
					default:
						return nil, errors.New("unreviewed tool")
					}
					payload, err := protojson.Marshal(message)
					if err != nil {
						return nil, err
					}
					raw, err = json.Marshal(struct {
						Payload string `json:"payload"`
					}{string(payload)})
					if err != nil {
						return nil, err
					}
				}
				return &mcp.CallToolResult{StructuredContent: raw}, nil
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
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	client, err := bridge.Open(context.Background(), bridge.ProcessConfig{Executable: exe, ConfigDir: t.TempDir(), GameID: "fixture", Timeout: 2 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	reading, err := Observe(context.Background(), client, testkit.NewManualClock(time.Now()))
	if err != nil {
		t.Fatal(err)
	}
	if paused, known := reading.Snapshot.Status.Paused.Value(); !known || !paused {
		t.Fatal("SDK pause lost")
	}
}

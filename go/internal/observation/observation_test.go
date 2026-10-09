package observation

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

func contextFixture(tick int64) *c.ObservationContext {
	return &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(18446744073709551615)}
}
func identityFixture(tick int64) *l.IdentityReply {
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: contextFixture(tick), Paused: proto.Bool(false)}}}
}
func tickFixture(tick int64) *l.TickReply {
	return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: contextFixture(tick), Paused: proto.Bool(false)}}}
}

type source struct {
	ticks []*l.TickReply
	index int
	err   error
}

func (s *source) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	if err := ctx.Err(); err != nil {
		return nil, bridge.Result{}, err
	}
	if s.err != nil {
		return nil, bridge.Result{Envelope: json.RawMessage(`{"error":true}`)}, s.err
	}
	v := s.ticks[s.index]
	s.index++
	return v, bridge.Result{Envelope: json.RawMessage(`{"tick":true}`)}, nil
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
func TestTickSnapshotAndFreshness(t *testing.T) {
	now := time.Now()
	s := &source{ticks: []*l.TickReply{tickFixture(10)}}
	reading, err := Observe(context.Background(), s, testkit.NewManualClock(now))
	if err != nil {
		t.Fatal(err)
	}
	snapshot := reading.Snapshot
	if s.index != 1 || !snapshot.SameTick() || snapshot.Before.Tick != 10 || snapshot.After != snapshot.Before || snapshot.Status.Availability != GameLoaded {
		t.Fatalf("one tick read describes the snapshot: %+v", snapshot)
	}
	if err = snapshot.CheckFresh(now, time.Second, snapshot.After); err != nil {
		t.Fatal(err)
	}
	if paused, known := snapshot.Status.Paused.Value(); !known || paused {
		t.Fatal("pause fact lost")
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
	unavailable := &source{ticks: []*l.TickReply{{Outcome: &l.TickReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}}}
	if _, err := Observe(context.Background(), unavailable, testkit.NewManualClock(now)); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatalf("unavailable tick: %v", err)
	}
	invalid := &source{ticks: []*l.TickReply{tickFixture(10)}}
	invalid.ticks[0].GetLoaded().Context.NativeGeneration = proto.Uint64(0)
	if _, err := Observe(context.Background(), invalid, testkit.NewManualClock(now)); !errors.Is(err, ErrContract) {
		t.Fatalf("invalid tick context: %v", err)
	}
}
func TestObservationUnavailableAndFailureRetainReceipt(t *testing.T) {
	s := &source{err: bridge.ErrUnavailable}
	reading, err := Observe(context.Background(), s, testkit.NewManualClock(time.Now()))
	if !errors.Is(err, bridge.ErrUnavailable) || len(reading.Receipt.Envelope) == 0 {
		t.Fatal("lost failure receipt")
	}
}
func TestMain(m *testing.M) {
	if testkit.FakeGameMain(os.Args) {
		return
	}
	os.Exit(m.Run())
}

// answerTick serves the game side of the tick read in the binary reply form
// the bridge asks for.
func answerTick(r *gabptest.Request) {
	var call struct {
		Name string `json:"name"`
	}
	if r.Method != gabp.MethodToolsCall || json.Unmarshal(r.Params, &call) != nil || call.Name != "rimgovernor/lifecycle_read_tick" {
		r.Reply(nil, &gabp.RemoteError{Code: -32601, Message: "unreviewed call"})
		return
	}
	reply := tickFixture(0)
	reply.GetLoaded().Paused = proto.Bool(true)
	payload, err := proto.Marshal(reply)
	if err != nil {
		r.Reply(nil, &gabp.RemoteError{Code: -32603, Message: err.Error()})
		return
	}
	var packed bytes.Buffer
	zip := gzip.NewWriter(&packed)
	_, _ = zip.Write(payload)
	_ = zip.Close()
	r.Reply(map[string]string{"proto": base64.StdEncoding.EncodeToString(packed.Bytes())}, nil)
}

func TestObserveThroughRealGameSession(t *testing.T) {
	server := gabptest.Start(t, &gabptest.Server{
		Token: "fixture-token",
		Tools: []map[string]any{
			{"name": "rimgovernor/lifecycle_read_tick", "inputSchema": json.RawMessage(`{"type":"object","properties":{"request":{"type":"object"}},"additionalProperties":false}`)},
			{"name": "rimgovernor/load_game_ready"},
		},
		Handle: answerTick,
	})
	spec := testkit.StartFakeGame(t, t.TempDir(), "fixture", server)
	client, err := bridge.Open(context.Background(), bridge.ProcessConfig{GameID: "fixture", Launch: spec, Timeout: 10 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	started, err := client.GamesStart(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.ConnectWithPoll(context.Background(), started); err != nil {
		t.Fatal(err)
	}
	reading, err := Observe(context.Background(), client, testkit.NewManualClock(time.Now()))
	if err != nil {
		t.Fatal(err)
	}
	if paused, known := reading.Snapshot.Status.Paused.Value(); !known || !paused {
		t.Fatal("SDK pause lost")
	}
}

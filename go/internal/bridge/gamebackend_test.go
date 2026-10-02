package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
	"github.com/davidarcher/RimGovernor/go/internal/gamehost"
)

// fakeGame is a parked child process launched through gamehost (the test
// binary's bridge-fake-game role) whose recorded endpoint is pointed at an
// in-process GABP server, so the session drives the real launch record,
// liveness and stop paths against a scripted game.
type fakeGame struct {
	spec   gamehost.Spec
	server *gabptest.Server

	mu        sync.Mutex
	attention map[string]any
}

func gameSpec(t *testing.T) gamehost.Spec {
	exe, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	return gamehost.Spec{GameID: "fixture", Executable: exe, Args: []string{"bridge-fake-game"}, StateDir: t.TempDir()}
}

func startFakeGame(t *testing.T) *fakeGame {
	t.Helper()
	g := &fakeGame{spec: gameSpec(t)}
	game, err := gamehost.Launch(context.Background(), g.spec)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = game.Stop(context.Background()) })
	g.server = gabptest.Start(t, &gabptest.Server{
		Token: "fixture-token",
		Tools: []map[string]any{{"name": "fixture/read", "inputSchema": json.RawMessage(emptySchema)}, {"name": "rimworld/load_game_ready"}},
		Welcome: map[string]any{"capabilities": map[string]any{
			"methods": []string{gabp.MethodToolsList, gabp.MethodToolsCall, gabp.MethodEventsSubscribe, attentionCurrent, attentionAck},
			"events":  []string{attentionOpened, attentionUpdated, attentionCleared},
		}},
		Handle: g.handle,
	})
	_, port, _ := net.SplitHostPort(g.server.Addr())
	path := filepath.Join(g.spec.StateDir, "fixture", "endpoint.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var endpoint gamehost.Endpoint
	if err := json.Unmarshal(data, &endpoint); err != nil {
		t.Fatal(err)
	}
	endpoint.Port, _ = strconv.Atoi(port)
	endpoint.Token = "fixture-token"
	if err := os.WriteFile(path, encode(endpoint), 0o644); err != nil {
		t.Fatal(err)
	}
	return g
}

func (g *fakeGame) handle(r *gabptest.Request) {
	switch r.Method {
	case gabp.MethodEventsSubscribe:
		r.Reply(map[string]any{}, nil)
	case attentionCurrent:
		g.mu.Lock()
		defer g.mu.Unlock()
		r.Reply(map[string]any{"attention": g.attention}, nil)
	case attentionAck:
		g.mu.Lock()
		g.attention = nil
		g.mu.Unlock()
		var p struct {
			AttentionID string `json:"attentionId"`
		}
		_ = json.Unmarshal(r.Params, &p)
		r.Reply(map[string]any{"acknowledged": true, "attentionId": p.AttentionID, "currentAttention": nil}, nil)
	case gabp.MethodToolsCall:
		var p struct {
			Name      string          `json:"name"`
			Arguments json.RawMessage `json:"parameters"`
		}
		_ = json.Unmarshal(r.Params, &p)
		if p.Name == "fixture/fail" {
			r.Reply(nil, &gabp.RemoteError{Code: -32000, Message: "fixture refused"})
			return
		}
		r.Reply(map[string]any{"echo": p.Arguments, "tick": json.RawMessage("9007199254740993")}, nil)
	default:
		r.Reply(nil, &gabp.RemoteError{Code: -32601, Message: "method not found"})
	}
}

// open raises attention on the game: the item is current and pushed.
func (g *fakeGame) open(item map[string]any) {
	g.mu.Lock()
	g.attention = item
	g.mu.Unlock()
	for _, conn := range g.server.Conns() {
		conn.Send(gabp.Message{V: gabp.Version, Type: gabp.TypeEvent, Channel: attentionOpened, Seq: 1, Payload: encode(item)})
	}
}

func openGameClient(t *testing.T, spec gamehost.Spec) *Client {
	t.Helper()
	client, err := Open(context.Background(), ProcessConfig{GameID: "fixture", Launch: spec, Timeout: 20 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	return client
}

func statusOf(t *testing.T, client *Client) string {
	t.Helper()
	result, err := client.GameStatus(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	var state struct {
		Status string `json:"status"`
	}
	if err := json.Unmarshal(result.Structured, &state); err != nil {
		t.Fatal(err)
	}
	return state.Status
}

func TestGameSessionCallsAttentionAndReattach(t *testing.T) {
	game := startFakeGame(t)
	client := openGameClient(t, game.spec)
	started, err := client.GamesStart(context.Background())
	if err != nil || !strings.Contains(string(started.Structured), `"attached":true`) {
		t.Fatalf("start attaches to the recorded game: %s %v", started.Structured, err)
	}
	if _, err := client.ConnectWithPoll(context.Background(), started); err != nil {
		t.Fatal(err)
	}
	if got := statusOf(t, client); got != "connected" {
		t.Fatalf("status %q", got)
	}

	read, err := client.NativeCall(context.Background(), "fixture/read", json.RawMessage(`{"name":"Coalition of Ñoa"}`))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(read.Structured), "9007199254740993") || !strings.Contains(string(read.Structured), "Coalition of") {
		t.Fatalf("native result: %s", read.Structured)
	}
	_, err = client.NativeCall(context.Background(), "fixture/fail", nil)
	var refusal *Refusal
	if !errors.As(err, &refusal) || refusal.Cause != "fixture refused" {
		t.Fatalf("native error reply: %v", err)
	}
	detail, err := client.Describe(context.Background(), "fixture/read")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(detail.Structured), `"inputSchema"`) {
		t.Fatalf("detail: %s", detail.Structured)
	}

	// An open blocking attention item refuses calls unexecuted until it is
	// acknowledged; reading attention stays possible.
	game.open(map[string]any{"attentionId": "att-1", "state": "open", "blocking": true, "summary": "error logged"})
	deadline := time.Now().Add(10 * time.Second)
	for {
		_, err = client.NativeCall(context.Background(), "fixture/read", nil)
		if errors.As(err, &refusal) {
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("attention never blocked: %v", err)
		}
		time.Sleep(10 * time.Millisecond)
	}
	var blocked struct {
		Status string `json:"status"`
	}
	if json.Unmarshal(refusal.Result.Structured, &blocked); blocked.Status != "blocked_by_attention" || !strings.Contains(strings.Join(refusal.Result.Text, ""), "error logged") {
		t.Fatalf("blocked refusal: %s %v", refusal.Result.Envelope, refusal.Result.Text)
	}
	attention, err := client.GetAttention(context.Background())
	if err != nil || !strings.Contains(string(attention.Structured), "att-1") {
		t.Fatalf("get attention: %s %v", attention.Structured, err)
	}
	if _, err := client.AckAttention(context.Background(), "att-1"); err != nil {
		t.Fatal(err)
	}
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); err != nil {
		t.Fatalf("call after acknowledgement: %v", err)
	}

	// A pawn job loop is a benign vanilla error: the next call acknowledges
	// it and runs instead of freezing the colony.
	game.open(map[string]any{"attentionId": "att-2", "state": "open", "blocking": true, "summary": "RimWorld logged a error message: Komodo started 10 jobs in one tick. newJob=HaulToCell"})
	deadline = time.Now().Add(10 * time.Second)
	for {
		game.mu.Lock()
		acked := game.attention == nil
		game.mu.Unlock()
		_, err = client.NativeCall(context.Background(), "fixture/read", nil)
		if err != nil {
			t.Fatalf("benign attention blocked the call: %v", err)
		}
		if acked {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("benign attention never acknowledged")
		}
		time.Sleep(10 * time.Millisecond)
	}

	// The game connection dropping on its own ends the session; Reattach
	// finds the same game and dials it again. The server side drops first,
	// then the client side through DropConnection.
	for _, conn := range game.server.Conns() {
		conn.Close()
	}
	select {
	case <-client.Disconnected():
	case <-time.After(10 * time.Second):
		t.Fatal("dropped connection not observed")
	}
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); !errors.Is(err, ErrDisconnected) {
		t.Fatalf("call on a lost session: %v", err)
	}
	if err := client.Reattach(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); err != nil {
		t.Fatal(err)
	}
	if !client.DropConnection() {
		t.Fatal("DropConnection found no connection")
	}
	select {
	case <-client.Disconnected():
	case <-time.After(10 * time.Second):
		t.Fatal("DropConnection not observed")
	}
	if err := client.Reattach(context.Background()); err != nil {
		t.Fatal(err)
	}

	// games_stop ends the process and keeps the session usable.
	if _, err := client.GamesStop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := statusOf(t, client); got != "stopped" {
		t.Fatalf("status after stop %q", got)
	}
	select {
	case <-client.Disconnected():
		t.Fatal("games_stop ended the session")
	default:
	}
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); !errors.Is(err, ErrRefused) {
		t.Fatalf("call on a stopped game: %v", err)
	}
}

func TestGameSessionLaunchesAndStops(t *testing.T) {
	spec := gameSpec(t)
	client := openGameClient(t, spec)
	if got := statusOf(t, client); got != "stopped" {
		t.Fatalf("status before start %q", got)
	}
	started, err := client.GamesStart(context.Background())
	if err != nil || !strings.Contains(string(started.Structured), `"launched":true`) {
		t.Fatalf("start: %s %v", started.Structured, err)
	}
	t.Cleanup(func() { _, _ = client.GamesStop(context.Background()) })
	if got := statusOf(t, client); got != "running" {
		t.Fatalf("status after launch %q", got)
	}
	// A second session under the same state dir attaches to the same game.
	peer := openGameClient(t, spec)
	again, err := peer.GamesStart(context.Background())
	if err != nil || !strings.Contains(string(again.Structured), `"attached":true`) {
		t.Fatalf("peer start: %s %v", again.Structured, err)
	}
	if _, err := client.GamesStop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := statusOf(t, peer); got != "stopped" {
		t.Fatalf("peer status after stop %q", got)
	}
	if _, err := Open(context.Background(), ProcessConfig{GameID: "fixture", Launch: gamehost.Spec{StateDir: "relative"}}); !errors.Is(err, ErrContract) {
		t.Fatalf("relative state dir: %v", err)
	}
}

func TestConnectAcknowledgesAttentionOpenBeforeAttach(t *testing.T) {
	game := startFakeGame(t)
	game.open(map[string]any{"attentionId": "att-load", "state": "open", "blocking": true, "summary": "save load error"})
	client := openGameClient(t, game.spec)
	started, err := client.GamesStart(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	connected, err := client.ConnectWithPoll(context.Background(), started)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(connected.Structured), "att-load") {
		t.Fatalf("connect receipt omits the acknowledged item: %s", connected.Structured)
	}
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); err != nil {
		t.Fatalf("call after attach: %v", err)
	}
}

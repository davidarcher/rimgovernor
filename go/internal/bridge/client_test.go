package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

const emptySchema = `{"type":"object","properties":{},"additionalProperties":false}`

type testServer struct {
	mu             sync.Mutex
	calls          []nativeArgument
	handler        func(context.Context, nativeArgument) (*mcp.CallToolResult, error)
	connectResult  *mcp.CallToolResult
	connectHandler func() (*mcp.CallToolResult, error)
	connectArgs    json.RawMessage
	detailResult   *mcp.CallToolResult
	schema         string
	sessions       []*mcp.ServerSession
	starts         int
	details        int
}

func structured(raw string) *mcp.CallToolResult {
	return &mcp.CallToolResult{StructuredContent: json.RawMessage(raw)}
}
func (s *testServer) server() *mcp.Server {
	server := mcp.NewServer(&mcp.Implementation{Name: "gabs-test", Version: "1"}, nil)
	for _, name := range []string{"games_call_tool", "games_tool_detail", "games_tool_names", "games_status", "games_connect", "games_start"} {
		server.AddTool(&mcp.Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)}, func(ctx context.Context, request *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
			switch request.Params.Name {
			case "games_start":
				s.mu.Lock()
				s.starts++
				s.mu.Unlock()
				return structured(`{"gabpConnected":true}`), nil
			case "games_connect":
				s.connectArgs = append(json.RawMessage(nil), request.Params.Arguments...)
				if s.connectHandler != nil {
					return s.connectHandler()
				}
				if s.connectResult != nil {
					return s.connectResult, nil
				}
				return structured(`{"success":true}`), nil
			case "games_tool_detail":
				s.mu.Lock()
				s.details++
				s.mu.Unlock()
				if s.detailResult != nil {
					return s.detailResult, nil
				}
				schema := s.schema
				if schema == "" {
					schema = emptySchema
				}
				return structured(`{"inputSchema":` + schema + `}`), nil
			case "games_call_tool":
				var args nativeArgument
				if err := json.Unmarshal(request.Params.Arguments, &args); err != nil {
					return nil, err
				}
				s.mu.Lock()
				s.calls = append(s.calls, args)
				s.mu.Unlock()
				if s.handler != nil {
					return s.handler(ctx, args)
				}
				return structured(`{"colonyId":"test-colony","tick":0,"operation":{"id":"receipt-1"}}`), nil
			default:
				return structured(`{"success":true}`), nil
			}
		})
	}
	return server
}
func (s *testServer) factory(t *testing.T) transportFactory {
	return func() mcp.Transport {
		serverTransport, clientTransport := mcp.NewInMemoryTransports()
		session, err := s.server().Connect(context.Background(), serverTransport, nil)
		if err != nil {
			t.Fatal(err)
		}
		s.mu.Lock()
		s.sessions = append(s.sessions, session)
		s.mu.Unlock()
		t.Cleanup(func() { _ = session.Close() })
		return clientTransport
	}
}
func testClient(t *testing.T, s *testServer, timeout time.Duration) *Client {
	t.Helper()
	client, err := open(context.Background(), "fixture-game", timeout, nil, nil, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	return client
}

// Tests below exercise MCP transport ownership independently of a domain decoder.
func testNativeRead(client *Client, ctx context.Context) (Result, error) {
	return client.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		return client.core(ctx, live, "games_call_tool", encode(nativeArgument{client.gameID, "fixture/read", json.RawMessage(`{}`)}))
	})
}

// TestCancellationAndClose exercises per-call context cancellation and Close
// against a Client that now allows concurrent in-flight calls (see
// TestConcurrentNativeCallsDoNotCrossTalk). The second call here overlaps the
// first in the handler rather than queuing behind it — that overlap is the
// point of the fix — but it must still fail with its own context's
// DeadlineExceeded, and Close must still cancel whatever remains in flight.
func TestCancellationAndClose(t *testing.T) {
	entered := make(chan struct{}, 2)
	s := &testServer{handler: func(ctx context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
		entered <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	}}
	client := testClient(t, s, time.Second)
	done := make(chan error, 1)
	go func() { _, err := testNativeRead(client, context.Background()); done <- err }()
	<-entered
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, err := testNativeRead(client, ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("second call cancellation: %v", err)
	}
	<-entered // the second call now overlaps the first rather than queuing behind it
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("inflight read succeeded after close")
		}
	case <-time.After(time.Second):
		t.Fatal("close left pending read")
	}
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, ErrClosed) {
		t.Fatalf("read after close: %v", err)
	}
	if err := client.Reconnect(context.Background()); !errors.Is(err, ErrClosed) {
		t.Fatalf("reconnected closed client: %v", err)
	}
	if err := client.Close(); err != nil {
		t.Fatal("close not idempotent", err)
	}
}

func TestExplicitReconnectNeverRetriesRead(t *testing.T) {
	var count atomic.Int32
	s := &testServer{handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		count.Add(1)
		return structured(`{"success":false}`), nil
	}}
	client := testClient(t, s, time.Second)
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
	if count.Load() != 1 {
		t.Fatal("read retried")
	}
	if err := client.Reconnect(context.Background()); err != nil {
		t.Fatal(err)
	}
	if count.Load() != 1 || len(s.sessions) != 2 {
		t.Fatal("reconnect repeated read or reused session")
	}
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
	if count.Load() != 2 {
		t.Fatal("unexpected read count")
	}
}

// TestMain lets the test binary stand in for GABS (testkit.GABSHTTPMain).
func TestMain(m *testing.M) {
	if gabsJobHelperMain(os.Args) {
		os.Exit(0)
	}
	if len(os.Args) > 2 && os.Args[1] == "server" {
		gabsJobFakeChild()
	}
	if testkit.GABSHTTPMain(os.Args, (&testServer{}).server) {
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func TestOwnedSubprocessAndFailedConnections(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	client, err := Open(context.Background(), ProcessConfig{Executable: executable, ConfigDir: t.TempDir(), GameID: "fixture", Timeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	if err = client.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err = Open(context.Background(), ProcessConfig{Executable: filepath.Join(t.TempDir(), "missing.exe"), ConfigDir: t.TempDir(), GameID: "fixture", Timeout: time.Second}); !errors.Is(err, ErrTransport) {
		t.Fatalf("missing executable: %v", err)
	}
	start := time.Now()
	_, err = open(context.Background(), "fixture", 300*time.Millisecond, nil, nil, func() mcp.Transport {
		return &gabsHTTPTransport{executable: executable, configDir: filepath.Join(t.TempDir(), "unresponsive"), logLevel: "error"}
	})
	if !errors.Is(err, ErrTransport) || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("failed handshake: %v", err)
	}
	if time.Since(start) > 3*time.Second {
		t.Fatal("failed initialization did not end promptly")
	}
}

func TestRawReceiptPreservesInt64AndFutureFields(t *testing.T) {
	wire := `{"identity":18446744073709551615,"tick":9223372036854775807,"nested":{"value":9007199254740993}}`
	s := &testServer{handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return structured(wire), nil }}
	client := testClient(t, s, time.Second)
	result, err := testNativeRead(client, context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"18446744073709551615", "9223372036854775807", "9007199254740993"} {
		if !strings.Contains(string(result.Structured), value) || !strings.Contains(string(result.Envelope), value) {
			t.Fatalf("rounded integer %s in %s", value, result.Structured)
		}
	}
	if _, err := decodeReceipt("fixture", nil, structured(wire)); !errors.Is(err, ErrContract) {
		t.Fatal("missing raw receipt fell back to SDK values")
	}
}

func TestLostConnectionAndMissingCapabilities(t *testing.T) {
	s := &testServer{}
	client := testClient(t, s, time.Second)
	lost := client.Disconnected()
	select {
	case <-lost:
		t.Fatal("live session reported disconnected")
	default:
	}
	if err := s.sessions[0].Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case <-lost:
	case <-time.After(5 * time.Second):
		t.Fatal("session loss not observed")
	}
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, ErrDisconnected) {
		t.Fatalf("lost connection: %v", err)
	}
	if err := client.Reconnect(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	select {
	case <-client.Disconnected():
		t.Fatal("fresh session reported disconnected")
	default:
	}
	server := mcp.NewServer(&mcp.Implementation{Name: "missing-capabilities", Version: "1"}, nil)
	serverTransport, clientTransport := mcp.NewInMemoryTransports()
	session, err := server.Connect(context.Background(), serverTransport, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer session.Close()
	_, err = open(context.Background(), "fixture", time.Second, nil, nil, func() mcp.Transport { return clientTransport })
	if !errors.Is(err, ErrContract) {
		t.Fatalf("missing capabilities accepted: %v", err)
	}
	done := make(chan struct{})
	go func() { _ = session.Wait(); close(done) }()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("failed discovery left session open")
	}
}

func TestReadDeadlineAndOversizedWireResult(t *testing.T) {
	s := &testServer{handler: func(ctx context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
		<-ctx.Done()
		return nil, ctx.Err()
	}}
	client := testClient(t, s, 100*time.Millisecond)
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("read deadline: %v", err)
	}
	large := &testServer{handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return structured(`{"payload":"` + strings.Repeat("x", maxResponseBytes) + `"}`), nil
	}}
	// The deadline only guards against a hang; decoding 50 MiB under race
	// detection takes several seconds.
	bigClient := testClient(t, large, 30*time.Second)
	if _, err := testNativeRead(bigClient, context.Background()); !errors.Is(err, ErrContract) {
		t.Fatalf("oversized result: %v", err)
	}
}

type blockedTransport struct{ entered chan struct{} }

func (b blockedTransport) Connect(ctx context.Context) (mcp.Connection, error) {
	close(b.entered)
	<-ctx.Done()
	return nil, ctx.Err()
}

func TestConcurrentReconnectHonorsContextAndClose(t *testing.T) {
	s := &testServer{}
	factory := s.factory(t)
	entered := make(chan struct{})
	count := 0
	client, err := open(context.Background(), "fixture", time.Second, nil, nil, func() mcp.Transport {
		count++
		if count == 1 {
			return factory()
		}
		return blockedTransport{entered: entered}
	})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	done := make(chan error, 1)
	go func() { done <- client.Reconnect(context.Background()) }()
	<-entered
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := client.Reconnect(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("queued reconnect ignored context: %v", err)
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("close did not cancel reconnect: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("close left initialization running")
	}
	if count != 2 {
		t.Fatal("canceled reconnect launched a process")
	}
}

func TestFlightRecorderCapturesRequestResponseAndError(t *testing.T) {
	failing := int32(0)
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if atomic.LoadInt32(&failing) != 0 {
			return &mcp.CallToolResult{IsError: true}, nil
		}
		return structured(`{"colonyId":"test-colony","tick":0,"operation":{"id":"receipt-1"}}`), nil
	}}
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	client, err := open(context.Background(), "fixture-game", time.Second, rec, nil, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	client.SetRecordingContext(func() map[string]any { return map[string]any{"colony": "test-colony"} })
	if _, err = testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	atomic.StoreInt32(&failing, 1)
	if _, err = testNativeRead(client, context.Background()); err == nil {
		t.Fatal("expected refused call to surface an error")
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	var kinds []string
	for _, row := range rows {
		kinds = append(kinds, row.Kind)
	}
	assertContains(t, kinds, "native_request")
	assertContains(t, kinds, "native_response")
	assertContains(t, kinds, "native_error")
	for _, row := range rows {
		if row.Kind == "native_request" && row.Context["colony"] != "test-colony" {
			t.Fatalf("expected recording context on request row, got %+v", row.Context)
		}
	}
}

// TestConcurrentNativeCallsDoNotCrossTalk fires overlapping native calls from
// multiple goroutines and asserts each gets back its own distinct payload.
// This is the regression test for the bridge no longer forcing calls
// single-flight: bridge.Client.operation used to hard-serialize every native
// call via a capacity-1 gate, and receiptConnection tracked exactly one
// pending request/response at a time. Neither GABP (frame-write atomicity
// only) nor GABS's own GABP client (map-keyed pending requests) nor the
// go-sdk transport this Client already depends on (writeMu-guarded writes,
// ID-correlated outgoingCalls) require single-flight; this test exercises
// the two calls actually overlapping in the handler to prove concurrent
// requests are correlated correctly rather than cross-talking.
func TestConcurrentNativeCallsDoNotCrossTalk(t *testing.T) {
	const n = 6
	release := make(chan struct{})
	entered := make(chan struct{}, n)
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		entered <- struct{}{}
		<-release // hold every call open simultaneously to force real overlap
		return structured(`{"echo":` + string(args.Arguments) + `}`), nil
	}}
	client := testClient(t, s, 5*time.Second)

	type outcome struct {
		want string
		got  Result
		err  error
	}
	results := make(chan outcome, n)
	for i := 0; i < n; i++ {
		i := i
		go func() {
			result, err := client.operation(context.Background(), func(ctx context.Context, live *liveSession) (Result, error) {
				return client.core(ctx, live, "games_call_tool", encode(nativeArgument{client.gameID, "fixture/read", json.RawMessage(fmt.Sprintf("%d", i))}))
			})
			results <- outcome{want: fmt.Sprintf(`{"echo":%d}`, i), got: result, err: err}
		}()
	}
	for i := 0; i < n; i++ {
		select {
		case <-entered:
		case <-time.After(5 * time.Second):
			t.Fatalf("only %d of %d calls overlapped in the handler", i, n)
		}
	}
	close(release)
	seen := map[string]bool{}
	for i := 0; i < n; i++ {
		select {
		case o := <-results:
			if o.err != nil {
				t.Fatalf("call failed: %v", o.err)
			}
			if string(o.got.Structured) != o.want {
				t.Fatalf("cross-talk: got %s, want %s", o.got.Structured, o.want)
			}
			seen[o.want] = true
		case <-time.After(5 * time.Second):
			t.Fatal("timed out waiting for results")
		}
	}
	if len(seen) != n {
		t.Fatalf("expected %d distinct results, got %d", n, len(seen))
	}
}

func assertContains(t *testing.T, values []string, want string) {
	t.Helper()
	for _, v := range values {
		if v == want {
			return
		}
	}
	t.Fatalf("expected %q among %v", want, values)
}

// TestReattachAfterLostSession is the #87 recovery: once GABS is gone the
// client is disconnected, Reattach dials a fresh process and re-runs the
// start/connect handshake against the still-running game, and a client
// closed meanwhile refuses to reattach.
func TestReattachAfterLostSession(t *testing.T) {
	s := &testServer{}
	client := testClient(t, s, time.Second)
	if err := s.sessions[0].Close(); err != nil {
		t.Fatal(err)
	}
	<-client.Disconnected()
	if err := client.Reattach(context.Background()); err != nil {
		t.Fatal(err)
	}
	s.mu.Lock()
	starts, sessions := s.starts, len(s.sessions)
	s.mu.Unlock()
	if starts != 1 || sessions != 2 {
		t.Fatalf("reattach: %d starts, %d sessions", starts, sessions)
	}
	if _, err := testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}
	<-client.Disconnected()
	if err := client.Reattach(context.Background()); !errors.Is(err, ErrClosed) {
		t.Fatalf("reattach after close: %v", err)
	}
}

// TestRuntimePublishRaceRetried covers #164: GABS refusing a call because it
// could not rename .runtime-*.tmp over runtime.json is transient and never
// reached the game, so core re-issues the call; any other refusal is not.
func TestRuntimePublishRaceRetried(t *testing.T) {
	const race = `Failed to claim runtime ownership for 'rimgovernor-trial': failed to publish runtime state: rename C:\u\.rimgovernor\bridge\config-headless\rimgovernor-trial\.runtime-372168833.tmp C:\u\.rimgovernor\bridge\config-headless\rimgovernor-trial\runtime.json: Access is denied.`
	var refusals int32
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if atomic.AddInt32(&refusals, 1) == 1 {
			return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: race}}}, nil
		}
		return structured(`{"colonyId":"test-colony","tick":0,"operation":{"id":"receipt-1"}}`), nil
	}}
	client := testClient(t, s, 5*time.Second)
	if _, err := testNativeRead(client, context.Background()); err != nil {
		t.Fatalf("expected the retried call to succeed, got %v", err)
	}
	if got := atomic.LoadInt32(&refusals); got != 2 {
		t.Fatalf("expected exactly one retry, handler saw %d calls", got)
	}

	other := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: "Failed to claim runtime ownership for 'x': a launch claim for x was published"}}}, nil
	}}
	otherClient := testClient(t, other, 5*time.Second)
	if _, err := testNativeRead(otherClient, context.Background()); !errors.Is(err, ErrRefused) {
		t.Fatalf("expected a non-race refusal to surface, got %v", err)
	}
	if len(other.calls) != 1 {
		t.Fatalf("expected no retry for a non-race refusal, got %d calls", len(other.calls))
	}
}

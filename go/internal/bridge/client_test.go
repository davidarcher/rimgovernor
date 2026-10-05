package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// testBudget is the per-call deadline for tests that never expect it to
// expire: a hang guard, not a latency assertion. Decoding a multi-megabyte
// payload through the in-memory transport under race detection on a loaded
// CI runner has run past 1s and past 30s; a test that asserts an expiry
// passes its own, shorter deadline.
const testBudget = time.Minute

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
	return client.operation(ctx, AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
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
	s := &testServer{handler: func(ctx context.Context, _ nativeArgument) (*callResult, error) {
		entered <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	}}
	client := testClient(t, s, testBudget)
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
	s := &testServer{handler: func(context.Context, nativeArgument) (*callResult, error) {
		count.Add(1)
		return structured(`{"success":false}`), nil
	}}
	client := testClient(t, s, testBudget)
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

// TestMain lets the test binary stand in for a game process
// (bridge-fake-game parks until killed; see gamebackend_test.go).
func TestMain(m *testing.M) {
	if len(os.Args) > 1 && os.Args[1] == "bridge-fake-game" {
		for {
			time.Sleep(time.Hour)
		}
	}
	os.Exit(m.Run())
}

func TestRawReceiptPreservesInt64AndFutureFields(t *testing.T) {
	wire := `{"identity":18446744073709551615,"tick":9223372036854775807,"nested":{"value":9007199254740993}}`
	s := &testServer{handler: func(context.Context, nativeArgument) (*callResult, error) { return structured(wire), nil }}
	client := testClient(t, s, testBudget)
	result, err := testNativeRead(client, context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, value := range []string{"18446744073709551615", "9223372036854775807", "9007199254740993"} {
		if !strings.Contains(string(result.Structured), value) || !strings.Contains(string(result.Envelope), value) {
			t.Fatalf("rounded integer %s in %s", value, result.Structured)
		}
	}
	if _, err := decodeReceipt("fixture", nil); !errors.Is(err, ErrContract) {
		t.Fatal("missing raw receipt fell back to SDK values")
	}
}

func TestLostConnectionAndMissingCapabilities(t *testing.T) {
	s := &testServer{}
	client := testClient(t, s, testBudget)
	lost := client.Disconnected()
	select {
	case <-lost:
		t.Fatal("live session reported disconnected")
	default:
	}
	if err := s.sessions[0].close(); err != nil {
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
	missing := newHandlerBackend(Discovery{ServerName: "missing-capabilities"}, nil)
	_, err := open(context.Background(), "fixture", time.Second, nil, nil, func(context.Context) (backend, error) { return missing, nil })
	if !errors.Is(err, ErrContract) {
		t.Fatalf("missing capabilities accepted: %v", err)
	}
	select {
	case <-missing.done():
	case <-time.After(time.Second):
		t.Fatal("failed discovery left session open")
	}
}

func TestReadDeadlineAndOversizedWireResult(t *testing.T) {
	s := &testServer{handler: func(ctx context.Context, _ nativeArgument) (*callResult, error) {
		<-ctx.Done()
		return nil, ctx.Err()
	}}
	client := testClient(t, s, 100*time.Millisecond)
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("read deadline: %v", err)
	}
	large := &testServer{handler: func(context.Context, nativeArgument) (*callResult, error) {
		return structured(`{"payload":"` + strings.Repeat("x", maxResponseBytes) + `"}`), nil
	}}
	// The deadline only guards against a hang; decoding 50 MiB under race
	// detection takes several seconds idle and ran past 30s on a loaded CI
	// runner.
	bigClient := testClient(t, large, 2*testBudget)
	if _, err := testNativeRead(bigClient, context.Background()); !errors.Is(err, ErrContract) {
		t.Fatalf("oversized result: %v", err)
	}
}

func TestConcurrentReconnectHonorsContextAndClose(t *testing.T) {
	s := &testServer{}
	factory := s.factory(t)
	entered := make(chan struct{})
	count := 0
	client, err := open(context.Background(), "fixture", time.Second, nil, nil, func(ctx context.Context) (backend, error) {
		count++
		if count == 1 {
			return factory(ctx)
		}
		close(entered)
		<-ctx.Done()
		return nil, ctx.Err()
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
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*callResult, error) {
		if atomic.LoadInt32(&failing) != 0 {
			return &callResult{IsError: true}, nil
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
// only) nor the gabp client (ID-correlated pending requests) require single-flight; this test exercises
// the two calls actually overlapping in the handler to prove concurrent
// requests are correlated correctly rather than cross-talking.
func TestConcurrentNativeCallsDoNotCrossTalk(t *testing.T) {
	const n = 6
	release := make(chan struct{})
	entered := make(chan struct{}, n)
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*callResult, error) {
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
			result, err := client.operation(context.Background(), AdmissionControl, func(ctx context.Context, live *liveSession) (Result, error) {
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

// TestIndependentCallAnsweredWhileLongPollHeld is the deterministic mirror of
// the smoke/dispatch acceptance case (#227, #617): with one call established
// as held in the handler, an independent call is issued and must be answered
// before the held one is released. The ordering is decided by synchronization
// (the handler reports entry, the test releases it only after the independent
// result is in hand), not by elapsed time; the deadlines here are hang guards.
// The native case keeps the same claim over the installed GABP host
// path, where the handler cannot be instrumented.
func TestIndependentCallAnsweredWhileLongPollHeld(t *testing.T) {
	entered := make(chan struct{}, 1)
	release := make(chan struct{})
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*callResult, error) {
		if args.Tool == "clock/read_events" {
			entered <- struct{}{}
			<-release
			return structured(`{"page":{}}`), nil
		}
		return structured(`{"identity":{}}`), nil
	}}
	client := testClient(t, s, testBudget)

	poll := make(chan error, 1)
	go func() {
		_, err := client.NativeCall(context.Background(), "clock/read_events", json.RawMessage(`{"waitMs":8000}`))
		poll <- err
	}()
	select {
	case <-entered:
	case <-time.After(10 * time.Second):
		t.Fatal("the held call never reached the handler; the precondition was never established")
	}

	if _, err := client.NativeCall(context.Background(), "lifecycle/read_identity", json.RawMessage(`{}`)); err != nil {
		t.Fatalf("independent read under a held poll: %v", err)
	}
	// The read is answered and the poll is still outstanding: nothing has
	// been sent on poll because nothing has released the handler yet.
	select {
	case err := <-poll:
		t.Fatalf("the held call completed before the independent read: %v", err)
	default:
	}
	close(release)
	select {
	case err := <-poll:
		if err != nil {
			t.Fatalf("held call: %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the released call never returned")
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

// TestReattachAfterLostSession is the #87 recovery: once the game connection is gone the
// client is disconnected, Reattach dials a fresh process and re-runs the
// start/connect handshake against the still-running game, and a client
// closed meanwhile refuses to reattach.
func TestReattachAfterLostSession(t *testing.T) {
	s := &testServer{}
	client := testClient(t, s, testBudget)
	if err := s.sessions[0].close(); err != nil {
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

// A refusal names its own cause. The tool name alone identified only the
// wrapper, so every native refusal read as "bridge read refused:
// games_call_tool" and nine nightly cases looked like transport faults
// (#663).
func TestRefusalNamesItsNativeCause(t *testing.T) {
	for _, tc := range []struct{ structured, want string }{
		{`{"reason":"No open reachable area for the fixture hut.","success":false}`,
			"bridge read refused: games_call_tool: No open reachable area for the fixture hut."},
		{`{"exception":"System.InvalidOperationException: Pause before drain\r\n  at HomeBridge","success":false}`,
			"bridge read refused: games_call_tool: System.InvalidOperationException: Pause before drain"},
		{`{"exception":"System.InvalidOperationException: Pause before drain/r/n  at HomeBridge","success":false}`,
			"bridge read refused: games_call_tool: System.InvalidOperationException: Pause before drain"},
		{`{"error":"stale token","success":false}`, "bridge read refused: games_call_tool: stale token"},
		{`{"success":false}`, "bridge read refused: games_call_tool"},
	} {
		s := &testServer{handler: func(context.Context, nativeArgument) (*callResult, error) {
			return structured(tc.structured), nil
		}}
		ctx, cancel := context.WithTimeout(context.Background(), testBudget)
		_, err := testNativeRead(testClient(t, s, testBudget), ctx)
		cancel()
		if !errors.Is(err, ErrRefused) || err.Error() != tc.want {
			t.Fatalf("refusal of %s = %v, wanted %q", tc.structured, err, tc.want)
		}
	}
	// A whole managed stack trace on one line is bounded, not pasted into
	// every error string that wraps it.
	long := strings.Repeat("x", 4096)
	s := &testServer{handler: func(context.Context, nativeArgument) (*callResult, error) {
		return structured(`{"reason":"` + long + `","success":false}`), nil
	}}
	ctx, cancel := context.WithTimeout(context.Background(), testBudget)
	defer cancel()
	_, err := testNativeRead(testClient(t, s, testBudget), ctx)
	var refusal *Refusal
	if !errors.As(err, &refusal) || len(refusal.Cause) > refusalCauseBytes+3 || !strings.HasSuffix(refusal.Cause, "...") {
		t.Fatalf("unbounded cause (%d bytes): %v", len(refusal.Cause), err)
	}
}

// A caller that asked the native side to wait longer than the session's
// timeout gets the deadline it asked for: food/fishing spent its 60s session
// budget on a native start it had given 120s and reported the cut
// as a transport failure (#663).
func TestWithCallTimeoutCoversALongNativeWait(t *testing.T) {
	released := make(chan struct{})
	s := &testServer{handler: func(ctx context.Context, _ nativeArgument) (*callResult, error) {
		select {
		case <-released:
			return structured(`{"success":true}`), nil
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}}
	client := testClient(t, s, 150*time.Millisecond)
	// Without the raised deadline the session timeout cuts the call.
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, context.DeadlineExceeded) && !errors.Is(err, ErrTransport) {
		t.Fatalf("the session timeout did not cut the call: %v", err)
	}
	done := make(chan error, 1)
	go func() {
		_, err := testNativeRead(client, WithCallTimeout(context.Background(), testBudget))
		done <- err
	}()
	time.Sleep(400 * time.Millisecond)
	close(released)
	if err := <-done; err != nil {
		t.Fatalf("the raised deadline did not cover the wait: %v", err)
	}
}

func TestWithCallTimeoutNeverTightensAReadOrOutrunsTheBound(t *testing.T) {
	c := &Client{timeout: time.Minute}
	if got := c.callTimeout(WithCallTimeout(context.Background(), time.Second)); got != time.Minute {
		t.Fatalf("a shorter call timeout tightened the read: %v", got)
	}
	if got := c.callTimeout(WithCallTimeout(context.Background(), 0)); got != time.Minute {
		t.Fatalf("a zero call timeout changed the read: %v", got)
	}
	if got := c.callTimeout(WithCallTimeout(context.Background(), 10*time.Hour)); got != MaxCallTimeout {
		t.Fatalf("call timeout outran its bound: %v", got)
	}
	if got := c.callTimeout(context.Background()); got != time.Minute {
		t.Fatalf("plain context did not take the session timeout: %v", got)
	}
}

// A hop that throws comes back from the RimBridge host as success:false with
// message/exception and an operation envelope, no proto/slot (see
// docs/developers/contracts/bridge-thrown-hop.md, #1887). A typed read must
// surface that as a named refusal, never as an empty reply (#1888).
func TestThrownHopSurfacesAsNamedRefusalOnTypedRead(t *testing.T) {
	thrown := `{"success":false,"message":"Pawn is gone","exception":"System.InvalidOperationException: Pawn is gone\r\n   at HomeBridge.Read",` +
		`"operation":{"OperationId":"op_1","Status":3,"Success":false,"Result":null,` +
		`"Error":{"Code":"capability.failed","Message":"Pawn is gone","ExceptionType":"System.InvalidOperationException"}}}`
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		return structured(thrown), nil
	}}
	identity, _, err := testClient(t, s, testBudget).Identity(context.Background())
	var refusal *Refusal
	if identity != nil || !errors.As(err, &refusal) || !errors.Is(err, ErrRefused) ||
		refusal.Cause != "System.InvalidOperationException: Pawn is gone" {
		t.Fatalf("thrown hop decoded as %v, %v", identity, err)
	}
}

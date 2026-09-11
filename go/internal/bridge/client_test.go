package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

const emptySchema = `{"type":"object","properties":{},"additionalProperties":false}`

type testServer struct {
	mu            sync.Mutex
	calls         []nativeArgument
	handler       func(context.Context, nativeArgument) (*mcp.CallToolResult, error)
	connectResult *mcp.CallToolResult
	connectArgs   json.RawMessage
	detailResult  *mcp.CallToolResult
	schema        string
	sessions      []*mcp.ServerSession
}

func structured(raw string) *mcp.CallToolResult {
	return &mcp.CallToolResult{StructuredContent: json.RawMessage(raw)}
}
func (s *testServer) server() *mcp.Server {
	server := mcp.NewServer(&mcp.Implementation{Name: "gabs-test", Version: "1"}, nil)
	for _, name := range []string{"games_call_tool", "games_tool_detail", "games_tool_names", "games_status", "games_connect"} {
		server.AddTool(&mcp.Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)}, func(ctx context.Context, request *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
			switch request.Params.Name {
			case "games_connect":
				s.connectArgs = append(json.RawMessage(nil), request.Params.Arguments...)
				if s.connectResult != nil {
					return s.connectResult, nil
				}
				return structured(`{"success":true}`), nil
			case "games_tool_detail":
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
	client, err := open(context.Background(), "fixture-game", timeout, s.factory(t))
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

func TestCancellationQueueAndClose(t *testing.T) {
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
		t.Fatalf("queue cancellation: %v", err)
	}
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
	if len(entered) != 0 {
		t.Fatal("cancelled queued read dispatched")
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

func TestMain(m *testing.M) {
	if len(os.Args) > 4 && os.Args[1] == "server" && os.Args[2] == "stdio" {
		if filepath.Base(os.Args[4]) == "unresponsive" {
			for {
				time.Sleep(time.Second)
			}
		}
		server := (&testServer{}).server()
		if err := server.Run(context.Background(), &mcp.StdioTransport{}); err != nil {
			os.Exit(2)
		}
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func TestOwnedSubprocessAndFailedConnections(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	client, err := Open(context.Background(), ProcessConfig{Executable: executable, ConfigDir: t.TempDir(), GameID: "fixture", Timeout: 2 * time.Second})
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
	var cmd *exec.Cmd
	start := time.Now()
	_, err = open(context.Background(), "fixture", 30*time.Millisecond, func() mcp.Transport {
		cmd = exec.Command(executable, "server", "stdio", "--configDir", filepath.Join(t.TempDir(), "unresponsive"))
		return &mcp.CommandTransport{Command: cmd, TerminateDuration: 20 * time.Millisecond}
	})
	if !errors.Is(err, ErrTransport) || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("failed handshake: %v", err)
	}
	if cmd.ProcessState == nil || time.Since(start) > 2*time.Second {
		t.Fatal("failed initialization leaked subprocess")
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
	if err := s.sessions[0].Close(); err != nil {
		t.Fatal(err)
	}
	if _, err := testNativeRead(client, context.Background()); !errors.Is(err, ErrTransport) {
		t.Fatalf("lost connection: %v", err)
	}
	if err := client.Reconnect(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	server := mcp.NewServer(&mcp.Implementation{Name: "missing-capabilities", Version: "1"}, nil)
	serverTransport, clientTransport := mcp.NewInMemoryTransports()
	session, err := server.Connect(context.Background(), serverTransport, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer session.Close()
	_, err = open(context.Background(), "fixture", time.Second, func() mcp.Transport { return clientTransport })
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
	bigClient := testClient(t, large, time.Second)
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
	client, err := open(context.Background(), "fixture", time.Second, func() mcp.Transport {
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

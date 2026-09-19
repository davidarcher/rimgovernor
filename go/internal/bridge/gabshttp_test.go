package bridge

import (
	"context"
	"errors"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// endpointFactory builds sessions against an already-serving GABS HTTP
// endpoint: no process is spawned, so tests exercise the connection alone.
func endpointFactory(endpoint string) transportFactory {
	return func() mcp.Transport { return endpointTransport(endpoint) }
}

type endpointTransport string

func (e endpointTransport) Connect(context.Context) (mcp.Connection, error) {
	return newGABSHTTPConnection(string(e), nil), nil
}

func TestGABSHTTPHeldCallDoesNotSerializeOthers(t *testing.T) {
	release := make(chan struct{})
	held := make(chan struct{}, 1)
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		if args.Tool == "held" {
			held <- struct{}{}
			select {
			case <-release:
			case <-ctx.Done():
			}
		}
		return structured(`{"colonyId":"test-colony","tick":0,"operation":{"id":"receipt-1"}}`), nil
	}}
	fake, err := testkit.NewGABSHTTP(context.Background(), s.server())
	if err != nil {
		t.Fatal(err)
	}
	httpServer := httptest.NewServer(fake)
	defer httpServer.Close()
	client, err := open(context.Background(), "fixture-game", 5*time.Second, nil, nil, endpointFactory(httpServer.URL))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()

	heldDone := make(chan error, 1)
	go func() {
		_, err := client.NativeCall(context.Background(), "held", nil)
		heldDone <- err
	}()
	<-held
	began := time.Now()
	if _, err := client.NativeCall(context.Background(), "quick", nil); err != nil {
		t.Fatalf("quick call behind a held one: %v", err)
	}
	if elapsed := time.Since(began); elapsed > time.Second {
		t.Fatalf("quick call waited %v behind the held call", elapsed)
	}
	select {
	case err := <-heldDone:
		t.Fatalf("held call returned before release: %v", err)
	default:
	}
	close(release)
	if err := <-heldDone; err != nil {
		t.Fatal(err)
	}
}

func TestGABSHTTPUnreachableEndpointEndsSession(t *testing.T) {
	s := &testServer{}
	fake, err := testkit.NewGABSHTTP(context.Background(), s.server())
	if err != nil {
		t.Fatal(err)
	}
	httpServer := httptest.NewServer(fake)
	client, err := open(context.Background(), "fixture-game", time.Second, nil, nil, endpointFactory(httpServer.URL))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	httpServer.CloseClientConnections()
	httpServer.Close()
	if _, err := client.NativeCall(context.Background(), "identity", nil); err == nil {
		t.Fatal("call against a gone endpoint succeeded")
	}
	select {
	case <-client.Disconnected():
	case <-time.After(2 * time.Second):
		t.Fatal("session did not end after the endpoint went away")
	}
	if _, err := client.NativeCall(context.Background(), "identity", nil); !errors.Is(err, ErrDisconnected) {
		t.Fatalf("call after loss: %v", err)
	}
}

func TestGABSHTTPProcessLifecycle(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	var pid int
	client, err := Open(context.Background(), ProcessConfig{Executable: executable, ConfigDir: t.TempDir(), GameID: "fixture", Timeout: 5 * time.Second, Spawned: func(p int) { pid = p }})
	if err != nil {
		t.Fatal(err)
	}
	if pid == 0 {
		t.Fatal("spawned PID not reported")
	}
	if _, err = testNativeRead(client, context.Background()); err != nil {
		t.Fatal(err)
	}
	process, err := os.FindProcess(pid)
	if err != nil {
		t.Fatal(err)
	}
	// Ending GABS from outside ends the session, as a lost stdio pipe did.
	if err := process.Kill(); err != nil {
		t.Fatal(err)
	}
	select {
	case <-client.Disconnected():
	case <-time.After(5 * time.Second):
		t.Fatal("session outlived its GABS process")
	}
	if err := client.Close(); err != nil {
		t.Fatal(err)
	}

	// A GABS that never serves is ended when the connect deadline passes.
	cmd := exec.Command(executable, "server", "http", "--addr", "127.0.0.1:0", "--configDir", filepath.Join(t.TempDir(), "unresponsive"), "--log-level", "error")
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	connection := newGABSHTTPConnection("http://127.0.0.1:1", cmd)
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	if err := connection.awaitHealthy(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("unresponsive gabs: %v", err)
	}
	if err := connection.Close(); err != nil {
		t.Fatal(err)
	}
	if cmd.ProcessState == nil {
		t.Fatal("failed initialization leaked subprocess")
	}
}

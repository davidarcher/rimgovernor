package bridge

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os/exec"
	"strconv"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/childproc"
	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// GABS's stdio server reads one JSON-RPC message, handles it to completion
// and writes its reply before reading the next (pardeike/GABS v1.1.1,
// internal/mcp/stdio_server.go Serve). Every call through a stdio session
// therefore waits behind the slowest call in flight: a held clock_read_events
// long poll stalled each planner read for its whole wait and identity cost a
// second while the native side executed it in milliseconds (issue #115).
// GABS's HTTP server handles each POST to /mcp in its own goroutine, so the
// Client spawns `gabs server http` on a loopback port and exchanges every
// JSON-RPC message by one POST instead. GABS answers a request with its
// JSON-RPC reply as the body and a notification with 204; it pushes nothing
// back on this path (server notifications only reach stdio writers and SSE
// subscribers), which this Client never consumed. Calls still queue at the
// game itself: RimBridgeServer runs a companion-mod tool synchronously on
// the connection's reader, so a slow native call delays the ones behind it
// by its own native time, not by a whole session round trip.
//
// The GABS process no longer ends with its stdin: Close kills it, and on
// Windows a job object (gabsjob_windows.go) kills it when this process ends
// any other way. The game GABS launched keeps running in both cases, as it
// does when GABS exits on its own. Elsewhere a controller that dies without
// Close leaves GABS running; its runtime-owner lease (30s by default) lapses
// after its last call so a successor can still attach.
type gabsHTTPTransport struct {
	executable string
	configDir  string
	logLevel   string
	stderr     io.Writer
	spawned    func(pid int)
}

// gabsHTTPStartAttempts bounds retries of the loopback port choice: the port
// is reserved by binding it and released before GABS binds it itself, so a
// peer may take it in between and GABS exits at startup.
const gabsHTTPStartAttempts = 3

// gabsHTTPCloseWait bounds how long Close waits for a killed GABS to exit.
const gabsHTTPCloseWait = 2 * time.Second

func (t *gabsHTTPTransport) Connect(ctx context.Context) (mcp.Connection, error) {
	var lastErr error
	for attempt := 0; attempt < gabsHTTPStartAttempts; attempt++ {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		connection, err := t.start(ctx)
		if err == nil {
			return connection, nil
		}
		lastErr = err
		if !errors.Is(err, errGABSExited) {
			break
		}
	}
	return nil, lastErr
}

var errGABSExited = errors.New("gabs exited before serving")

func (t *gabsHTTPTransport) start(ctx context.Context) (mcp.Connection, error) {
	port, err := freeLoopbackPort()
	if err != nil {
		return nil, err
	}
	addr := net.JoinHostPort("127.0.0.1", strconv.Itoa(port))
	cmd := exec.Command(t.executable, "server", "http", "--addr", addr, "--configDir", t.configDir, "--log-level", t.logLevel)
	childproc.HideConsole(cmd)
	cmd.Stderr = t.stderr
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	connection := newGABSHTTPConnection("http://"+addr, cmd)
	connection.lease = leaseGABSProcess(cmd.Process)
	if err := connection.awaitHealthy(ctx); err != nil {
		_ = connection.Close()
		return nil, err
	}
	if t.spawned != nil {
		t.spawned(cmd.Process.Pid)
	}
	return connection, nil
}

func freeLoopbackPort() (int, error) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	port := listener.Addr().(*net.TCPAddr).Port
	return port, listener.Close()
}

// gabsHTTPConnection is one GABS HTTP session. Write posts the message from
// its own goroutine and queues the reply for Read, so calls overlap exactly
// as far as GABS and the native bridge let them; the Client's gate bounds
// how many are in flight. A POST that fails at the HTTP layer answers its
// request with a JSON-RPC error rather than leaving the caller waiting, and
// the process ending closes the connection so the SDK session ends the way a
// closed stdio pipe ended it.
type gabsHTTPConnection struct {
	endpoint  string
	client    *http.Client
	transport *http.Transport
	cmd       *exec.Cmd
	lease     *gabsProcessLease

	ctx       context.Context
	cancel    context.CancelFunc
	incoming  chan jsonrpc.Message
	exited    chan struct{}
	closeOnce sync.Once
}

// gabsHTTPQueue bounds replies waiting for the SDK's read loop; the loop
// drains continuously, so a post blocks here only under a caller bug.
const gabsHTTPQueue = 64

func newGABSHTTPConnection(endpoint string, cmd *exec.Cmd) *gabsHTTPConnection {
	ctx, cancel := context.WithCancel(context.Background())
	transport := &http.Transport{Proxy: nil, MaxIdleConnsPerHost: MaxConcurrentCalls}
	c := &gabsHTTPConnection{
		endpoint:  endpoint,
		client:    &http.Client{Transport: transport},
		transport: transport,
		cmd:       cmd,
		ctx:       ctx,
		cancel:    cancel,
		incoming:  make(chan jsonrpc.Message, gabsHTTPQueue),
		exited:    make(chan struct{}),
	}
	if cmd != nil {
		go func() {
			_ = cmd.Wait()
			close(c.exited)
			_ = c.Close()
		}()
	} else {
		close(c.exited)
	}
	return c
}

// awaitHealthy polls GABS's /health until it answers, the process exits or
// ctx ends. GABS prints no ready signal; its listener appears shortly after
// it loads the games configuration.
func (c *gabsHTTPConnection) awaitHealthy(ctx context.Context) error {
	ticker := time.NewTicker(50 * time.Millisecond)
	defer ticker.Stop()
	for {
		request, err := http.NewRequestWithContext(ctx, http.MethodGet, c.endpoint+"/health", nil)
		if err != nil {
			return err
		}
		response, err := c.client.Do(request)
		if err == nil {
			_, _ = io.Copy(io.Discard, response.Body)
			_ = response.Body.Close()
			if response.StatusCode == http.StatusOK {
				return nil
			}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-c.exited:
			return fmt.Errorf("%w: %s", errGABSExited, c.endpoint)
		case <-ticker.C:
		}
	}
}

func (c *gabsHTTPConnection) Read(ctx context.Context) (jsonrpc.Message, error) {
	select {
	case message := <-c.incoming:
		return message, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-c.ctx.Done():
		return nil, io.EOF
	}
}

func (c *gabsHTTPConnection) Write(ctx context.Context, message jsonrpc.Message) error {
	if err := c.ctx.Err(); err != nil {
		return io.EOF
	}
	body, err := jsonrpc.EncodeMessage(message)
	if err != nil {
		return err
	}
	var expects jsonrpc.ID
	if request, ok := message.(*jsonrpc.Request); ok && request.ID.IsValid() {
		expects = request.ID
	}
	go func() {
		reply, err := c.post(body)
		if err != nil {
			if expects.IsValid() {
				c.deliver(&jsonrpc.Response{ID: expects, Error: err})
			}
			if c.ctx.Err() == nil && errors.Is(err, errGABSUnreachable) {
				_ = c.Close()
			}
			return
		}
		if reply != nil {
			c.deliver(reply)
		}
	}()
	return nil
}

var errGABSUnreachable = errors.New("gabs http unreachable")

// post exchanges one message. The request is bound to the connection, not
// the call: cancelling a call stops the SDK waiting for its reply, but GABS
// runs the tool to completion either way (as it does over stdio), and a late
// reply for a forgotten ID is dropped by the SDK.
func (c *gabsHTTPConnection) post(body []byte) (jsonrpc.Message, error) {
	request, err := http.NewRequestWithContext(c.ctx, http.MethodPost, c.endpoint+"/mcp", bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Accept", "application/json")
	response, err := c.client.Do(request)
	if err != nil {
		return nil, fmt.Errorf("%w: %w", errGABSUnreachable, err)
	}
	defer response.Body.Close()
	switch response.StatusCode {
	case http.StatusOK:
	case http.StatusNoContent:
		_, _ = io.Copy(io.Discard, response.Body)
		return nil, nil
	default:
		detail, _ := io.ReadAll(io.LimitReader(response.Body, 512))
		return nil, fmt.Errorf("gabs http %d: %s", response.StatusCode, bytes.TrimSpace(detail))
	}
	// The receipt layer bounds a native result at maxResponseBytes; the
	// envelope around it is small, so this is the transport's memory bound.
	raw, err := io.ReadAll(io.LimitReader(response.Body, maxResponseBytes+(1<<20)))
	if err != nil {
		return nil, fmt.Errorf("%w: %w", errGABSUnreachable, err)
	}
	reply, err := jsonrpc.DecodeMessage(raw)
	if err != nil {
		return nil, fmt.Errorf("gabs http reply: %w", err)
	}
	return reply, nil
}

func (c *gabsHTTPConnection) deliver(message jsonrpc.Message) {
	select {
	case c.incoming <- message:
	case <-c.ctx.Done():
	}
}

func (c *gabsHTTPConnection) Close() error {
	c.closeOnce.Do(func() {
		c.cancel()
		if c.cmd != nil && c.cmd.Process != nil {
			select {
			case <-c.exited:
			default:
				_ = c.cmd.Process.Kill()
			}
			select {
			case <-c.exited:
			case <-time.After(gabsHTTPCloseWait):
			}
		}
		c.lease.release()
		c.transport.CloseIdleConnections()
	})
	return nil
}

func (c *gabsHTTPConnection) SessionID() string { return "" }

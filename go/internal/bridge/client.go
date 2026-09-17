// Package bridge owns GABS MCP sessions. It exposes reviewed reads, never a
// generic native call API. Discovery annotations do not grant write authority.
package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os/exec"
	"path/filepath"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

var (
	ErrClosed       = errors.New("bridge closed")
	ErrDisconnected = errors.New("bridge disconnected")
	ErrTransport    = errors.New("bridge transport failure")
	ErrRefused      = errors.New("bridge read refused")
	ErrContract     = errors.New("bridge contract failure")
)

// ProcessConfig starts only GABS; it never launches or stops a game. Paths are
// explicit absolute paths. Timeout bounds initialization, discovery and each read,
// including queue time. Stderr is optional and must be safe for concurrent writes.
type ProcessConfig struct {
	Executable string
	ConfigDir  string
	GameID     string
	LogLevel   string
	Timeout    time.Duration
	Stderr     io.Writer
	// Recorder, when set, durably records every native request/response/error
	// including background reads.
	// It is opt-in: a nil Recorder records nothing and costs nothing.
	Recorder *FlightRecorder
}

// Result retains the complete MCP receipt at the transport boundary. Structured
// is untrusted wire JSON until a consumer decodes its generated contract.
type Result struct {
	Envelope   json.RawMessage
	Structured json.RawMessage
	Text       []string
}

type Refusal struct {
	Tool   string
	Result Result
}

func (e *Refusal) Error() string { return "bridge read refused: " + e.Tool }
func (e *Refusal) Unwrap() error { return ErrRefused }

type Tool struct {
	Name        string
	InputSchema json.RawMessage
}
type Discovery struct {
	ProtocolVersion string
	ServerName      string
	ServerVersion   string
	Tools           []Tool
}

// maxResponseBytes bounds the whole JSON-RPC tools/call response (envelope,
// structured content and text combined). It must exceed ProtoBoundary's own
// 48 MiB MaximumMediaEnvelopeBytes (raw uncompressed video frames go out
// base64-encoded through that path) plus the MCP tool-result wrapper's own
// overhead, or every ReadFrame call fails as "oversized" before the native
// media bound is ever reached.
const maxResponseBytes = 50 << 20
const maxTools = 2048

type transportFactory func() mcp.Transport

type connectionOwner struct {
	mcp.Transport
	connection *receiptConnection
}

func (t *connectionOwner) Connect(ctx context.Context) (mcp.Connection, error) {
	connection, err := t.Transport.Connect(ctx)
	if connection != nil {
		t.connection = &receiptConnection{Connection: connection}
	}
	if err != nil {
		return nil, err
	}
	return t.connection, nil
}
func (t *connectionOwner) close() error {
	if t.connection != nil {
		return t.connection.Close()
	}
	return nil
}

type liveSession struct {
	sdk       *mcp.ClientSession
	owner     *connectionOwner
	ctx       context.Context
	cancel    context.CancelFunc
	discovery Discovery
}

// maxConcurrentCalls bounds how many native calls one Client may have in
// flight at once. GABP only requires outbound frame writes to stay atomic
// when responses/events are produced concurrently (github.com/pardeike/GABS
// docs/releases/v1.1.1.md); the go-sdk transport already guarantees that
// (mcp.ioConn.writeMu) and correlates concurrent calls by JSON-RPC ID
// (jsonrpc2.Connection.outgoingCalls), same as GABS's own GABP client
// (pendingReqs). So calls need not be single-flight; this cap is only
// backpressure against a caller bug flooding the native bridge at once.
const maxConcurrentCalls = 8

// Client owns a single session. Up to maxConcurrentCalls calls may be in
// flight at once; Close cancels in-flight and queued work. Reconnect is
// explicit and never repeats a native call.
type Client struct {
	lifecycle  chan struct{}
	mu         sync.Mutex
	live       *liveSession
	closed     bool
	dialCancel context.CancelFunc
	factory    transportFactory
	gameID     string
	timeout    time.Duration
	gate       chan struct{}

	recorder         *FlightRecorder
	recordingContext func() map[string]any
}

// SetRecordingContext installs a callback read once per recorded call and
// attached to every flight-recorder row it produces (colony/plan/direction
// identity, not authority). It has no effect when the Client has no Recorder.
func (c *Client) SetRecordingContext(context func() map[string]any) {
	c.mu.Lock()
	c.recordingContext = context
	c.mu.Unlock()
}

func Open(ctx context.Context, config ProcessConfig) (*Client, error) {
	if !filepath.IsAbs(config.Executable) || !filepath.IsAbs(config.ConfigDir) {
		return nil, fmt.Errorf("%w: absolute executable/config paths required", ErrContract)
	}
	if config.LogLevel == "" {
		config.LogLevel = "error"
	}
	switch config.LogLevel {
	case "debug", "info", "warn", "error":
	default:
		return nil, fmt.Errorf("%w: invalid log level", ErrContract)
	}
	return open(ctx, config.GameID, config.Timeout, config.Recorder, func() mcp.Transport {
		cmd := exec.Command(config.Executable, "server", "stdio", "--configDir", config.ConfigDir, "--log-level", config.LogLevel)
		cmd.Stderr = config.Stderr
		return &mcp.CommandTransport{Command: cmd, TerminateDuration: time.Second}
	})
}

func open(ctx context.Context, gameID string, timeout time.Duration, recorder *FlightRecorder, factory transportFactory) (*Client, error) {
	if gameID == "" || len(gameID) > 256 {
		return nil, fmt.Errorf("%w: invalid game ID", ErrContract)
	}
	if timeout == 0 {
		timeout = 30 * time.Second
	}
	if timeout < time.Millisecond || timeout > 120*time.Second {
		return nil, fmt.Errorf("%w: timeout outside 1ms..120s", ErrContract)
	}
	c := &Client{factory: factory, gameID: gameID, timeout: timeout, gate: make(chan struct{}, maxConcurrentCalls), lifecycle: make(chan struct{}, 1), recorder: recorder}
	if err := c.Reconnect(ctx); err != nil {
		return nil, err
	}
	return c, nil
}

func (c *Client) closeLive() error {
	c.mu.Lock()
	live := c.live
	c.live = nil
	c.mu.Unlock()
	if live == nil {
		return nil
	}
	live.cancel()
	return live.sdk.Close()
}

func (c *Client) Close() error {
	c.mu.Lock()
	c.closed = true
	if c.dialCancel != nil {
		c.dialCancel()
	}
	if c.live != nil {
		c.live.cancel()
	}
	c.mu.Unlock()
	c.lifecycle <- struct{}{}
	defer func() { <-c.lifecycle }()
	return c.closeLive()
}

func (c *Client) Reconnect(ctx context.Context) error {
	ctx, cancel := context.WithTimeout(ctx, c.timeout)
	defer cancel()
	select {
	case c.lifecycle <- struct{}{}:
		defer func() { <-c.lifecycle }()
	case <-ctx.Done():
		return ctx.Err()
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	c.mu.Lock()
	closed := c.closed
	c.mu.Unlock()
	if closed {
		return ErrClosed
	}
	if err := c.closeLive(); err != nil {
		return fmt.Errorf("%w: close old session: %w", ErrTransport, err)
	}
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return ErrClosed
	}
	c.dialCancel = cancel
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.dialCancel = nil; c.mu.Unlock() }()
	owner := &connectionOwner{Transport: c.factory()}
	sdk := mcp.NewClient(&mcp.Implementation{Name: "rimgovernor", Version: "go-read-v1"}, nil)
	session, err := sdk.Connect(ctx, owner, nil)
	if err != nil {
		_ = owner.close()
		return fmt.Errorf("%w: initialize: %w", ErrTransport, err)
	}
	discovery, err := discover(ctx, session)
	if err != nil {
		_ = session.Close()
		return err
	}
	liveCtx, liveCancel := context.WithCancel(context.Background())
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		liveCancel()
		_ = session.Close()
		return ErrClosed
	}
	c.live = &liveSession{sdk: session, owner: owner, ctx: liveCtx, cancel: liveCancel, discovery: discovery}
	c.mu.Unlock()
	return nil
}

// Discovery is the initialized session's bounded core tool catalog, copied so
// callers cannot change the capability gate. Native tool details are separate.
func (c *Client) Discovery() (Discovery, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return Discovery{}, ErrClosed
	}
	if c.live == nil {
		return Discovery{}, ErrDisconnected
	}
	d := c.live.discovery
	d.Tools = append([]Tool(nil), d.Tools...)
	for i := range d.Tools {
		d.Tools[i].InputSchema = append(json.RawMessage(nil), d.Tools[i].InputSchema...)
	}
	return d, nil
}

func discover(ctx context.Context, session *mcp.ClientSession) (Discovery, error) {
	init := session.InitializeResult()
	if init == nil || init.ServerInfo == nil {
		return Discovery{}, fmt.Errorf("%w: server identity missing", ErrContract)
	}
	d := Discovery{ProtocolVersion: init.ProtocolVersion, ServerName: init.ServerInfo.Name, ServerVersion: init.ServerInfo.Version}
	cursor := ""
	seen := map[string]bool{}
	names := map[string]bool{}
	bytes := 0
	for page := 0; page < 32; page++ {
		result, err := session.ListTools(ctx, &mcp.ListToolsParams{Cursor: cursor})
		if err != nil {
			return Discovery{}, fmt.Errorf("%w: discovery: %w", ErrTransport, err)
		}
		if result == nil {
			return Discovery{}, fmt.Errorf("%w: tool discovery missing", ErrContract)
		}
		for _, tool := range result.Tools {
			if tool == nil || tool.Name == "" || names[tool.Name] || len(d.Tools) >= maxTools {
				return Discovery{}, fmt.Errorf("%w: invalid tool catalog", ErrContract)
			}
			schema, err := json.Marshal(tool.InputSchema)
			if err != nil {
				return Discovery{}, fmt.Errorf("%w: tool schema: %w", ErrContract, err)
			}
			bytes += len(schema) + len(tool.Name)
			if bytes > maxResponseBytes {
				return Discovery{}, fmt.Errorf("%w: oversized tool catalog", ErrContract)
			}
			names[tool.Name] = true
			d.Tools = append(d.Tools, Tool{Name: tool.Name, InputSchema: schema})
		}
		cursor = result.NextCursor
		if cursor == "" {
			if !names["games_call_tool"] || !names["games_tool_detail"] {
				return Discovery{}, fmt.Errorf("%w: required GABS read capabilities missing", ErrContract)
			}
			return d, nil
		}
		if len(cursor) > 4096 || seen[cursor] {
			return Discovery{}, fmt.Errorf("%w: invalid discovery cursor", ErrContract)
		}
		seen[cursor] = true
	}
	return Discovery{}, fmt.Errorf("%w: discovery page limit", ErrContract)
}

func (c *Client) operation(ctx context.Context, run func(context.Context, *liveSession) (Result, error)) (Result, error) {
	ctx, cancel := context.WithTimeout(ctx, c.timeout)
	defer cancel()
	c.mu.Lock()
	live, closed := c.live, c.closed
	c.mu.Unlock()
	if closed {
		return Result{}, ErrClosed
	}
	if live == nil {
		return Result{}, ErrDisconnected
	}
	stop := context.AfterFunc(live.ctx, cancel)
	defer stop()
	timing := &callTiming{began: time.Now()}
	select {
	case c.gate <- struct{}{}:
		defer func() { <-c.gate }()
	case <-ctx.Done():
		return Result{}, ctx.Err()
	}
	timing.gateWait = time.Since(timing.began)
	if err := ctx.Err(); err != nil {
		return Result{}, err
	}
	return run(withCallTiming(ctx, timing), live)
}

// snapshotRecordingContext reads the installed recording-context callback (if
// any) once per call and merges in any action/goal correlation the caller
// attached to ctx via WithFlightAction.
func (c *Client) snapshotRecordingContext(ctx context.Context) map[string]any {
	c.mu.Lock()
	callback := c.recordingContext
	c.mu.Unlock()
	merged := map[string]any{}
	if callback != nil {
		for k, v := range callback() {
			merged[k] = v
		}
	}
	for k, v := range flightActionFrom(ctx) {
		merged[k] = v
	}
	return merged
}

func (c *Client) core(ctx context.Context, live *liveSession, name string, arguments json.RawMessage) (Result, error) {
	found := false
	for _, tool := range live.discovery.Tools {
		if tool.Name == name {
			found = true
			break
		}
	}
	if !found {
		return Result{}, fmt.Errorf("%w: missing capability %s", ErrContract, name)
	}
	var recordCtx map[string]any
	var request uint64
	recording := c.recorder != nil
	if recording {
		recordCtx = c.snapshotRecordingContext(ctx)
		request, _ = c.recorder.Event("native_request", recordCtx, true, map[string]any{"tool": name, "arguments": arguments})
	}
	timing := callTimingFrom(ctx)
	if timing != nil {
		timing.request = request
	}
	waiter := make(chan receiptOutcome, 1)
	callBegan := time.Now()
	result, err := live.sdk.CallTool(withReceiptWaiter(ctx, waiter), &mcp.CallToolParams{Name: name, Arguments: arguments})
	callElapsed := time.Since(callBegan)
	// phases is attached to the response/error row so a timeline consumer can
	// split the call without re-deriving it from wall clocks.
	phases := func(decode time.Duration, bytes int) map[string]any {
		out := map[string]any{"call_ms": millis(callElapsed), "decode_ms": millis(decode), "response_bytes": bytes}
		if timing != nil {
			out["gate_wait_ms"] = millis(timing.gateWait)
			out["total_ms"] = millis(time.Since(timing.began))
		}
		return out
	}
	nativeTool := nativeToolOf(name, arguments)
	var raw json.RawMessage
	var receiptErr error
	select {
	case outcome := <-waiter:
		raw, receiptErr = outcome.raw, outcome.err
	default:
		// No response ever reached Read for this request (write failure,
		// cancellation before reply, ...); fall through on the CallTool
		// error below, exactly as when a receipt legitimately never arrives.
	}
	if receiptErr != nil {
		if recording {
			c.recorder.Event("native_error", recordCtx, true, map[string]any{"request": request, "tool": name, "native_tool": nativeTool, "error": receiptErr.Error(), "timing": phases(0, len(raw))})
		}
		return Result{}, receiptErr
	}
	if err != nil {
		if recording {
			c.recorder.Event("native_error", recordCtx, true, map[string]any{"request": request, "tool": name, "native_tool": nativeTool, "error": err.Error(), "timing": phases(0, len(raw))})
		}
		return Result{}, fmt.Errorf("%w: %s: %w", ErrTransport, name, err)
	}
	decodeBegan := time.Now()
	decoded, decodeErr := decodeReceipt(name, raw, result)
	decodeElapsed := time.Since(decodeBegan)
	if recording {
		if decodeErr != nil {
			c.recorder.Event("native_error", recordCtx, true, map[string]any{"request": request, "tool": name, "native_tool": nativeTool, "error": decodeErr.Error(), "timing": phases(decodeElapsed, len(raw))})
		} else {
			c.recorder.Event("native_response", recordCtx, false, map[string]any{"request": request, "tool": name, "native_tool": nativeTool, "result": decoded.Structured, "timing": phases(decodeElapsed, len(raw))})
		}
	}
	return decoded, decodeErr
}

func decodeResult(name string, result *mcp.CallToolResult) (Result, error) {
	if result == nil {
		return Result{}, fmt.Errorf("%w: missing result", ErrContract)
	}
	envelope, err := json.Marshal(result)
	if err != nil || len(envelope) > maxResponseBytes {
		return Result{}, fmt.Errorf("%w: invalid or oversized result", ErrContract)
	}
	return decodeReceipt(name, envelope, result)
}

func decodeReceipt(name string, envelope json.RawMessage, result *mcp.CallToolResult) (Result, error) {
	if result == nil || len(envelope) == 0 || len(envelope) > maxResponseBytes {
		return Result{}, fmt.Errorf("%w: raw MCP receipt missing or oversized", ErrContract)
	}
	var wire struct {
		Structured json.RawMessage `json:"structuredContent"`
	}
	if err := json.Unmarshal(envelope, &wire); err != nil {
		return Result{}, fmt.Errorf("%w: invalid raw MCP receipt: %w", ErrContract, err)
	}
	raw := Result{Envelope: envelope, Structured: wire.Structured}
	for _, content := range result.Content {
		if text, ok := content.(*mcp.TextContent); ok {
			raw.Text = append(raw.Text, text.Text)
		}
	}
	var flags struct {
		Success *bool           `json:"success"`
		Refused bool            `json:"refused"`
		Unknown json.RawMessage `json:"unknownArguments"`
	}
	if len(raw.Structured) > 0 && json.Unmarshal(raw.Structured, &flags) != nil {
		return raw, fmt.Errorf("%w: structured result must be object", ErrContract)
	}
	unknown := string(flags.Unknown)
	if result.IsError || len(result.InputRequests) > 0 || flags.Success != nil && !*flags.Success || flags.Refused || unknown != "" && unknown != "null" && unknown != "[]" && unknown != "{}" {
		return raw, &Refusal{Tool: name, Result: raw}
	}
	return raw, nil
}

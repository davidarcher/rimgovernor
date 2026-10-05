// Package bridge owns the controller's session with the game: gamehost
// launches or finds the process and a direct GABP connection carries its
// calls. It exposes reviewed reads, never a generic native call API.
// Discovery annotations do not grant write authority.
package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gamehost"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

var (
	ErrClosed       = errors.New("bridge closed")
	ErrDisconnected = errors.New("bridge disconnected")
	ErrTransport    = errors.New("bridge transport failure")
	ErrRefused      = errors.New("bridge read refused")
	ErrContract     = errors.New("bridge contract failure")
)

// ProcessConfig names the game a Client drives. Timeout bounds connecting,
// discovery and each read, including queue time.
type ProcessConfig struct {
	GameID string
	// Launch is how games_start starts the game when none is running.
	// Launch.StateDir (absolute, required) is where the running game's
	// endpoint is recorded, so every Client sharing it finds the same
	// process; an empty Launch.Executable makes the Client attach-only.
	// Launch.GameID is taken from GameID. LaunchSpecFromConfig builds it
	// from a configuration directory.
	Launch  gamehost.Spec
	Timeout time.Duration
	// Recorder, when set, durably records every native request/response/error
	// including background reads.
	// It is opt-in: a nil Recorder records nothing and costs nothing.
	Recorder *FlightRecorder
	// Transcript, when set, records every call and its raw receipt
	// so a Replay can serve the session back without a game (#282).
	Transcript *Transcript
}

// Result retains the complete receipt at the transport boundary. Structured
// is untrusted wire JSON until a consumer decodes its generated contract.
type Result struct {
	Envelope   json.RawMessage
	Structured json.RawMessage
	Text       []string
	// slotted is the raw reply read from the native reply ring (#1344)
	// when the wrapper named a slot.
	slotted []byte
}

type Refusal struct {
	Tool string
	// Cause is the refusal's own account of itself, lifted out of the
	// structured result: the native reason a fixture op reports, or the
	// first line of the exception the game threw. A refusal whose result
	// says nothing leaves it empty. The tool name alone identified only
	// the wrapper (games_call_tool on every native read), so nine nightly
	// cases reported a transport-shaped error for a named native refusal
	// nobody could read without the evidence tree (#663).
	Cause  string
	Result Result
}

func (e *Refusal) Error() string {
	if e.Cause == "" {
		return "bridge read refused: " + e.Tool
	}
	return "bridge read refused: " + e.Tool + ": " + e.Cause
}
func (e *Refusal) Unwrap() error { return ErrRefused }

// refusalCauseBytes bounds a lifted cause. A native exception carries its
// whole managed stack trace; the first line names the failure and the rest
// belongs in the recorded receipt, not in every error string that wraps it.
const refusalCauseBytes = 240

// refusalCause lifts the refusal's own account out of a structured result.
// The native side reports either a reviewed refusal reason or, when the
// game threw, an exception whose first line is the message. Only the first
// line is taken, and it is bounded: callers put this in an error string.
func refusalCause(structured json.RawMessage) string {
	if len(structured) == 0 {
		return ""
	}
	var wire struct {
		Reason    string `json:"reason"`
		Exception string `json:"exception"`
		Error     string `json:"error"`
		Message   string `json:"message"`
	}
	if json.Unmarshal(structured, &wire) != nil {
		return ""
	}
	for _, candidate := range []string{wire.Reason, wire.Exception, wire.Error, wire.Message} {
		if cause := firstLine(candidate); cause != "" {
			return cause
		}
	}
	return ""
}

// firstLine is candidate's first line, bounded to refusalCauseBytes. The
// native payload escapes its own backslashes to forward slashes, so a
// managed stack trace arrives with literal "/r/n" separators as well as
// real newlines; both end the line.
func firstLine(candidate string) string {
	line := candidate
	for _, separator := range []string{"\r", "\n", "/r/n", "/n", "/r"} {
		if i := strings.Index(line, separator); i >= 0 {
			line = line[:i]
		}
	}
	line = strings.TrimSpace(line)
	if len(line) > refusalCauseBytes {
		line = strings.TrimSpace(line[:refusalCauseBytes]) + "..."
	}
	return line
}

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

// maxResponseBytes bounds one receipt (envelope, structured content and
// text combined); the GABP frame cap is 1 MiB above it (gamebackend.go).
const maxResponseBytes = 50 << 20

type liveSession struct {
	backend   backend
	ctx       context.Context
	cancel    context.CancelFunc
	discovery Discovery
	// described records the native methods whose games_tool_detail input
	// schema this session has already fetched and validated. Tool schemas
	// are static for the life of a connection, so protoCall describes each
	// method once per session instead of before every call; a
	// Reconnect/Reattach starts a fresh liveSession and therefore a fresh set.
	describeMu sync.Mutex
	described  map[string]bool
}

// MaxConcurrentCalls bounds how many native calls one Client may have in
// flight at once. The GABP connection correlates concurrent requests by id,
// so calls need not be single-flight; this cap is backpressure against a
// caller bug flooding the native bridge at once, and the slots are handed
// out by AdmissionClass (admission.go, #631) so bulk reads never hold every
// one against the control path.
const MaxConcurrentCalls = 8

// Client owns a single session. Up to MaxConcurrentCalls calls may be in
// flight at once; Close cancels in-flight and queued work. Reconnect is
// explicit and never repeats a native call. A session whose game connection
// drops on its own is ended at once: calls then fail fast with
// ErrDisconnected, Disconnected reports the loss, and Reattach is the
// bounded recovery a supervisor drives.
type Client struct {
	lifecycle  chan struct{}
	mu         sync.Mutex
	live       *liveSession
	closed     bool
	dialCancel context.CancelFunc
	factory    backendFactory
	gameID     string
	// stateDir is the launch state dir, empty for clients built without
	// a game host.
	stateDir string
	timeout  time.Duration
	gate     *admission
	// writes counts the typed side-effect calls queued or in flight.
	writes atomic.Int64

	frames  *frameStream
	catalog catalogCache
	replies *replySlots

	recorder         *FlightRecorder
	recordingContext func() map[string]any
	transcript       *Transcript
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
	if !filepath.IsAbs(config.Launch.StateDir) || config.Launch.Executable != "" && !filepath.IsAbs(config.Launch.Executable) {
		return nil, fmt.Errorf("%w: absolute state dir and executable paths required", ErrContract)
	}
	launch := config.Launch
	client, err := open(ctx, config.GameID, config.Timeout, config.Recorder, config.Transcript, func(context.Context) (backend, error) {
		return newGameBackend(config.GameID, launch, config.Recorder), nil
	})
	if err != nil {
		return nil, err
	}
	// The snapshot stream (#858) serves the state families of a game on
	// this host; test clients built with open read over GABP only.
	client.stateDir = launch.StateDir
	client.frames = newFrameStream()
	client.replies = newReplySlots()
	return client, nil
}

func open(ctx context.Context, gameID string, timeout time.Duration, recorder *FlightRecorder, transcript *Transcript, factory backendFactory) (*Client, error) {
	if gameID == "" || len(gameID) > 256 {
		return nil, fmt.Errorf("%w: invalid game ID", ErrContract)
	}
	if timeout == 0 {
		timeout = 30 * time.Second
	}
	if timeout < time.Millisecond || timeout > 120*time.Second {
		return nil, fmt.Errorf("%w: timeout outside 1ms..120s", ErrContract)
	}
	c := &Client{factory: factory, gameID: gameID, timeout: timeout, gate: newAdmission(MaxConcurrentCalls), lifecycle: make(chan struct{}, 1), recorder: recorder, transcript: transcript}
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
	return live.backend.close()
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
	c.frames.close()
	c.replies.close()
	return c.closeLive()
}

type callTimeoutKey struct{}

// MaxCallTimeout bounds what WithCallTimeout may ask for. It is generous
// because the calls that need it are the lifecycle ones: generating a fresh
// debug world or loading a save can legitimately take minutes on a loaded
// CI runner, and the native tool takes its own timeoutMs for exactly that
// wait. Any read that runs this long is a hang, and the harness budget
// ends the case well before the bound.
const MaxCallTimeout = 10 * time.Minute

// WithCallTimeout raises this call's deadline above the session's Timeout.
// A session timeout that bounds every read cannot also cover a native call
// the caller explicitly asked to wait longer for: food/fishing spent its
// 60s session budget waiting on a native start it had given
// 120s, and reported the cut as "bridge transport failure: games_call_tool:
// context deadline exceeded" (#663). A caller passing a native timeoutMs
// must pass the matching deadline here. Lowering the deadline is not this
// function's job: a shorter timeout than the session's is ignored, so a
// caller cannot accidentally tighten a read.
func WithCallTimeout(ctx context.Context, timeout time.Duration) context.Context {
	if timeout <= 0 {
		return ctx
	}
	if timeout > MaxCallTimeout {
		timeout = MaxCallTimeout
	}
	return context.WithValue(ctx, callTimeoutKey{}, timeout)
}

// CallTimeoutFrom is the raised deadline ctx carries, if any. A caller that
// assembles arguments and the matching deadline in different places uses it
// to check its own work.
func CallTimeoutFrom(ctx context.Context) (time.Duration, bool) {
	timeout, ok := ctx.Value(callTimeoutKey{}).(time.Duration)
	return timeout, ok
}

// callTimeout is the deadline this call runs under: the session's Timeout,
// or the longer one the caller asked for.
func (c *Client) callTimeout(ctx context.Context) time.Duration {
	if timeout, ok := ctx.Value(callTimeoutKey{}).(time.Duration); ok && timeout > c.timeout {
		return timeout
	}
	return c.timeout
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
	// The new session may be another game process on this host; its
	// snapshot ring is a different one (#858).
	c.frames.close()
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return ErrClosed
	}
	c.dialCancel = cancel
	c.mu.Unlock()
	defer func() { c.mu.Lock(); c.dialCancel = nil; c.mu.Unlock() }()
	session, err := c.factory(ctx)
	if err != nil {
		return fmt.Errorf("%w: initialize: %w", ErrTransport, err)
	}
	discovery, err := checkDiscovery(session.discovery())
	if err != nil {
		_ = session.close()
		return err
	}
	c.transcript.session(c.gameID, discovery)
	liveCtx, liveCancel := context.WithCancel(context.Background())
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		liveCancel()
		_ = session.close()
		return ErrClosed
	}
	live := &liveSession{backend: session, ctx: liveCtx, cancel: liveCancel, discovery: discovery}
	c.live = live
	c.mu.Unlock()
	go c.watch(live)
	return nil
}

// watch drops live once its session has ended for any reason. Reconnect and
// Close end sessions themselves, in which case live is already superseded or
// nil and this is a no-op; a game connection that dropped on its own is what
// makes the Client disconnected.
func (c *Client) watch(live *liveSession) {
	<-live.backend.done()
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.live == live {
		c.live = nil
		live.cancel()
	}
}

// Disconnected is closed once the current session has been lost or the
// Client closed. Without a live session it is already closed. A successful
// Reattach/Reconnect starts a fresh session with a fresh channel.
func (c *Client) Disconnected() <-chan struct{} {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.live == nil || c.closed {
		return closedChannel
	}
	return c.live.ctx.Done()
}

var closedChannel = func() chan struct{} { ch := make(chan struct{}); close(ch); return ch }()

// GameClosed reports, after a lost session, whether the player closed the
// game: its most recent launch, started by this process, exited 0. A crash,
// a kill or a game another process launched reads false, as does one still
// running when ctx ends.
func (c *Client) GameClosed(ctx context.Context) bool {
	if c.stateDir == "" {
		return false
	}
	for {
		if code, ok := gamehost.LastExit(c.stateDir, c.gameID); ok {
			return code == 0
		}
		select {
		case <-ctx.Done():
			return false
		case <-time.After(250 * time.Millisecond):
		}
	}
}

// Reattach restores service after the session was lost: a fresh session,
// then games_start and ConnectWithPoll against the game that kept running
// (relaunching it when it did not), exactly as a restarted controller
// attaches. It is bounded by ctx
// and the connect deadlines, never repeats a native call, and leaves the
// Client disconnected when any step fails so the caller can retry.
func (c *Client) Reattach(ctx context.Context) error {
	if err := c.Reconnect(ctx); err != nil {
		return err
	}
	started, err := c.GamesStart(ctx)
	if err != nil {
		_ = c.closeLive()
		return fmt.Errorf("reattach: games_start: %w", err)
	}
	if _, err := c.ConnectWithPoll(ctx, started); err != nil {
		_ = c.closeLive()
		return fmt.Errorf("reattach: connect: %w", err)
	}
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

// checkDiscovery validates a session's catalog and copies it.
func checkDiscovery(d Discovery) (Discovery, error) {
	names := map[string]bool{}
	tools := make([]Tool, 0, len(d.Tools))
	for _, tool := range d.Tools {
		if tool.Name == "" || names[tool.Name] {
			return Discovery{}, fmt.Errorf("%w: invalid tool catalog", ErrContract)
		}
		names[tool.Name] = true
		tools = append(tools, Tool{Name: tool.Name, InputSchema: append(json.RawMessage(nil), tool.InputSchema...)})
	}
	if !names["games_call_tool"] || !names["games_tool_detail"] {
		return Discovery{}, fmt.Errorf("%w: required read capabilities missing", ErrContract)
	}
	d.Tools = tools
	return d, nil
}

// operation admits one call under class (the class of the native tool it
// will reach; see admissionClassOf) and runs it against the live session.
func (c *Client) operation(ctx context.Context, class AdmissionClass, run func(context.Context, *liveSession) (Result, error)) (Result, error) {
	ctx, cancel := context.WithTimeout(ctx, c.callTimeout(ctx))
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
	// A call outside any traced unit of work (startup, a dashboard read)
	// is a trace of its own, so its request, reply and decode rows share
	// one id instead of each minting a single-row trace (#298).
	ctx, _ = telemetry.EnsureTrace(ctx)
	timing := &callTiming{began: time.Now()}
	admitted, err := c.gate.acquire(ctx, class)
	if err != nil {
		return Result{}, err
	}
	defer c.gate.release(class)
	timing.admission = admitted
	timing.gateWait = admitted.wait
	if err := ctx.Err(); err != nil {
		return Result{}, err
	}
	return run(withCallTiming(ctx, timing), live)
}

// snapshotRecordingContext reads the installed recording-context callback (if
// any) once per call and merges in the trace the caller's step or dispatch
// attached via telemetry.WithTrace (#298).
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
	telemetry.TraceFrom(ctx).Stamp(merged)
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
	var marker *callMarker
	recording := c.recorder != nil
	nativeTool := nativeToolOf(name, arguments)
	if recording {
		recordCtx = c.snapshotRecordingContext(ctx)
		marker = c.startCallMarker(recordCtx, name, nativeTool, arguments)
	}
	timing := callTimingFrom(ctx)
	callBegan := time.Now()
	raw, err := live.backend.call(ctx, name, arguments)
	callElapsed := time.Since(callBegan)
	if err == nil && len(raw) > maxResponseBytes {
		raw, err = nil, fmt.Errorf("%w: oversized native result", ErrContract)
	}
	// A reply in the native reply ring is read now, before a later reply
	// can take its slot (#1344).
	var slotted []byte
	if err == nil {
		raw, slotted, err = c.replies.resolveReplySlot(raw, recording || c.transcript != nil)
	}
	// phases is attached to the response/error row so a timeline consumer can
	// split the call without re-deriving it from wall clocks.
	phases := func(decode time.Duration, bytes int) map[string]any {
		out := map[string]any{"call_ms": millis(callElapsed), "decode_ms": millis(decode), "response_bytes": bytes}
		if timing != nil {
			out["gate_wait_ms"] = millis(timing.gateWait)
			out["total_ms"] = millis(time.Since(timing.began))
			out["class"] = string(timing.admission.class)
			out["queue_depth"] = timing.admission.queueDepth
			out["class_queue_depth"] = timing.admission.classDepth
		}
		return out
	}
	// One native_call row per completed call; request names the in-flight
	// marker when the call ran past slowCallMarker (#2057).
	var request uint64
	if marker != nil {
		request = marker.finish()
	}
	callRow := func(fields map[string]any) map[string]any {
		fields["tool"], fields["native_tool"] = name, nativeTool
		if request != 0 {
			fields["request"] = request
		}
		if len(arguments) > 0 {
			fields["arguments"] = arguments
		}
		return fields
	}
	if name == "games_call_tool" {
		readTallyFrom(ctx).add(c, nativeTool)
	} else {
		readTallyFrom(ctx).add(c, name)
	}
	if c.transcript != nil {
		c.transcript.call(ctx, name, arguments, raw, err, callElapsed)
	}
	if err != nil {
		if recording {
			c.recordCall(ctx, recordCtx, callRow(map[string]any{"ok": false, "error": err.Error(), "timing": phases(0, len(raw))}))
		}
		if errors.Is(err, ErrContract) {
			return Result{}, err
		}
		return Result{}, fmt.Errorf("%w: %s: %w", ErrTransport, name, err)
	}
	decodeBegan := time.Now()
	decoded, decodeErr := decodeReceipt(name, raw)
	decoded.slotted = slotted
	decodeElapsed := time.Since(decodeBegan)
	if recording {
		if decodeErr != nil {
			row := callRow(map[string]any{"ok": false, "error": decodeErr.Error(), "timing": phases(decodeElapsed, len(raw))})
			// A refusal's text names its cause (an open attention item, a
			// tool the game no longer exposes), which the error alone hides.
			var refusal *Refusal
			if errors.As(decodeErr, &refusal) {
				row["refused_text"] = refusal.Result.Text
			}
			c.recordCall(ctx, recordCtx, row)
		} else {
			timing := phases(decodeElapsed, len(raw))
			if native, ok := nativeTiming(decoded.Structured); ok {
				timing["native_queue_ms"] = native.queueMs
				timing["native_execute_ms"] = native.executeMs
				if native.trace != "" {
					timing["native_trace"] = native.trace
				}
				if native.queueDepth >= 0 {
					timing["native_queue_depth"] = native.queueDepth
				}
				// The companion's observation capture account and its frame
				// recorder's session counters (#642), verbatim.
				if native.observation != nil {
					timing["native_observation"] = native.observation
				}
				if native.frames != nil {
					timing["native_frames"] = native.frames
				}
			}
			row := callRow(map[string]any{"ok": true, "result": decoded.Structured, "timing": timing})
			if typeName := recordedReplyType(ctx); typeName != "" {
				row["reply_type"] = typeName
			}
			c.recordCall(ctx, recordCtx, row)
		}
	}
	return decoded, decodeErr
}

func decodeReceipt(name string, envelope json.RawMessage) (Result, error) {
	if len(envelope) == 0 || len(envelope) > maxResponseBytes {
		return Result{}, fmt.Errorf("%w: raw receipt missing or oversized", ErrContract)
	}
	var wire struct {
		Content []struct {
			Type string `json:"type"`
			Text string `json:"text"`
		} `json:"content"`
		Structured json.RawMessage `json:"structuredContent"`
		IsError    bool            `json:"isError"`
	}
	if err := json.Unmarshal(envelope, &wire); err != nil {
		return Result{}, fmt.Errorf("%w: invalid raw receipt: %w", ErrContract, err)
	}
	if string(wire.Structured) == "null" {
		wire.Structured = nil
	}
	raw := Result{Envelope: envelope, Structured: wire.Structured}
	for _, content := range wire.Content {
		if content.Type == "text" {
			raw.Text = append(raw.Text, content.Text)
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
	if wire.IsError || flags.Success != nil && !*flags.Success || flags.Refused || unknown != "" && unknown != "null" && unknown != "[]" && unknown != "{}" {
		return raw, &Refusal{Tool: name, Cause: refusalCause(raw.Structured), Result: raw}
	}
	return raw, nil
}

// DropConnection closes the live session's game connection the way a
// transport failure would, leaving the game running: Disconnected fires
// and Reattach restores service. It is for transport-drop acceptance cases
// (#87) and reports false when no game connection was open.
func (c *Client) DropConnection() bool {
	c.mu.Lock()
	live := c.live
	c.mu.Unlock()
	if live == nil {
		return false
	}
	dropper, ok := live.backend.(interface{ dropConnection() bool })
	return ok && dropper.dropConnection()
}

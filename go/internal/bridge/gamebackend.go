package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gamehost"
)

// Attention methods and channels of the GABP attention extension. An open
// blocking item means the game logged something a caller must read and
// acknowledge before more native calls run.
const (
	attentionCurrent = "attention/current"
	attentionAck     = "attention/ack"
	attentionOpened  = "attention/opened"
	attentionUpdated = "attention/updated"
	attentionCleared = "attention/cleared"
)

// connectDialBound caps one games_connect's dial: a game still booting
// answers "no attachable endpoint yet" and ConnectWithPoll tries again.
const connectDialBound = 10 * time.Second

// gameBackend drives one game: gamehost launches or finds the process and
// a direct GABP connection carries the calls.
type gameBackend struct {
	gameID string
	spec   gamehost.Spec

	connectMu sync.Mutex // one dial at a time

	mu        sync.Mutex
	game      *gamehost.Game
	conn      *gabp.Conn
	tools     []gabp.Tool
	attention *attentionState
	stopping  bool
	closed    bool

	once  sync.Once
	ended chan struct{}
}

// attentionState is the connection's view of the game's open attention
// item, kept current by the attention events.
type attentionState struct {
	supported bool
	current   json.RawMessage // the item, nil when none is open
}

func newGameBackend(gameID string, spec gamehost.Spec) *gameBackend {
	spec.GameID = gameID
	return &gameBackend{gameID: gameID, spec: spec, ended: make(chan struct{})}
}

func (b *gameBackend) discovery() Discovery  { return wrapperDiscovery("rimgovernor-gamehost") }
func (b *gameBackend) done() <-chan struct{} { return b.ended }

// close drops the connection and leaves the game running.
func (b *gameBackend) close() error {
	b.mu.Lock()
	b.closed = true
	conn := b.conn
	b.conn = nil
	b.mu.Unlock()
	if conn != nil {
		_ = conn.Close()
	}
	b.once.Do(func() { close(b.ended) })
	return nil
}

func (b *gameBackend) lose() { b.once.Do(func() { close(b.ended) }) }

func (b *gameBackend) call(ctx context.Context, name string, arguments json.RawMessage) (json.RawMessage, error) {
	switch name {
	case "games_start":
		return b.start(ctx), nil
	case "games_stop":
		return b.stop(ctx), nil
	case "games_status":
		return b.status(), nil
	case "games_connect":
		return b.connect(ctx), nil
	case "games_tool_names":
		return b.toolNames(ctx, arguments), nil
	case "games_tool_detail":
		return b.toolDetail(ctx, arguments), nil
	case "games_call_tool":
		return b.callTool(ctx, arguments)
	case "games_get_attention":
		return b.getAttention(ctx), nil
	case "games_ack_attention":
		return b.ackAttention(ctx, arguments), nil
	}
	return nil, fmt.Errorf("%w: unknown call %s", ErrContract, name)
}

func (b *gameBackend) refuse(structured map[string]any, format string, args ...any) json.RawMessage {
	if structured == nil {
		structured = map[string]any{}
	}
	structured["gameId"] = b.gameID
	return receiptEnvelope(structured, true, fmt.Sprintf(format, args...))
}

// running is the live game this session knows or the one recorded under
// the state dir, or nil.
func (b *gameBackend) running() *gamehost.Game {
	b.mu.Lock()
	game := b.game
	b.mu.Unlock()
	if game != nil && game.Alive() {
		return game
	}
	attached, err := gamehost.Attach(b.spec.StateDir, b.gameID)
	if err != nil {
		return nil
	}
	b.mu.Lock()
	b.game = attached
	b.mu.Unlock()
	return attached
}

func (b *gameBackend) gameFields(game *gamehost.Game, fields map[string]any) map[string]any {
	fields["gameId"] = b.gameID
	if game != nil {
		fields["pid"] = game.PID()
		fields["generation"] = game.Generation()
	}
	return fields
}

// start attaches to a recorded live game or launches one. It never dials:
// ConnectWithPoll does, through games_connect.
func (b *gameBackend) start(ctx context.Context) json.RawMessage {
	if game := b.running(); game != nil {
		b.mu.Lock()
		connected := b.conn != nil
		b.mu.Unlock()
		return receiptEnvelope(b.gameFields(game, map[string]any{"status": "running", "attached": true, "gabpConnected": connected}), false)
	}
	if b.spec.Executable == "" {
		return b.refuse(nil, "no running game %q under %s and no launch executable configured", b.gameID, b.spec.StateDir)
	}
	game, err := gamehost.Launch(ctx, b.spec)
	if errors.Is(err, gamehost.ErrAlreadyRunning) {
		if game = b.running(); game != nil {
			return receiptEnvelope(b.gameFields(game, map[string]any{"status": "running", "attached": true}), false)
		}
	}
	if err != nil {
		return b.refuse(nil, "launch %q: %v", b.gameID, err)
	}
	b.mu.Lock()
	b.game = game
	b.stopping = false
	b.mu.Unlock()
	return receiptEnvelope(b.gameFields(game, map[string]any{"status": "started", "launched": true}), false)
}

// stop ends the recorded game's process tree. A game that is not running
// is already stopped.
func (b *gameBackend) stop(ctx context.Context) json.RawMessage {
	game := b.running()
	if game == nil {
		return receiptEnvelope(b.gameFields(nil, map[string]any{"status": "stopped", "stopped": false}), false)
	}
	b.mu.Lock()
	b.stopping = true
	conn := b.conn
	b.conn = nil
	b.mu.Unlock()
	if conn != nil {
		_ = conn.Close()
	}
	if err := game.Stop(ctx); err != nil {
		return b.refuse(b.gameFields(game, map[string]any{"status": "running"}), "stop %q: %v", b.gameID, err)
	}
	b.mu.Lock()
	if b.game == game {
		b.game = nil
	}
	b.mu.Unlock()
	return receiptEnvelope(b.gameFields(game, map[string]any{"status": "stopped", "stopped": true}), false)
}

func (b *gameBackend) status() json.RawMessage {
	game := b.running()
	b.mu.Lock()
	connected, tools := b.conn != nil, len(b.tools)
	b.mu.Unlock()
	status := "stopped"
	switch {
	case game != nil && connected:
		status = "connected"
	case game != nil:
		status = "running"
	}
	fields := b.gameFields(game, map[string]any{"status": status, "gabpConnected": connected})
	if connected {
		fields["toolCount"] = tools
	}
	return receiptEnvelope(fields, false)
}

// connect dials the running game's endpoint, once per connection. A game
// not yet listening refuses with "no attachable endpoint yet" so the
// caller's poll keeps waiting.
func (b *gameBackend) connect(ctx context.Context) json.RawMessage {
	b.connectMu.Lock()
	defer b.connectMu.Unlock()
	b.mu.Lock()
	if b.conn != nil {
		fields := map[string]any{"success": true, "status": "connected", "toolCount": len(b.tools)}
		game := b.game
		b.mu.Unlock()
		return receiptEnvelope(b.gameFields(game, fields), false)
	}
	b.mu.Unlock()
	game := b.running()
	if game == nil {
		return b.refuse(map[string]any{"status": "stopped"}, "game %q is not running; games_start launches it", b.gameID)
	}
	dialCtx, cancel := context.WithTimeout(ctx, connectDialBound)
	if deadline, ok := ctx.Deadline(); ok && time.Until(deadline) < connectDialBound+time.Second {
		cancel()
		dialCtx, cancel = context.WithDeadline(ctx, deadline.Add(-min(time.Second, time.Until(deadline)/2)))
	}
	defer cancel()
	state := &attentionState{}
	conn, err := gabp.Dial(dialCtx, game.Addr(), game.Token(), gabp.Options{
		ClientName: "rimgovernor", ClientVersion: "go-read-v1", MaxFrameBytes: maxResponseBytes + 1<<20,
		OnEvent: func(event gabp.Event) { b.attentionEvent(state, event) },
	})
	if err != nil {
		if !game.Alive() {
			return b.refuse(map[string]any{"status": "stopped"}, "game %q exited before its endpoint answered", b.gameID)
		}
		return b.refuse(map[string]any{"status": "running"}, "game %q has no attachable endpoint yet: %v", b.gameID, err)
	}
	tools, err := conn.ListTools(dialCtx)
	if err != nil {
		_ = conn.Close()
		return b.refuse(map[string]any{"status": "running"}, "game %q has no attachable endpoint yet: tools/list: %v", b.gameID, err)
	}
	welcome := conn.Welcome()
	var acknowledged json.RawMessage
	if slices.Contains(welcome.Capabilities.Methods, attentionCurrent) && slices.Contains(welcome.Capabilities.Methods, attentionAck) {
		state.supported = true
		var channels []string
		for _, channel := range []string{attentionOpened, attentionUpdated, attentionCleared} {
			if slices.Contains(welcome.Capabilities.Events, channel) {
				channels = append(channels, channel)
			}
		}
		if len(channels) > 0 {
			_ = conn.Subscribe(dialCtx, channels...)
		}
		if raw, err := conn.Call(dialCtx, attentionCurrent, struct{}{}); err == nil {
			b.setAttention(state, currentAttention(raw))
		}
		acknowledged = b.ackPreexisting(dialCtx, conn, state)
	}
	b.mu.Lock()
	if b.closed {
		b.mu.Unlock()
		_ = conn.Close()
		return b.refuse(nil, "session closed")
	}
	b.conn, b.tools, b.attention, b.game, b.stopping = conn, tools, state, game, false
	b.mu.Unlock()
	go b.watch(conn)
	fields := map[string]any{"success": true, "status": "connected", "toolCount": len(tools)}
	if acknowledged != nil {
		fields["acknowledgedAttention"] = acknowledged
	}
	return receiptEnvelope(b.gameFields(game, fields), false)
}

// ackPreexisting acknowledges a blocking attention item already open when
// the controller attaches: the game logged it while booting or loading a
// save, before any controller call it could be about, and left open it
// refuses every native call so the bot never starts. The item rides the
// connect receipt so the flight recorder keeps its text. Items opened
// after attach still block until a caller acknowledges them.
func (b *gameBackend) ackPreexisting(ctx context.Context, conn *gabp.Conn, state *attentionState) json.RawMessage {
	b.mu.Lock()
	item := state.current
	b.mu.Unlock()
	if blockingItem(item) == nil {
		return nil
	}
	var id struct {
		AttentionID string `json:"attentionId"`
	}
	if json.Unmarshal(item, &id) != nil || id.AttentionID == "" {
		return nil
	}
	raw, err := conn.Call(ctx, attentionAck, map[string]string{"attentionId": id.AttentionID})
	if err != nil {
		return nil
	}
	var result struct {
		Acknowledged     bool            `json:"acknowledged"`
		CurrentAttention json.RawMessage `json:"currentAttention"`
	}
	if json.Unmarshal(raw, &result) != nil || !result.Acknowledged {
		return nil
	}
	current := result.CurrentAttention
	if string(current) == "null" {
		current = nil
	}
	b.setAttention(state, current)
	return item
}

// watch ends the session when conn drops on its own. games_stop and close
// drop it deliberately and leave the session usable or already ended.
func (b *gameBackend) watch(conn *gabp.Conn) {
	<-conn.Done()
	b.mu.Lock()
	lost := b.conn == conn && !b.stopping
	if b.conn == conn {
		b.conn = nil
	}
	b.mu.Unlock()
	if lost {
		b.lose()
	}
}

func currentAttention(raw json.RawMessage) json.RawMessage {
	var body struct {
		Attention json.RawMessage `json:"attention"`
	}
	if json.Unmarshal(raw, &body) != nil || string(body.Attention) == "null" {
		return nil
	}
	return body.Attention
}

func (b *gameBackend) attentionEvent(state *attentionState, event gabp.Event) {
	switch event.Channel {
	case attentionOpened, attentionUpdated:
		if len(event.Payload) == 0 || string(event.Payload) == "null" {
			b.setAttention(state, nil)
			return
		}
		b.setAttention(state, append(json.RawMessage(nil), event.Payload...))
	case attentionCleared:
		b.setAttention(state, nil)
	}
}

func (b *gameBackend) setAttention(state *attentionState, item json.RawMessage) {
	b.mu.Lock()
	state.current = item
	b.mu.Unlock()
}

// live is the current connection and its attention state, or nil.
func (b *gameBackend) live() (*gabp.Conn, *attentionState) {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.conn, b.attention
}

func (b *gameBackend) notConnected(tool string) json.RawMessage {
	return b.refuse(map[string]any{"status": "disconnected", "tool": tool}, "Game %q is not connected via GABP. Use games_status to check it is running, then games_connect or games_start.", b.gameID)
}

// catalog is the connection's tool list, re-read when fresh is set (the
// native catalog can grow after a load).
func (b *gameBackend) catalog(ctx context.Context, conn *gabp.Conn, fresh bool) ([]gabp.Tool, error) {
	b.mu.Lock()
	tools := b.tools
	b.mu.Unlock()
	if !fresh && tools != nil {
		return tools, nil
	}
	tools, err := conn.ListTools(ctx)
	if err != nil {
		return nil, err
	}
	b.mu.Lock()
	if b.conn == conn {
		b.tools = tools
	}
	b.mu.Unlock()
	return tools, nil
}

func (b *gameBackend) toolNames(ctx context.Context, arguments json.RawMessage) json.RawMessage {
	var args struct {
		Query string `json:"query"`
	}
	_ = json.Unmarshal(arguments, &args)
	conn, _ := b.live()
	if conn == nil {
		return b.notConnected("")
	}
	tools, err := b.catalog(ctx, conn, true)
	if err != nil {
		return b.refuse(nil, "tools/list: %v", err)
	}
	query := strings.ToLower(strings.TrimSpace(args.Query))
	rows := []map[string]any{}
	for _, tool := range tools {
		if query != "" && !strings.Contains(strings.ToLower(tool.Name), query) {
			continue
		}
		rows = append(rows, map[string]any{"name": tool.Name, "gabpName": tool.Name})
	}
	return receiptEnvelope(map[string]any{"gameId": b.gameID, "tools": rows, "total": len(rows)}, false)
}

func (b *gameBackend) toolDetail(ctx context.Context, arguments json.RawMessage) json.RawMessage {
	var args struct {
		Tool string `json:"tool"`
	}
	_ = json.Unmarshal(arguments, &args)
	conn, _ := b.live()
	if conn == nil {
		return b.notConnected(args.Tool)
	}
	for _, fresh := range []bool{false, true} {
		tools, err := b.catalog(ctx, conn, fresh)
		if err != nil {
			return b.refuse(nil, "tools/list: %v", err)
		}
		for _, tool := range tools {
			if tool.Name != args.Tool {
				continue
			}
			detail := map[string]any{"gameId": b.gameID, "name": tool.Name, "gabpName": tool.Name, "description": tool.Description, "inputSchema": tool.InputSchema}
			if len(tool.OutputSchema) > 0 {
				detail["outputSchema"] = tool.OutputSchema
			}
			if len(tool.Tags) > 0 {
				detail["tags"] = tool.Tags
			}
			return receiptEnvelope(detail, false)
		}
	}
	return b.refuse(map[string]any{"requested": args.Tool}, "Tool '%s' not found for game '%s'.", args.Tool, b.gameID)
}

// bypassesAttention reports a tool the attention gate never blocks: one
// whose name carries the token "attention", so reading and acknowledging
// attention stays possible while an item is open.
func bypassesAttention(tool string) bool {
	parts := strings.FieldsFunc(strings.ToLower(tool), func(r rune) bool { return strings.ContainsRune("/._-: ", r) })
	return slices.Contains(parts, "attention")
}

// callTool runs one native tool. An open blocking attention item refuses
// the call unexecuted (status blocked_by_attention); a native error reply
// is a refusal carrying the error object; a call no answer came back for
// is an error.
func (b *gameBackend) callTool(ctx context.Context, arguments json.RawMessage) (json.RawMessage, error) {
	var args nativeArgument
	if err := json.Unmarshal(arguments, &args); err != nil || args.Tool == "" {
		return b.refuse(nil, "Missing required argument: tool"), nil
	}
	conn, attention := b.live()
	if conn == nil {
		return b.notConnected(args.Tool), nil
	}
	if !bypassesAttention(args.Tool) {
		b.mu.Lock()
		item := attention.current
		b.mu.Unlock()
		if blocked := blockingItem(item); blocked != nil {
			summary := ""
			if s := strings.TrimSpace(blocked.Summary); s != "" {
				summary = fmt.Sprintf(" Summary: %s.", s)
			}
			return b.refuse(map[string]any{"executed": false, "status": "blocked_by_attention", "tool": args.Tool, "attention": item},
				"Tool call %q for game %q was not executed because important game information requires acknowledgement.%s Review it with games_get_attention, acknowledge it with games_ack_attention, then retry the original call.", args.Tool, b.gameID, summary), nil
		}
	}
	raw, isError, err := conn.CallTool(ctx, args.Tool, args.Arguments)
	if err != nil {
		return nil, err
	}
	if len(raw) > maxResponseBytes {
		return nil, fmt.Errorf("%w: oversized native result", ErrContract)
	}
	structured := raw
	var object map[string]json.RawMessage
	if json.Unmarshal(raw, &object) != nil {
		structured = encode(map[string]json.RawMessage{"value": raw})
	}
	text := ""
	if value, ok := object["text"]; ok {
		_ = json.Unmarshal(value, &text)
	}
	if isError {
		if text == "" {
			var remote struct {
				Message string `json:"message"`
			}
			_ = json.Unmarshal(raw, &remote)
			text = "Tool error: " + remote.Message
		}
		return receiptEnvelope(structured, true, text), nil
	}
	return receiptEnvelope(structured, false, text), nil
}

type attentionItem struct {
	Blocking bool   `json:"blocking"`
	State    string `json:"state"`
	Summary  string `json:"summary"`
}

func blockingItem(raw json.RawMessage) *attentionItem {
	if len(raw) == 0 {
		return nil
	}
	var item attentionItem
	if json.Unmarshal(raw, &item) != nil || !item.Blocking || strings.EqualFold(item.State, "cleared") {
		return nil
	}
	return &item
}

func (b *gameBackend) getAttention(ctx context.Context) json.RawMessage {
	conn, attention := b.live()
	if conn == nil {
		return b.notConnected("games_get_attention")
	}
	if !attention.supported {
		return receiptEnvelope(map[string]any{"gameId": b.gameID, "supported": false, "attention": nil}, false)
	}
	raw, err := conn.Call(ctx, attentionCurrent, struct{}{})
	if err != nil {
		return b.refuse(nil, "attention/current: %v", err)
	}
	item := currentAttention(raw)
	b.setAttention(attention, item)
	return receiptEnvelope(map[string]any{"gameId": b.gameID, "supported": true, "attention": item}, false)
}

func (b *gameBackend) ackAttention(ctx context.Context, arguments json.RawMessage) json.RawMessage {
	var args attentionArgument
	_ = json.Unmarshal(arguments, &args)
	conn, attention := b.live()
	if conn == nil {
		return b.notConnected("games_ack_attention")
	}
	if !attention.supported {
		return receiptEnvelope(map[string]any{"gameId": b.gameID, "supported": false, "acknowledged": false, "attentionId": args.AttentionID}, false,
			fmt.Sprintf("Game '%s' does not advertise attention support.", b.gameID))
	}
	raw, err := conn.Call(ctx, attentionAck, map[string]string{"attentionId": args.AttentionID})
	if err != nil {
		return b.refuse(nil, "Failed to acknowledge attention '%s' for game '%s': %v", args.AttentionID, b.gameID, err)
	}
	var result struct {
		Acknowledged     bool            `json:"acknowledged"`
		AttentionID      string          `json:"attentionId"`
		CurrentAttention json.RawMessage `json:"currentAttention"`
	}
	if err := json.Unmarshal(raw, &result); err != nil {
		return b.refuse(nil, "attention/ack: invalid reply: %v", err)
	}
	current := result.CurrentAttention
	if string(current) == "null" {
		current = nil
	}
	b.setAttention(attention, current)
	fields := map[string]any{"gameId": b.gameID, "supported": true, "acknowledged": result.Acknowledged, "attentionId": result.AttentionID, "currentAttention": current}
	message := fmt.Sprintf("Acknowledged attention '%s' for game '%s'.", args.AttentionID, b.gameID)
	if !result.Acknowledged {
		message = fmt.Sprintf("Attention '%s' was not acknowledged for game '%s'.", args.AttentionID, b.gameID)
	}
	return receiptEnvelope(fields, !result.Acknowledged, message)
}

// LaunchSpecFromConfig reads games.<gameID> of configDir/config.json (the
// launch description the setup tooling writes: target, workingDir, args,
// env) into a gamehost.Spec whose state dir is configDir, so every
// controller using that configuration finds the same running game.
func LaunchSpecFromConfig(configDir, gameID string) (gamehost.Spec, error) {
	spec := gamehost.Spec{GameID: gameID, StateDir: configDir}
	data, err := os.ReadFile(filepath.Join(configDir, "config.json"))
	if err != nil {
		return spec, fmt.Errorf("launch config: %w", err)
	}
	var config struct {
		Games map[string]struct {
			Target     string            `json:"target"`
			WorkingDir string            `json:"workingDir"`
			Args       []string          `json:"args"`
			Env        map[string]string `json:"env"`
		} `json:"games"`
	}
	if err := json.Unmarshal(data, &config); err != nil {
		return spec, fmt.Errorf("launch config %s: %w", filepath.Join(configDir, "config.json"), err)
	}
	game, ok := config.Games[gameID]
	if !ok {
		return spec, fmt.Errorf("launch config %s: no games.%s", filepath.Join(configDir, "config.json"), gameID)
	}
	spec.Executable, spec.WorkingDir, spec.Args, spec.Env = game.Target, game.WorkingDir, game.Args, game.Env
	return spec, nil
}

// dropConnection closes the game connection as a crash would, without a
// games_stop: the watch goroutine then ends the session.
func (b *gameBackend) dropConnection() bool {
	b.mu.Lock()
	conn := b.conn
	b.mu.Unlock()
	if conn == nil {
		return false
	}
	_ = conn.Close()
	return true
}

// ConfiguredProcess is the ProcessConfig for gameID under configDir, its
// launch spec read by LaunchSpecFromConfig.
func ConfiguredProcess(configDir, gameID string, timeout time.Duration) (ProcessConfig, error) {
	launch, err := LaunchSpecFromConfig(configDir, gameID)
	if err != nil {
		return ProcessConfig{}, err
	}
	return ProcessConfig{GameID: gameID, Launch: launch, Timeout: timeout}, nil
}

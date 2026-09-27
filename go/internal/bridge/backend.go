package bridge

import (
	"context"
	"encoding/json"
	"sync"
)

// wrapperTools are the calls a session answers. The names are the labels
// every receipt, flight-recorder row and transcript carries; they name what
// the call does (start, connect, call a native tool, ...), not a transport.
var wrapperTools = []string{
	"games_start", "games_stop", "games_status", "games_connect",
	"games_tool_names", "games_tool_detail", "games_call_tool",
	"games_get_attention", "games_ack_attention",
}

// backend answers wrapper calls for one session. call returns the receipt
// envelope (a wireResult's JSON) or an error when no answer arrived at all;
// a refusal is an envelope with isError set, never an error.
type backend interface {
	discovery() Discovery
	call(ctx context.Context, name string, arguments json.RawMessage) (json.RawMessage, error)
	// done is closed once the session is lost: its game connection dropped
	// without a games_stop, or the backend was closed.
	done() <-chan struct{}
	close() error
}

type backendFactory func(ctx context.Context) (backend, error)

// wireResult is the receipt envelope: structured content plus any text a
// refusal carries. Its JSON layout is the one recorded transcripts hold.
type wireResult struct {
	Content    []wireContent   `json:"content"`
	Structured json.RawMessage `json:"structuredContent,omitempty"`
	IsError    bool            `json:"isError,omitempty"`
}

type wireContent struct {
	Type string `json:"type"`
	Text string `json:"text"`
}

// receiptEnvelope encodes a receipt. structured is marshalled unless it is
// already raw JSON.
func receiptEnvelope(structured any, isError bool, text ...string) json.RawMessage {
	result := wireResult{Content: []wireContent{}, IsError: isError}
	for _, t := range text {
		if t != "" {
			result.Content = append(result.Content, wireContent{Type: "text", Text: t})
		}
	}
	switch value := structured.(type) {
	case nil:
	case json.RawMessage:
		result.Structured = value
	default:
		result.Structured = encode(value)
	}
	return encode(result)
}

// handlerBackend answers every call from handle; Replay and tests use it.
type handlerBackend struct {
	catalog Discovery
	handle  func(ctx context.Context, name string, arguments json.RawMessage) (json.RawMessage, error)
	once    sync.Once
	ended   chan struct{}
}

func newHandlerBackend(catalog Discovery, handle func(context.Context, string, json.RawMessage) (json.RawMessage, error)) *handlerBackend {
	return &handlerBackend{catalog: catalog, handle: handle, ended: make(chan struct{})}
}

func (b *handlerBackend) discovery() Discovery { return b.catalog }
func (b *handlerBackend) call(ctx context.Context, name string, arguments json.RawMessage) (json.RawMessage, error) {
	return b.handle(ctx, name, arguments)
}
func (b *handlerBackend) done() <-chan struct{} { return b.ended }
func (b *handlerBackend) close() error {
	b.once.Do(func() { close(b.ended) })
	return nil
}

// wrapperDiscovery is the catalog of a session that answers every wrapper.
func wrapperDiscovery(server string) Discovery {
	d := Discovery{ProtocolVersion: "gabp/1", ServerName: server, ServerVersion: "1"}
	for _, name := range wrapperTools {
		d.Tools = append(d.Tools, Tool{Name: name, InputSchema: json.RawMessage(`{"type":"object"}`)})
	}
	return d
}

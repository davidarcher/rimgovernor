package bridge

import (
	"context"
	"encoding/json"
	"sync"
	"testing"
)

const emptySchema = `{"type":"object","properties":{},"additionalProperties":false}`

// callResult is a fake session's answer to one wrapper call.
type callResult struct {
	Content           []content
	StructuredContent any
	IsError           bool
}

type content interface{ text() string }

type textContent struct{ Text string }

func (t *textContent) text() string { return t.Text }

func (r *callResult) receipt() json.RawMessage {
	var texts []string
	for _, c := range r.Content {
		texts = append(texts, c.text())
	}
	return receiptEnvelope(r.StructuredContent, r.IsError, texts...)
}

func structured(raw string) *callResult {
	return &callResult{StructuredContent: json.RawMessage(raw)}
}

// testServer is a fake session answering the wrapper calls in process;
// handler answers games_call_tool.
type testServer struct {
	mu             sync.Mutex
	calls          []nativeArgument
	handler        func(context.Context, nativeArgument) (*callResult, error)
	connectResult  *callResult
	connectHandler func() (*callResult, error)
	connectArgs    json.RawMessage
	detailResult   *callResult
	schema         string
	sessions       []*handlerBackend
	starts         int
	details        int
	// tools, when set, replaces the session's catalog.
	tools []string
}

func (s *testServer) answer(ctx context.Context, name string, arguments json.RawMessage) (*callResult, error) {
	switch name {
	case "games_start":
		s.mu.Lock()
		s.starts++
		s.mu.Unlock()
		return structured(`{"gabpConnected":true}`), nil
	case "games_connect":
		s.mu.Lock()
		s.connectArgs = append(json.RawMessage(nil), arguments...)
		s.mu.Unlock()
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
		if err := json.Unmarshal(arguments, &args); err != nil {
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
}

func (s *testServer) factory(t *testing.T) backendFactory {
	return func(context.Context) (backend, error) {
		catalog := wrapperDiscovery("test")
		if s.tools != nil {
			catalog.Tools = nil
			for _, name := range s.tools {
				catalog.Tools = append(catalog.Tools, Tool{Name: name})
			}
		}
		session := newHandlerBackend(catalog, func(ctx context.Context, name string, arguments json.RawMessage) (json.RawMessage, error) {
			result, err := s.answer(ctx, name, arguments)
			if err != nil {
				return nil, err
			}
			return result.receipt(), nil
		})
		s.mu.Lock()
		s.sessions = append(s.sessions, session)
		s.mu.Unlock()
		t.Cleanup(func() { _ = session.close() })
		return session, nil
	}
}

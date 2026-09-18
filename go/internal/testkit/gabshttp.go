package testkit

import (
	"context"
	"io"
	"net/http"
	"path/filepath"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/jsonrpc"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// GABSHTTP serves GABS's HTTP surface in front of an mcp.Server: GET /health,
// and POST /mcp answering one JSON-RPC message per request with its reply as
// the body (204 for a notification), each request handled concurrently as
// GABS does. Tests hand it to httptest or ListenAndServe to stand in for
// `gabs server http` (bridge.Open's transport).
type GABSHTTP struct {
	connection mcp.Connection
	mu         sync.Mutex
	waiting    map[any]chan jsonrpc.Message
}

func NewGABSHTTP(ctx context.Context, server *mcp.Server) (*GABSHTTP, error) {
	serverTransport, clientTransport := mcp.NewInMemoryTransports()
	if _, err := server.Connect(ctx, serverTransport, nil); err != nil {
		return nil, err
	}
	connection, err := clientTransport.Connect(ctx)
	if err != nil {
		return nil, err
	}
	f := &GABSHTTP{connection: connection, waiting: map[any]chan jsonrpc.Message{}}
	go func() {
		for {
			message, err := connection.Read(ctx)
			if err != nil {
				return
			}
			response, ok := message.(*jsonrpc.Response)
			if !ok {
				continue
			}
			f.mu.Lock()
			ch := f.waiting[response.ID.Raw()]
			delete(f.waiting, response.ID.Raw())
			f.mu.Unlock()
			if ch != nil {
				ch <- response
			}
		}
	}()
	return f, nil
}

func (f *GABSHTTP) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	switch {
	case r.URL.Path == "/health" && r.Method == http.MethodGet:
		w.WriteHeader(http.StatusOK)
		_, _ = io.WriteString(w, `{"status":"ok"}`)
	case r.URL.Path == "/mcp" && r.Method == http.MethodPost:
		body, err := io.ReadAll(r.Body)
		if err != nil {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		message, err := jsonrpc.DecodeMessage(body)
		if err != nil {
			w.WriteHeader(http.StatusBadRequest)
			return
		}
		request, ok := message.(*jsonrpc.Request)
		if !ok || !request.ID.IsValid() {
			_ = f.connection.Write(r.Context(), message)
			w.WriteHeader(http.StatusNoContent)
			return
		}
		ch := make(chan jsonrpc.Message, 1)
		f.mu.Lock()
		f.waiting[request.ID.Raw()] = ch
		f.mu.Unlock()
		if err := f.connection.Write(r.Context(), message); err != nil {
			w.WriteHeader(http.StatusInternalServerError)
			return
		}
		reply := <-ch
		encoded, err := jsonrpc.EncodeMessage(reply)
		if err != nil {
			w.WriteHeader(http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(encoded)
	default:
		w.WriteHeader(http.StatusMethodNotAllowed)
	}
}

// GABSHTTPMain is the TestMain hook for a test binary standing in for GABS:
// invoked as `server http --addr A --configDir D ...` it serves server on A
// and never returns, except that a configDir whose base name is
// "unresponsive" sleeps forever without listening. Any other argument
// vector returns false so the tests run normally.
func GABSHTTPMain(args []string, server func() *mcp.Server) bool {
	if len(args) <= 6 || args[1] != "server" || args[2] != "http" {
		return false
	}
	if filepath.Base(args[6]) == "unresponsive" {
		for {
			time.Sleep(time.Hour)
		}
	}
	fake, err := NewGABSHTTP(context.Background(), server())
	if err != nil {
		return true
	}
	_ = http.ListenAndServe(args[4], fake)
	return true
}

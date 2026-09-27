// Package gabptest is an in-process fake GABP server (RimBridgeServer's
// side of gabp/1) for tests of gabp and its callers.
package gabptest

import (
	"bufio"
	"encoding/json"
	"net"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
)

// Request is one request the server received. Reply answers it, from any
// goroutine and in any order; a second Reply is ignored.
type Request struct {
	Method string
	Params json.RawMessage
	Raw    []byte // the frame body exactly as it arrived
	sc     *ServerConn
	id     string
	once   sync.Once
}

// Conn is the connection the request arrived on.
func (r *Request) Conn() *ServerConn { return r.sc }

// Reply sends result (marshalled) or, when rerr is non-nil, an error response.
func (r *Request) Reply(result any, rerr *gabp.RemoteError) {
	r.once.Do(func() {
		m := gabp.Message{V: gabp.Version, ID: r.id, Type: gabp.TypeResponse, Error: rerr}
		if rerr == nil {
			b, err := json.Marshal(result)
			if err != nil {
				panic(err)
			}
			m.Result = b
		}
		r.sc.Send(m)
	})
}

// Server accepts connections on a loopback port. session/hello is answered
// automatically (refused with code -32001 on a token mismatch) and
// tools/list from Tools; every other request goes to Handle, which must
// Reply eventually (or never, to simulate a hang).
type Server struct {
	Token   string
	Listen  string         // listen address; default 127.0.0.1:0
	Welcome map[string]any // extra welcome fields; agentId etc. default
	Tools   []map[string]any
	Handle  func(*Request)

	ln    net.Listener
	mu    sync.Mutex
	conns []*ServerConn
	reqs  []*Request
}

// ServerConn is one accepted client connection.
type ServerConn struct {
	nc net.Conn
	mu sync.Mutex
}

// Send writes one message frame.
func (sc *ServerConn) Send(m gabp.Message) {
	b, _ := json.Marshal(m)
	sc.WriteRaw(b)
}

// WriteRaw writes body as one frame, unvalidated.
func (sc *ServerConn) WriteRaw(body []byte) {
	sc.mu.Lock()
	defer sc.mu.Unlock()
	_ = gabp.WriteFrame(sc.nc, body)
}

// Close drops the connection.
func (sc *ServerConn) Close() { _ = sc.nc.Close() }

// Start listens on 127.0.0.1:0 and closes on test cleanup.
func Start(t testing.TB, s *Server) *Server {
	t.Helper()
	addr := s.Listen
	if addr == "" {
		addr = "127.0.0.1:0"
	}
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		t.Fatal(err)
	}
	s.ln = ln
	t.Cleanup(s.Close)
	go s.accept()
	return s
}

// Addr is the listen address.
func (s *Server) Addr() string { return s.ln.Addr().String() }

// Close stops listening and drops every connection.
func (s *Server) Close() {
	_ = s.ln.Close()
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, c := range s.conns {
		c.Close()
	}
}

// Conns returns the accepted connections so far.
func (s *Server) Conns() []*ServerConn {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]*ServerConn(nil), s.conns...)
}

// Requests returns every request received so far, handshake included.
func (s *Server) Requests() []*Request {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]*Request(nil), s.reqs...)
}

func (s *Server) accept() {
	for {
		nc, err := s.ln.Accept()
		if err != nil {
			return
		}
		sc := &ServerConn{nc: nc}
		s.mu.Lock()
		s.conns = append(s.conns, sc)
		s.mu.Unlock()
		go s.serve(sc)
	}
}

func (s *Server) serve(sc *ServerConn) {
	r := bufio.NewReader(sc.nc)
	for {
		body, err := gabp.ReadFrame(r, 0)
		if err != nil {
			sc.Close()
			return
		}
		var m gabp.Message
		if json.Unmarshal(body, &m) != nil || m.Type != gabp.TypeRequest {
			continue
		}
		req := &Request{Method: m.Method, Params: m.Params, Raw: body, sc: sc, id: m.ID}
		s.mu.Lock()
		s.reqs = append(s.reqs, req)
		s.mu.Unlock()
		switch m.Method {
		case gabp.MethodSessionHello:
			var p struct {
				Token string `json:"token"`
			}
			_ = json.Unmarshal(m.Params, &p)
			if p.Token != s.Token {
				req.Reply(nil, &gabp.RemoteError{Code: -32001, Message: "invalid token"})
				continue
			}
			w := map[string]any{
				"agentId":       "agent-1",
				"app":           map[string]any{"name": "RimWorld", "version": "1.6"},
				"capabilities":  map[string]any{"methods": []string{gabp.MethodToolsList, gabp.MethodToolsCall}},
				"schemaVersion": "1.0",
			}
			for k, v := range s.Welcome {
				w[k] = v
			}
			req.Reply(w, nil)
		case gabp.MethodToolsList:
			tools := s.Tools
			if tools == nil {
				tools = []map[string]any{}
			}
			req.Reply(map[string]any{"tools": tools}, nil)
		default:
			if s.Handle == nil {
				req.Reply(nil, &gabp.RemoteError{Code: -32601, Message: "method not found"})
				continue
			}
			go s.Handle(req)
		}
	}
}

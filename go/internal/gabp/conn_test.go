package gabp_test

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
)

// Deadlines here are hang guards, not budgets.
func ctxT(t *testing.T) context.Context {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)
	return ctx
}

func dial(t *testing.T, s *gabptest.Server, opts gabp.Options) *gabp.Conn {
	t.Helper()
	c, err := gabp.Dial(ctxT(t), s.Addr(), s.Token, opts)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	return c
}

func TestHandshake(t *testing.T) {
	s := gabptest.Start(t, &gabptest.Server{Token: "tok", Welcome: map[string]any{
		"serverInfo": map[string]any{"name": "RimGovernor.Host", "version": "2.0"},
	}})
	c := dial(t, s, gabp.Options{ClientVersion: "1.2"})
	w := c.Welcome()
	if w.AgentID != "agent-1" || len(w.Capabilities.Methods) != 2 || w.ServerInfo == nil || w.ServerInfo.Version != "2.0" {
		t.Fatalf("welcome %+v", w)
	}
	var hello map[string]any
	_ = json.Unmarshal(s.Requests()[0].Params, &hello)
	for _, k := range []string{"token", "bridgeVersion", "platform", "launchId", "clientInfo"} {
		if hello[k] == nil {
			t.Errorf("hello lacks %s: %v", k, hello)
		}
	}
	if !strings.Contains(string(s.Requests()[0].Raw), `"v":"gabp/1"`) {
		t.Errorf("envelope %s", s.Requests()[0].Raw)
	}
}

func TestDialRetriesUntilListening(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	addr := ln.Addr().String()
	ln.Close()
	done := make(chan error, 1)
	go func() {
		c, err := gabp.Dial(ctxT(t), addr, "tok", gabp.Options{BackoffMin: 10 * time.Millisecond})
		if err == nil {
			c.Close()
		}
		done <- err
	}()
	time.Sleep(100 * time.Millisecond)
	gabptest.Start(t, &gabptest.Server{Token: "tok", Listen: addr})
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestDialGivesUpAtDeadline(t *testing.T) {
	ln, _ := net.Listen("tcp", "127.0.0.1:0")
	addr := ln.Addr().String()
	ln.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	if _, err := gabp.Dial(ctx, addr, "x", gabp.Options{BackoffMin: 10 * time.Millisecond}); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("err %v", err)
	}
}

func TestBadTokenRefused(t *testing.T) {
	s := gabptest.Start(t, &gabptest.Server{Token: "right"})
	_, err := gabp.Dial(ctxT(t), s.Addr(), "wrong", gabp.Options{})
	var re *gabp.RemoteError
	if !errors.As(err, &re) || re.Code != -32001 {
		t.Fatalf("err %v", err)
	}
}

func TestConcurrentOutOfOrder(t *testing.T) {
	var mu sync.Mutex
	var held []*gabptest.Request
	const n = 8
	all := make(chan struct{})
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) {
		mu.Lock()
		held = append(held, r)
		if len(held) == n {
			close(all)
		}
		mu.Unlock()
	}})
	c := dial(t, s, gabp.Options{})
	var wg sync.WaitGroup
	for i := range n {
		wg.Add(1)
		go func() {
			defer wg.Done()
			res, isErr, err := c.CallTool(ctxT(t), "echo", json.RawMessage(fmt.Sprintf(`{"i":%d}`, i)))
			var got struct{ Parameters struct{ I int } }
			if err != nil || isErr || json.Unmarshal(res, &got) != nil || got.Parameters.I != i {
				t.Errorf("call %d: %s %v %v", i, res, isErr, err)
			}
		}()
	}
	<-all
	for i := n - 1; i >= 0; i-- { // answer in reverse
		held[i].Reply(json.RawMessage(held[i].Params), nil)
	}
	wg.Wait()
}

func TestToolErrorAndIsError(t *testing.T) {
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) {
		var p struct{ Name string }
		_ = json.Unmarshal(r.Params, &p)
		if p.Name == "refuse" {
			r.Reply(nil, &gabp.RemoteError{Code: 7, Message: "no"})
			return
		}
		r.Reply(map[string]any{"isError": true, "detail": "x"}, nil)
	}})
	c := dial(t, s, gabp.Options{})
	res, isErr, err := c.CallTool(ctxT(t), "refuse", nil)
	if err != nil || !isErr || !strings.Contains(string(res), `"code":7`) {
		t.Fatalf("refuse: %s %v %v", res, isErr, err)
	}
	res, isErr, err = c.CallTool(ctxT(t), "flag", nil)
	if err != nil || !isErr || !strings.Contains(string(res), "detail") {
		t.Fatalf("flag: %s %v %v", res, isErr, err)
	}
}

func TestListToolsNormalisesParameters(t *testing.T) {
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Tools: []map[string]any{
		{"name": "a", "inputSchema": map[string]any{"type": "object", "x": 1}},
		{"name": "b", "parameters": []map[string]any{{"name": "n", "type": "Int32", "required": true}, {"name": "s", "type": "String"}}},
	}})
	tools, err := dial(t, s, gabp.Options{}).ListTools(ctxT(t))
	if err != nil || len(tools) != 2 {
		t.Fatal(tools, err)
	}
	if !strings.Contains(string(tools[0].InputSchema), `"x":1`) {
		t.Errorf("a: %s", tools[0].InputSchema)
	}
	var sch struct {
		Properties map[string]struct{ Type string }
		Required   []string
	}
	_ = json.Unmarshal(tools[1].InputSchema, &sch)
	if sch.Properties["n"].Type != "integer" || sch.Properties["s"].Type != "string" || len(sch.Required) != 1 {
		t.Errorf("b: %s", tools[1].InputSchema)
	}
}

func TestCancelKeepsConn(t *testing.T) {
	var first sync.Once
	var hung *gabptest.Request
	got := make(chan struct{})
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) {
		isFirst := false
		first.Do(func() { isFirst = true; hung = r; close(got) })
		if !isFirst {
			r.Reply("ok", nil)
		}
	}})
	c := dial(t, s, gabp.Options{})
	ctx, cancel := context.WithCancel(context.Background())
	errc := make(chan error, 1)
	go func() { _, _, err := c.CallTool(ctx, "hang", nil); errc <- err }()
	<-got
	cancel()
	if err := <-errc; !errors.Is(err, context.Canceled) {
		t.Fatalf("err %v", err)
	}
	hung.Reply("late", nil) // dropped
	res, _, err := c.CallTool(ctxT(t), "next", nil)
	if err != nil || string(res) != `"ok"` {
		t.Fatalf("after cancel: %s %v", res, err)
	}
}

func TestDisconnect(t *testing.T) {
	got := make(chan struct{})
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) { close(got) }})
	c := dial(t, s, gabp.Options{})
	errc := make(chan error, 1)
	go func() { _, _, err := c.CallTool(ctxT(t), "hang", nil); errc <- err }()
	<-got
	s.Conns()[0].Close()
	if err := <-errc; !errors.Is(err, gabp.ErrDisconnected) {
		t.Fatalf("pending: %v", err)
	}
	<-c.Done()
	if !errors.Is(c.Err(), gabp.ErrDisconnected) {
		t.Fatal(c.Err())
	}
	if _, err := c.Call(ctxT(t), "x", nil); !errors.Is(err, gabp.ErrDisconnected) {
		t.Fatalf("after: %v", err)
	}
}

func TestNonASCIIEscaped(t *testing.T) {
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) { r.Reply(r.Params, nil) }})
	c := dial(t, s, gabp.Options{})
	res, _, err := c.CallTool(ctxT(t), "echo", json.RawMessage(`{"s":"café 🍞"}`))
	if err != nil {
		t.Fatal(err)
	}
	raw := s.Requests()[1].Raw
	for _, b := range raw {
		if b >= 0x80 {
			t.Fatalf("non-ASCII byte on the wire: %s", raw)
		}
	}
	if !bytes.Contains(raw, []byte(`caf`+"\\u00e9 \\ud83c\\udf5e")) {
		t.Errorf("wire %s", raw)
	}
	var back struct{ Parameters struct{ S string } }
	_ = json.Unmarshal(res, &back)
	if back.Parameters.S != "café 🍞" {
		t.Errorf("round trip %q", back.Parameters.S)
	}
}

func TestOversizedFrame(t *testing.T) {
	got := make(chan *gabptest.Request, 1)
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) { got <- r }})
	c := dial(t, s, gabp.Options{MaxFrameBytes: 4096})
	errc := make(chan error, 1)
	go func() { _, _, err := c.CallTool(ctxT(t), "big", nil); errc <- err }()
	(<-got).Reply(strings.Repeat("x", 8192), nil)
	err := <-errc
	if !errors.Is(err, gabp.ErrDisconnected) || !errors.Is(c.Err(), gabp.ErrFrameTooLarge) {
		t.Fatalf("err %v / %v", err, c.Err())
	}
}

func TestEvents(t *testing.T) {
	evs := make(chan gabp.Event, 1)
	s := gabptest.Start(t, &gabptest.Server{Token: "t", Handle: func(r *gabptest.Request) {
		r.Reply(map[string]any{}, nil)
		r.Conn().Send(gabp.Message{V: gabp.Version, ID: "e1", Type: gabp.TypeEvent, Channel: "system/log", Seq: 3, Payload: json.RawMessage(`{"m":1}`)})
	}})
	c := dial(t, s, gabp.Options{OnEvent: func(e gabp.Event) { evs <- e }})
	if err := c.Subscribe(ctxT(t), "system/log"); err != nil {
		t.Fatal(err)
	}
	if e := <-evs; e.Channel != "system/log" || e.Seq != 3 || string(e.Payload) != `{"m":1}` {
		t.Fatalf("%+v", e)
	}
}

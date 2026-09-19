package bridge

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

func TestConnectWithPollRetriesStartupRefusal(t *testing.T) {
	calls := 0
	s := &testServer{connectHandler: func() (*mcp.CallToolResult, error) {
		calls++
		if calls == 1 {
			r := structured(`{"error":"listener not ready"}`)
			r.IsError = true
			return r, nil
		}
		return structured(`{"connected":true}`), nil
	}}
	c := testClient(t, s, time.Second)
	if _, err := c.ConnectWithPoll(context.Background(), Result{}); err != nil {
		t.Fatal(err)
	}
	if calls != 2 || strings.Contains(string(s.connectArgs), "forceTakeover") {
		t.Fatalf("calls=%d args=%s", calls, s.connectArgs)
	}
}

func TestConnectWithPollPreservesForeignOwnership(t *testing.T) {
	calls := 0
	s := &testServer{connectHandler: func() (*mcp.CallToolResult, error) {
		calls++
		return structured(`{"foreignOwner":true,"message":"owned by peer"}`), nil
	}}
	c := testClient(t, s, time.Second)
	_, err := c.ConnectWithPoll(context.Background(), Result{})
	if !errors.Is(err, ErrRefused) || !strings.Contains(err.Error(), "owned by peer") || calls != 1 {
		t.Fatalf("calls=%d error=%v", calls, err)
	}
}

func TestConnectWithPollCancelsRetry(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	s := &testServer{connectHandler: func() (*mcp.CallToolResult, error) {
		cancel()
		r := structured(`{"error":"listener not ready"}`)
		r.IsError = true
		return r, nil
	}}
	c := testClient(t, s, time.Second)
	if _, err := c.ConnectWithPoll(ctx, Result{}); !errors.Is(err, context.Canceled) {
		t.Fatalf("error=%v", err)
	}
}

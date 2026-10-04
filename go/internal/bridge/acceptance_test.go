package bridge

import (
	"context"
	"errors"
	"strings"
	"testing"
)

func TestConnectWithPollRetriesStartupRefusal(t *testing.T) {
	calls := 0
	s := &testServer{connectHandler: func() (*callResult, error) {
		calls++
		if calls == 1 {
			r := structured(`{"error":"listener not ready"}`)
			r.IsError = true
			return r, nil
		}
		return structured(`{"connected":true}`), nil
	}}
	c := testClient(t, s, testBudget)
	if _, err := c.ConnectWithPoll(context.Background(), Result{}); err != nil {
		t.Fatal(err)
	}
	if calls != 2 || strings.Contains(string(s.connectArgs), "forceTakeover") {
		t.Fatalf("calls=%d args=%s", calls, s.connectArgs)
	}
}

func TestConnectWithPollWaitsForUnpublishedStartupEndpoint(t *testing.T) {
	if testing.Short() {
		t.Skip("slow: runs under cmd/test -full and nightly")
	}
	calls := 0
	s := &testServer{connectHandler: func() (*callResult, error) {
		calls++
		if calls <= 6 {
			r := structured(`{"error":"runtime claim carries no attachable endpoint yet"}`)
			r.IsError = true
			return r, nil
		}
		return structured(`{"connected":true}`), nil
	}}
	c := testClient(t, s, testBudget)
	if _, err := c.ConnectWithPoll(context.Background(), Result{}); err != nil {
		t.Fatal(err)
	}
	if calls != 7 || strings.Contains(string(s.connectArgs), "forceTakeover") {
		t.Fatalf("calls=%d args=%s", calls, s.connectArgs)
	}
}

func TestConnectWithPollCancelsRetry(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	s := &testServer{connectHandler: func() (*callResult, error) {
		cancel()
		r := structured(`{"error":"listener not ready"}`)
		r.IsError = true
		return r, nil
	}}
	c := testClient(t, s, testBudget)
	if _, err := c.ConnectWithPoll(ctx, Result{}); !errors.Is(err, context.Canceled) {
		t.Fatalf("error=%v", err)
	}
}

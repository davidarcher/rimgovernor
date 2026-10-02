package main

import (
	"bytes"
	"context"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"
)

// reattachFake loses its session on demand and fails a configurable number
// of reattach attempts before succeeding.
type reattachFake struct {
	mu       sync.Mutex
	lost     chan struct{}
	failures int
	attempts int
	attached chan struct{}
	// closedGame makes GameClosed report a game the player closed.
	closedGame bool
}

func newReattachFake(failures int) *reattachFake {
	return &reattachFake{lost: make(chan struct{}), failures: failures, attached: make(chan struct{}, 8)}
}
func (f *reattachFake) Disconnected() <-chan struct{} {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.lost
}
func (f *reattachFake) lose() {
	f.mu.Lock()
	defer f.mu.Unlock()
	close(f.lost)
}
func (f *reattachFake) GameClosed(context.Context) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.closedGame
}
func (f *reattachFake) Reattach(ctx context.Context) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.attempts++
	if f.attempts <= f.failures {
		return errors.New("game still letting go")
	}
	f.lost = make(chan struct{})
	f.attached <- struct{}{}
	return nil
}

func TestSuperviseBridgeReattachesWithBackoffUntilContextEnds(t *testing.T) {
	fake := newReattachFake(2)
	var out bytes.Buffer
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		superviseBridge(ctx, fake, &out, func() { t.Error("a crashed game ended the service") })
	}()
	fake.lose()
	select {
	case <-fake.attached:
	case <-time.After(10 * time.Second):
		t.Fatal("never reattached")
	}
	fake.mu.Lock()
	attempts := fake.attempts
	fake.mu.Unlock()
	if attempts != 3 {
		t.Fatalf("attempts: %d", attempts)
	}
	// A second loss is supervised the same way, with backoff reset.
	fake.lose()
	select {
	case <-fake.attached:
	case <-time.After(10 * time.Second):
		t.Fatal("second loss not reattached")
	}
	cancel()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("supervisor did not stop")
	}
	log := out.String()
	if strings.Count(log, "game session lost") != 2 || !strings.Contains(log, "attempt 2 failed") || !strings.Contains(log, "reattached after 3 attempt(s)") || !strings.Contains(log, "reattached after 1 attempt(s)") {
		t.Fatal(log)
	}
}

// A game the player closed ends the service instead of being relaunched.
func TestSuperviseBridgeStopsWhenThePlayerClosesTheGame(t *testing.T) {
	fake := newReattachFake(0)
	fake.closedGame = true
	var out bytes.Buffer
	closed := make(chan struct{})
	done := make(chan struct{})
	go func() { defer close(done); superviseBridge(context.Background(), fake, &out, func() { close(closed) }) }()
	fake.lose()
	select {
	case <-closed:
	case <-time.After(10 * time.Second):
		t.Fatal("service not stopped")
	}
	<-done
	fake.mu.Lock()
	attempts := fake.attempts
	fake.mu.Unlock()
	if attempts != 0 || !strings.Contains(out.String(), "the game was closed") {
		t.Fatalf("attempts %d: %s", attempts, out.String())
	}
}

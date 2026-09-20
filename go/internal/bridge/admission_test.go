package bridge

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// TestEveryReviewedMethodHasAnAdmissionClass pins the class table to the
// allowlist: a new typed adapter must say which slots it competes for.
func TestEveryReviewedMethodHasAnAdmissionClass(t *testing.T) {
	for name := range reviewedNativeMethods {
		if _, ok := nativeAdmissionClass[name]; !ok {
			t.Errorf("%s is reviewed but has no admission class", name)
		}
	}
	for name := range nativeAdmissionClass {
		if !reviewedNativeMethods[name] {
			t.Errorf("%s has an admission class but is not reviewed", name)
		}
	}
	for name, want := range map[string]AdmissionClass{
		"rimgovernor/clock_renew":              AdmissionControl,
		"rimgovernor/clock_pause":              AdmissionControl,
		"rimgovernor/operations_execute":       AdmissionControl,
		"rimgovernor/observations_read_bundle": AdmissionObservation,
		"rimgovernor/observations_list_pawns":  AdmissionObservation,
		"rimgovernor/presentation_read_frame":  AdmissionMedia,
		"games_status":                         AdmissionControl,
		"rimgovernor/observations_read_custom": AdmissionObservation,
		"test/fixture_prepare":                 AdmissionObservation,
	} {
		if got := admissionClassOf(name); got != want {
			t.Errorf("%s: class %s, want %s", name, got, want)
		}
	}
}

// TestAdmissionReservesAControlSlot fills the observation ceiling and the
// shared remainder, then checks a control call is admitted at once while
// further observation waits, and that a freed slot goes to waiting
// control before waiting observation.
func TestAdmissionReservesAControlSlot(t *testing.T) {
	a := newAdmission(MaxConcurrentCalls)
	ctx := context.Background()
	admitted := 0
	for i := 0; i < admissionObservationMax; i++ {
		if _, err := a.acquire(ctx, AdmissionObservation); err != nil {
			t.Fatal(err)
		}
		admitted++
	}
	for i := 0; i < admissionMediaMax; i++ {
		if _, err := a.acquire(ctx, AdmissionMedia); err != nil {
			t.Fatal(err)
		}
		admitted++
	}
	if admitted != MaxConcurrentCalls-admissionControlReserved {
		t.Fatalf("observation and media hold %d slots, want %d", admitted, MaxConcurrentCalls-admissionControlReserved)
	}
	// The ninth non-control call waits; the eighth slot is control's.
	waitCtx, cancel := context.WithTimeout(ctx, 50*time.Millisecond)
	defer cancel()
	if _, err := a.acquire(waitCtx, AdmissionObservation); err == nil {
		t.Fatal("observation took the reserved control slot")
	}
	outcome, err := a.acquire(ctx, AdmissionControl)
	if err != nil {
		t.Fatal(err)
	}
	if outcome.wait != 0 || outcome.class != AdmissionControl {
		t.Fatalf("control waited: %+v", outcome)
	}
	// Everything is held now. Queue an observation, then a control call;
	// releasing one observation slot must admit the control call first.
	obsReady := make(chan admissionOutcome, 1)
	go func() {
		outcome, _ := a.acquire(ctx, AdmissionObservation)
		obsReady <- outcome
	}()
	waitUntil(t, func() bool { _, waiting := a.snapshot(); return waiting[AdmissionObservation] == 1 })
	ctlReady := make(chan admissionOutcome, 1)
	go func() {
		outcome, _ := a.acquire(ctx, AdmissionControl)
		ctlReady <- outcome
	}()
	waitUntil(t, func() bool { _, waiting := a.snapshot(); return waiting[AdmissionControl] == 1 })
	a.release(AdmissionObservation)
	select {
	case outcome := <-ctlReady:
		if outcome.queueDepth != 1 || outcome.classDepth != 0 {
			t.Fatalf("control saw queue %+v", outcome)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("control was not admitted on the freed slot")
	}
	select {
	case <-obsReady:
		t.Fatal("observation was admitted ahead of control")
	default:
	}
	a.release(AdmissionControl)
	select {
	case outcome := <-obsReady:
		if outcome.wait <= 0 {
			t.Fatalf("observation reports no wait: %+v", outcome)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("observation never admitted")
	}
}

// TestControlCallDispatchesAheadOfObservationCalls is the issue's
// acceptance (#631): with eight observation calls in flight through a
// Client, a control call reaches the transport next, not behind them.
func TestControlCallDispatchesAheadOfObservationCalls(t *testing.T) {
	const observations = 8
	release := make(chan struct{})
	entered := make(chan string, observations+1)
	s := &testServer{handler: func(ctx context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		entered <- args.Tool
		if strings.HasPrefix(args.Tool, "rimgovernor/observations_") {
			<-release
		}
		return structured(`{"ok":true}`), nil
	}}
	client := testClient(t, s, 5*time.Second)
	for i := 0; i < observations; i++ {
		go func() {
			_, _ = client.NativeCall(context.Background(), "rimgovernor/observations_read_bundle", json.RawMessage(`{}`))
		}()
	}
	for i := 0; i < admissionObservationMax; i++ {
		select {
		case <-entered:
		case <-time.After(5 * time.Second):
			t.Fatalf("only %d observation calls reached the transport", i)
		}
	}
	waitUntil(t, func() bool {
		_, waiting := client.gate.snapshot()
		return waiting[AdmissionObservation] == observations-admissionObservationMax
	})
	done := make(chan error, 1)
	go func() {
		_, err := client.NativeCall(context.Background(), "rimgovernor/clock_renew", json.RawMessage(`{}`))
		done <- err
	}()
	select {
	case tool := <-entered:
		if tool != "rimgovernor/clock_renew" {
			t.Fatalf("next call at the transport was %s", tool)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("control call queued behind observation")
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	close(release)
}

func waitUntil(t *testing.T, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for !cond() {
		if time.Now().After(deadline) {
			t.Fatal("condition not reached")
		}
		time.Sleep(time.Millisecond)
	}
}

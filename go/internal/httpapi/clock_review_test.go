package httpapi

import (
	"context"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type reviewFixture struct {
	calls int
	ack   store.ClockAcknowledgement
}

func (f *reviewFixture) Read(context.Context) (store.ClockReviewState, error) {
	return store.ClockReviewState{Revision: 2, InboxCursor: 3, ReviewedCursor: 3, Holds: []store.ClockHold{{Kind: store.ClockInterruptionHold, FromCursor: 3, ThroughCursor: 3}}}, nil
}
func (f *reviewFixture) Acknowledge(_ context.Context, ack store.ClockAcknowledgement) (store.ClockReviewState, error) {
	f.calls++
	f.ack = ack
	if ack.ExpectedRevision != 2 {
		return store.ClockReviewState{}, store.ErrConflict
	}
	return store.ClockReviewState{Revision: 3, InboxCursor: 3, ReviewedCursor: 3, AcknowledgedCursor: 3}, nil
}
func TestClockReviewAuthenticatedCAS(t *testing.T) {
	s, player := playerAPI(t)
	f := &reviewFixture{}
	s.config.ClockReview = f
	call := func(method, path, body, token string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, "http://127.0.0.1"+path, strings.NewReader(body))
		r.Header.Set("Content-Type", "application/json")
		if token != "" {
			r.Header.Set("X-RimGovernor-Player", token)
		}
		w := httptest.NewRecorder()
		s.Handler().ServeHTTP(w, r)
		return w
	}
	if w := call("GET", "/api/player/clock", "", ""); w.Code != 200 || !strings.Contains(w.Body.String(), `"revision":"2"`) {
		t.Fatal(w.Code, w.Body.String())
	}
	body := `{"requestId":"review-one","expectedRevision":"2","throughCursor":"3"}`
	if w := call("POST", "/api/player/clock/acknowledge", body, ""); w.Code != 403 || f.calls != 0 {
		t.Fatal("unauthenticated acknowledgement")
	}
	if w := call("POST", "/api/player/clock/acknowledge", strings.Replace(body, `"2"`, `"02"`, 1), s.playerToken); w.Code != 400 || f.calls != 0 {
		t.Fatal("noncanonical revision")
	}
	if w := call("POST", "/api/player/clock/acknowledge", strings.Replace(body, `"2"`, `"1"`, 1), s.playerToken); w.Code != 409 {
		t.Fatal(w.Code)
	}
	if w := call("POST", "/api/player/clock/acknowledge", body, s.playerToken); w.Code != 200 || !strings.Contains(w.Body.String(), `"acknowledgedCursor":"3"`) {
		t.Fatal(w.Code, w.Body.String())
	}
	if f.ack.RequestID != "review-one" || player.calls != 0 {
		t.Fatal("acknowledgement changed player authority")
	}
}

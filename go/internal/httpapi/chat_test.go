package httpapi

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
)

const chatJSON = `{"requestId":"chat-request","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"message":"tend to bob"}`

func TestChatHTTPDisabledReturns501(t *testing.T) {
	s, f := playerAPI(t)
	w := playerCall(s, "POST", "/api/chats/plans", chatJSON, s.playerToken)
	if w.Code != 501 {
		t.Fatal(w.Code, w.Body.String())
	}
	if f.calls != 0 {
		t.Fatal("disabled chat reached player", f.calls)
	}
}

// TestChatHTTPRejectsUnauthenticatedAndDisabled exercises the disabled-chat
// path. The generic mutation guard (body size, query string) runs ahead of
// any per-route dispatch and reports 400 regardless of enablement; anything
// that clears that guard hits chat's own enablement check before its body is
// ever decoded, so a merely malformed-but-bounded body reports 501 just like
// a well-formed one when chat is off. Body-decode-error coverage for the
// enabled case lives in TestDecodeChatRequest instead.
func TestChatHTTPRejectsUnauthenticatedAndDisabled(t *testing.T) {
	s, _ := playerAPI(t)
	for _, body := range []string{`null`, chatJSON + `{}`, strings.Replace(chatJSON, `"message":"tend to bob"`, `"message":null`, 1)} {
		w := playerCall(s, "POST", "/api/chats/plans", body, s.playerToken)
		if w.Code != 501 {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	for _, tc := range []struct {
		method, path, body, token string
		status                    int
	}{
		{"POST", "/api/chats/plans", chatJSON, "", 403},
		{"GET", "/api/chats/plans", "", "", 405},
		{"POST", "/api/chats/plans?x=1", chatJSON, s.playerToken, 400},
		{"POST", "/api/chats/plans", strings.Repeat(" ", 8193) + chatJSON, s.playerToken, 400},
	} {
		w := playerCall(s, tc.method, tc.path, tc.body, tc.token)
		if w.Code != tc.status {
			t.Fatal(tc.path, w.Code, w.Body.String())
		}
	}
}
func TestChatFailureStatus(t *testing.T) {
	for _, tc := range []struct {
		kind interpreter.FailureKind
		want int
	}{
		{interpreter.NoAuthority, 403},
		{interpreter.StaleFacts, 409},
		{interpreter.ModelFailure, 502},
		{interpreter.InvalidCommand, 422},
		{interpreter.UnknownFacts, 422},
		{interpreter.UnsupportedCommand, 422},
		{interpreter.InvalidInput, 400},
	} {
		if got := chatFailureStatus(tc.kind); got != tc.want {
			t.Fatalf("%s: got %d want %d", tc.kind, got, tc.want)
		}
	}
}
func TestDecodeChatRequest(t *testing.T) {
	requestID, world, message, err := decodeChatRequest(strings.NewReader(chatJSON))
	if err != nil {
		t.Fatal(err)
	}
	if requestID != "chat-request" || world.Colony != "colony" || world.Load != "load" || world.Map != 0 || message != "tend to bob" {
		t.Fatal(requestID, world, message)
	}
}

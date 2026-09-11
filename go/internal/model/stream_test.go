package model

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

func streamChunk(text string, finishReason *string) string {
	index := 0
	value := envelope{Choices: []choice{{Index: &index, Delta: &responseMessage{Content: &text}, FinishReason: finishReason}}}
	data, err := json.Marshal(value)
	if err != nil {
		panic(err)
	}
	return "data: " + string(data) + "\n\n"
}

func terminal(reason string) string { return streamChunk("", &reason) + "data: [DONE]\n\n" }

func TestStreamFragmentsMultilineDataAndUsage(t *testing.T) {
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		var request completionRequest
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
			t.Error(err)
		}
		if !request.Stream || request.StreamOptions == nil || !request.StreamOptions.IncludeUsage {
			t.Error("missing streaming usage request")
		}
		w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
		payload := ": heartbeat\r\n\r\n" + streamChunk("Hello", nil) + streamChunk(" 🐾", nil) +
			"data: {\"choices\":[{\"index\":0,\n" +
			"data: \"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
			"data: {\"choices\":[],\"usage\":{\"completion_tokens\":2}}\n\n" + "data: [DONE]\n\n"
		for _, value := range []byte(payload) {
			w.Write([]byte{value})
			w.(http.Flusher).Flush()
		}
	}, nil)
	var deltas []string
	result, err := client.Stream(context.Background(), testRequest, func(text string) error { deltas = append(deltas, text); return nil })
	if err != nil || result.Text != "Hello 🐾" || strings.Join(deltas, "") != result.Text || result.FinishReason != Stop {
		t.Fatalf("%#v %v %q", result, err, deltas)
	}
	if result.Usage == nil || result.Usage.PromptTokens != nil || result.Usage.CompletionTokens == nil || *result.Usage.CompletionTokens != 2 {
		t.Fatalf("usage lost missing/known distinction: %#v", result.Usage)
	}
}

func TestStreamRefusesMalformedTruncatedAndUnsupported(t *testing.T) {
	tests := []struct {
		name, body string
		want       error
	}{
		{"truncated", streamChunk("partial", nil), ErrInvalidResponse},
		{"missing done", streamChunk("hello", nil) + streamChunk("", pointer("stop")), ErrInvalidResponse},
		{"done before finish", "data: [DONE]\n\n", ErrInvalidResponse},
		{"malformed", "data: {\n\n", ErrInvalidResponse},
		{"unterminated frame", "data: [DONE]", ErrInvalidResponse},
		{"server error", `data: {"error":{"message":"model failed"}}` + "\n\n", ErrInvalidResponse},
		{"multiple choices", `data: {"choices":[{"index":0,"delta":{}},{"index":1,"delta":{}}]}` + "\n\n", ErrInvalidResponse},
		{"tool finish", terminal("tool_calls"), ErrToolCalls},
		{"tool delta", `data: {"choices":[{"index":0,"delta":{"content":"ignored","tool_calls":[{"id":"a"}]}}]}` + "\n\n", ErrToolCalls},
		{"refusal", `data: {"choices":[{"index":0,"delta":{"content":"ignored","refusal":"No"}}]}` + "\n\n", ErrRefusal},
		{"filtered", terminal("content_filter"), ErrRefusal},
		{"length", streamChunk("partial", nil) + terminal("length"), ErrOutputLimit},
		{"after finish", streamChunk("", pointer("stop")) + streamChunk("late", nil) + "data: [DONE]\n\n", ErrInvalidResponse},
		{"frame too large", ":" + strings.Repeat("x", 3000) + "\n\n", ErrFrameTooLarge},
		{"total too large", strings.Repeat(": heartbeat\n\n", 800), ErrResponseTooLarge},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
				io.Copy(io.Discard, r.Body)
				w.Header().Set("Content-Type", "text/event-stream")
				io.WriteString(w, tc.body)
			}, nil)
			var delivered strings.Builder
			result, err := client.Stream(context.Background(), testRequest, func(text string) error { delivered.WriteString(text); return nil })
			if !errors.Is(err, tc.want) {
				t.Fatalf("got %v want %v", err, tc.want)
			}
			if tc.want == ErrToolCalls || tc.want == ErrRefusal {
				if delivered.Len() != 0 {
					t.Fatal("unsupported delta emitted text")
				}
			}
			if tc.want == ErrOutputLimit && (result.Text != "partial" || result.FinishReason != Length) {
				t.Fatalf("lost partial response %#v", result)
			}
		})
	}
}

func pointer(value string) *string { return &value }

func TestStreamCancellationAndCallbackError(t *testing.T) {
	for _, mode := range []string{"cancel", "close", "callback"} {
		t.Run(mode, func(t *testing.T) {
			client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
				io.Copy(io.Discard, r.Body)
				w.Header().Set("Content-Type", "text/event-stream")
				io.WriteString(w, streamChunk("first", nil))
				w.(http.Flusher).Flush()
				<-r.Context().Done()
			}, nil)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			callbackError := errors.New("consumer stopped")
			started := make(chan struct{})
			done := make(chan error, 1)
			go func() {
				_, err := client.Stream(ctx, testRequest, func(string) error {
					close(started)
					if mode == "callback" {
						return callbackError
					}
					return nil
				})
				done <- err
			}()
			select {
			case <-started:
			case <-time.After(time.Second):
				t.Fatal("stream never started")
			}
			want := error(context.Canceled)
			if mode == "cancel" {
				cancel()
			} else if mode == "close" {
				client.Close()
			} else {
				want = callbackError
			}
			select {
			case err := <-done:
				if !errors.Is(err, want) {
					t.Fatal(err)
				}
			case <-time.After(time.Second):
				t.Fatal("stream did not terminate")
			}
		})
	}
}

func TestStreamCRLFFrameLimitAndBareCR(t *testing.T) {
	for _, ending := range []string{"\r", "\r\n"} {
		t.Run(ending, func(t *testing.T) {
			payload := strings.ReplaceAll(streamChunk("ok", nil)+terminal("stop"), "\n", ending)
			client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "text/event-stream")
				io.WriteString(w, payload)
			}, nil)
			if result, err := client.Stream(context.Background(), testRequest, func(string) error { return nil }); err != nil || result.Text != "ok" {
				t.Fatalf("%#v %v", result, err)
			}
		})
	}
}

func TestStreamRequiresEventMediaType(t *testing.T) {
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, completeOK)
	}, nil)
	if _, err := client.Stream(context.Background(), testRequest, func(string) error { return nil }); !errors.Is(err, ErrInvalidResponse) {
		t.Fatal(err)
	}
}

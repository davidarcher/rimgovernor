package model

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

var testRequest = Request{Messages: []Message{{Role: User, Content: "Describe the colony"}}, MaxOutputTokens: 32}

const completeOK = `{"choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}`

func testClient(t *testing.T, handler http.HandlerFunc, edit func(*Config)) (*Client, *httptest.Server) {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	config := Config{Model: "local-test", BaseURL: server.URL + "/v1", Timeout: 2 * time.Second, MaxResponseBytes: 8192, MaxFrameBytes: 2048}
	if edit != nil {
		edit(&config)
	}
	client, err := NewClient(config)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { client.Close() })
	return client, server
}

func TestCompleteUsesExplicitLocalTextRequest(t *testing.T) {
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" || r.URL.Path != "/v1/chat/completions" {
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
		}
		var body completionRequest
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if body.Model != "local-test" || body.MaxTokens != 32 || body.N != 1 || body.Stream || body.StreamOptions != nil || len(body.Messages) != 1 || body.Messages[0] != testRequest.Messages[0] {
			t.Errorf("unexpected request %#v", body)
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, completeOK)
	}, nil)
	result, err := client.Complete(context.Background(), testRequest)
	if err != nil || result.Text != "Hello" || result.FinishReason != Stop || result.Usage == nil || result.Usage.PromptTokens == nil || *result.Usage.PromptTokens != 4 {
		t.Fatalf("result=%#v err=%v", result, err)
	}
}

func TestConfigRejectsNonlocalOrAmbiguousEndpoints(t *testing.T) {
	for _, base := range []string{"https://127.0.0.1/v1", "http://example.com/v1", "http://127.0.0.1.evil/v1", "http://user:pass@127.0.0.1/v1", "http://localhost/v1?key=x", "http://localhost/v1#part", "http://host.docker.internal/v1", "http://[::2]/v1", "http://localhost:0/v1"} {
		if client, err := NewClient(Config{Model: "test", BaseURL: base, Timeout: time.Second, MaxResponseBytes: 100}); err == nil {
			client.Close()
			t.Errorf("accepted %s", base)
		}
	}
	for _, base := range []string{"http://localhost:1234/v1", "http://127.0.0.1:1234/v1", "http://[::1]:1234/v1", "http://host.docker.internal:1234/v1"} {
		client, err := NewClient(Config{Model: "test", BaseURL: base, Timeout: time.Second, MaxResponseBytes: 100, AllowDockerHost: true})
		if err != nil {
			t.Errorf("refused %s: %v", base, err)
		} else {
			client.Close()
		}
	}
}

func TestRefusesRedirectWithoutSendingToTargetOrRetrying(t *testing.T) {
	var targets, requests atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { targets.Add(1) }))
	defer target.Close()
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		w.Header().Set("Location", target.URL)
		w.WriteHeader(http.StatusTemporaryRedirect)
	}, nil)
	_, err := client.Complete(context.Background(), testRequest)
	var httpError *HTTPError
	if !errors.As(err, &httpError) || httpError.StatusCode != 307 || targets.Load() != 0 || requests.Load() != 1 {
		t.Fatalf("err=%v target=%d requests=%d", err, targets.Load(), requests.Load())
	}
}

func TestProxyEnvironmentCannotSupplyTransport(t *testing.T) {
	var proxyCalls atomic.Int32
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { proxyCalls.Add(1) }))
	defer proxy.Close()
	t.Setenv("HTTP_PROXY", proxy.URL)
	t.Setenv("HTTPS_PROXY", proxy.URL)
	t.Setenv("ALL_PROXY", proxy.URL)
	t.Setenv("NO_PROXY", "")
	client, server := testClient(t, func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, completeOK) }, func(config *Config) { config.BaseURL = strings.Replace(config.BaseURL, "127.0.0.1", "localhost", 1) })
	_ = server
	if _, err := client.Complete(context.Background(), testRequest); err != nil {
		t.Fatal(err)
	}
	if client.transport.Proxy != nil || proxyCalls.Load() != 0 {
		t.Fatal("proxy configuration was inherited")
	}
}

func TestCompleteRefusalsAndBounds(t *testing.T) {
	tests := []struct {
		name, body string
		status     int
		want       error
	}{
		{"length", strings.Replace(completeOK, `"stop"`, `"length"`, 1), 200, ErrOutputLimit},
		{"tool finish", strings.Replace(completeOK, `"stop"`, `"tool_calls"`, 1), 200, ErrToolCalls},
		{"tool payload", strings.Replace(completeOK, `"content":"Hello"`, `"content":"Hello","tool_calls":[{"id":"tool-1"}]`, 1), 200, ErrToolCalls},
		{"refusal", strings.Replace(completeOK, `"content":"Hello"`, `"content":"Hello","refusal":"No"`, 1), 200, ErrRefusal},
		{"filtered", strings.Replace(completeOK, `"stop"`, `"content_filter"`, 1), 200, ErrRefusal},
		{"missing finish", strings.Replace(completeOK, `"finish_reason":"stop"`, `"finish_reason":null`, 1), 200, ErrInvalidResponse},
		{"missing content", strings.Replace(completeOK, `"content":"Hello"`, `"content":null`, 1), 200, ErrInvalidResponse},
		{"wrong choice", strings.Replace(completeOK, `"index":0`, `"index":1`, 1), 200, ErrInvalidResponse},
		{"missing choice", `{"choices":[]}`, 200, ErrInvalidResponse},
		{"duplicate", `{"choices":[],"choices":[]}`, 200, ErrInvalidResponse},
		{"malformed", "{", 200, ErrInvalidResponse},
		{"utf8", string([]byte{'"', 0xff, '"'}), 200, ErrInvalidResponse},
		{"negative usage", strings.Replace(completeOK, `"prompt_tokens":4`, `"prompt_tokens":-1`, 1), 200, ErrInvalidResponse},
		{"fraction usage", strings.Replace(completeOK, `"prompt_tokens":4`, `"prompt_tokens":1.5`, 1), 200, ErrInvalidResponse},
		{"server error", `{"error":{"message":"loading failed"}}`, 200, ErrInvalidResponse},
		{"too large", strings.Repeat("x", 9000), 200, ErrResponseTooLarge},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(tc.status); io.WriteString(w, tc.body) }, nil)
			result, err := client.Complete(context.Background(), testRequest)
			if !errors.Is(err, tc.want) {
				t.Fatalf("got %v want %v", err, tc.want)
			}
			if tc.want == ErrOutputLimit && (result.Text != "Hello" || result.FinishReason != Length) {
				t.Fatalf("missing diagnostic partial response %#v", result)
			}
		})
	}
}

func TestHTTPErrorAndUnknownUsage(t *testing.T) {
	var requests atomic.Int32
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		w.WriteHeader(503)
		io.WriteString(w, `{"error":{"message":"model is loading"}}`)
	}, nil)
	_, err := client.Complete(context.Background(), testRequest)
	var httpError *HTTPError
	if !errors.As(err, &httpError) || httpError.StatusCode != 503 || httpError.Message != "model is loading" || requests.Load() != 1 {
		t.Fatalf("%v", err)
	}
	unknown, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}`)
	}, nil)
	value, err := unknown.Complete(context.Background(), testRequest)
	if err != nil || value.Usage != nil {
		t.Fatalf("unknown usage became zero: %#v %v", value, err)
	}
}

func TestCancellationAndOwnedCloseWhileWaitingHeaders(t *testing.T) {
	for _, mode := range []string{"cancel", "close", "timeout"} {
		t.Run(mode, func(t *testing.T) {
			started := make(chan struct{})
			client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) {
				io.Copy(io.Discard, r.Body)
				close(started)
				<-r.Context().Done()
			}, func(config *Config) {
				if mode == "timeout" {
					config.Timeout = 100 * time.Millisecond
				}
			})
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			done := make(chan error, 1)
			go func() { _, err := client.Complete(ctx, testRequest); done <- err }()
			select {
			case <-started:
			case <-time.After(time.Second):
				t.Fatal("request never started")
			}
			want := context.Canceled
			if mode == "cancel" {
				cancel()
			} else if mode == "close" {
				client.Close()
			} else {
				want = context.DeadlineExceeded
			}
			select {
			case err := <-done:
				if !errors.Is(err, want) {
					t.Fatalf("got %v", err)
				}
			case <-time.After(time.Second):
				t.Fatal("request did not cancel")
			}
			if mode == "close" {
				if _, err := client.Complete(context.Background(), testRequest); !errors.Is(err, ErrClosed) {
					t.Fatal(err)
				}
			}
		})
	}
}

func TestInvalidRequestsNeverReachServer(t *testing.T) {
	var count atomic.Int32
	client, _ := testClient(t, func(w http.ResponseWriter, r *http.Request) { count.Add(1) }, nil)
	for _, request := range []Request{{}, {Messages: []Message{{Role: "tool", Content: "no"}}, MaxOutputTokens: 1}, {Messages: []Message{{Role: User, Content: string([]byte{0xff})}}, MaxOutputTokens: 1}} {
		if _, err := client.Complete(context.Background(), request); err == nil {
			t.Fatal("invalid request accepted")
		}
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := client.Complete(ctx, testRequest); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if count.Load() != 0 {
		t.Fatal("invalid request reached server")
	}
}

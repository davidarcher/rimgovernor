package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/httpapi"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The launcher's client for serve's read API; today only the work ledger
// (the one observation view without a Grafana equivalent). The page never
// fetches serve itself (its SetHtml origin is opaque and serve rejects a
// foreign Origin), so the view reads this Reading through Bind. The feed
// keeps its last good value: a failed refresh returns that value marked
// stale with the error, and a 404 is "not served" (Observe mode serves no
// ledger), not an error.

const (
	serveRequestTimeout = 5 * time.Second
	serveBodyLimit      = 4 << 20
)

// Reading is one feed's latest answer. Value is the last good decode (nil
// before the first); Stale says it is older than the newest attempt, whose
// failure Error carries. NotServed means the controller answered 404 and
// Value is dropped. At is when Value was read.
type Reading[T any] struct {
	Value     *T        `json:"value"`
	Stale     bool      `json:"stale"`
	Error     string    `json:"error,omitempty"`
	NotServed bool      `json:"notServed"`
	At        time.Time `json:"at"`
}

// feed holds one endpoint's last good value behind its own lock, so a slow
// feed never blocks another.
type feed[T any] struct {
	path string
	mu   sync.Mutex
	last Reading[T]
}

// ServeClient reads serve's API at the controller URL base returns (the
// launcher passes app.activePort). It is safe for concurrent use.
type ServeClient struct {
	base   func() string
	http   *http.Client
	ledger feed[policy.LedgerView]
}

func NewServeClient(base func() string) *ServeClient {
	c := &ServeClient{base: base, http: &http.Client{Timeout: serveRequestTimeout}}
	c.ledger.path = "/api/ledger"
	return c
}

// Ledger fetches the work ledger once and returns the feed's Reading.
func (c *ServeClient) Ledger(ctx context.Context) Reading[policy.LedgerView] {
	return refresh(ctx, c, &c.ledger)
}

func refresh[T any](ctx context.Context, c *ServeClient, f *feed[T]) Reading[T] {
	value, notServed, err := fetch[T](ctx, c, f.path)
	f.mu.Lock()
	defer f.mu.Unlock()
	switch {
	case err != nil:
		f.last.Stale = f.last.Value != nil
		f.last.NotServed = false
		f.last.Error = err.Error()
	case notServed:
		f.last = Reading[T]{NotServed: true}
	default:
		f.last = Reading[T]{Value: value, At: time.Now()}
	}
	return f.last
}

func fetch[T any](ctx context.Context, c *ServeClient, path string) (*T, bool, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(c.base(), "/")+path, nil)
	if err != nil {
		return nil, false, err
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, false, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, serveBodyLimit+1))
	if err != nil {
		return nil, false, err
	}
	switch {
	case resp.StatusCode == http.StatusNotFound:
		return nil, true, nil
	case resp.StatusCode != http.StatusOK:
		var failure httpapi.Failure
		if json.Unmarshal(body, &failure) == nil && failure.Detail != "" {
			return nil, false, fmt.Errorf("HTTP %d: %s", resp.StatusCode, failure.Detail)
		}
		return nil, false, fmt.Errorf("HTTP %d", resp.StatusCode)
	case len(body) > serveBodyLimit:
		return nil, false, fmt.Errorf("response over %d bytes", serveBodyLimit)
	}
	var v T
	if err := json.Unmarshal(body, &v); err != nil {
		return nil, false, fmt.Errorf("decode %s: %w", path, err)
	}
	return &v, false, nil
}

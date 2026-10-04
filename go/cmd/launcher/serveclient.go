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
	"github.com/davidarcher/RimGovernor/go/internal/spectator"
)

// The launcher's client for serve's read API (#1984). The page never
// fetches serve itself (its SetHtml origin is opaque and serve rejects a
// foreign Origin), so the views read these Readings through Bind. Each feed
// keeps its last good value: a failed refresh returns that value marked
// stale with the error, and a 404 is "not served" (Observe mode serves no
// routines, spectator or player routes), not an error.

// Poll cadences follow the dashboard's; the views drive the fetch methods
// at these.
const (
	StateEvery    = 1500 * time.Millisecond
	NowEvery      = 2 * time.Second
	RoutinesEvery = 3 * time.Second
	ClockEvery    = 3 * time.Second
	ControlEvery  = 1500 * time.Millisecond

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

// DevelopmentView is the Now view's slice of /api/routines: the development
// rows with their blocked-by reasons, and each active concern's blocked
// reason. The full DTO is unexported and up to 1 MiB; the decoder skips
// everything else in it.
type DevelopmentView struct {
	Development *Development   `json:"development"`
	Progress    []ConcernBlock `json:"progress"`
}

type Development struct {
	Tick        int64            `json:"tick"`
	Workers     *int             `json:"workers"`
	Capacity    int              `json:"capacity"`
	Committed   []string         `json:"committed"`
	HeldWorkers int              `json:"heldWorkers"`
	Limiting    string           `json:"limiting"`
	Rows        []DevelopmentRow `json:"rows"`
}

// DevelopmentRow is one candidate concern; Reason is why it was or was not
// given a slot, Bottleneck the work type that limited it.
type DevelopmentRow struct {
	Concern      string   `json:"concern"`
	Score        float64  `json:"score"`
	Deficit      *float64 `json:"deficit"`
	Risk         *float64 `json:"risk"`
	WaitingSince int64    `json:"waitingSince"`
	Selected     bool     `json:"selected"`
	Committed    bool     `json:"committed"`
	Reason       string   `json:"reason"`
	Bottleneck   string   `json:"bottleneck"`
}

// ConcernBlock is a concern's blocked-by reason, empty when not blocked.
type ConcernBlock struct {
	Concern string `json:"concern"`
	Blocked string `json:"blocked"`
}

// ClockView is /api/player/clock: the clock review cursors and holds.
type ClockView struct {
	Revision     string      `json:"revision"`
	Inbox        string      `json:"inboxCursor"`
	Reviewed     string      `json:"reviewedCursor"`
	Acknowledged string      `json:"acknowledgedCursor"`
	Holds        []ClockHold `json:"holds"`
}

type ClockHold struct {
	Kind    string `json:"kind"`
	From    string `json:"fromCursor"`
	Through string `json:"throughCursor"`
}

// ControlView is /api/player/control: the latest pause or resume record and
// whether player control is enabled.
type ControlView struct {
	Record *ControlRecord   `json:"record"`
	State  ControlState     `json:"state"`
	Error  *httpapi.Failure `json:"error"`
}

type ControlRecord struct {
	RequestID string `json:"requestId"`
	Kind      string `json:"kind"`
	Phase     string `json:"phase"`
}

type ControlState struct {
	Enabled          bool `json:"enabled"`
	ObservationKnown bool `json:"observationKnown"`
}

// feed holds one endpoint's last good value behind its own lock, so a slow
// feed never blocks another.
type feed[T any] struct {
	path string
	// keep503 reads a 503 whose body is the feed's own DTO (not a bare
	// failure) as a value: /api/player/control answers 503 with the record
	// and an "uncertain" error while a control outcome is unresolved.
	keep503 bool
	mu      sync.Mutex
	last    Reading[T]
}

// ServeClient reads serve's API at the controller URL base returns (the
// launcher passes app.activePort). It is safe for concurrent use.
type ServeClient struct {
	base     func() string
	http     *http.Client
	state    feed[httpapi.State]
	now      feed[spectator.Now]
	routines feed[DevelopmentView]
	clock    feed[ClockView]
	control  feed[ControlView]
	tok      tokenCache // the player token, fetched on first write
}

func NewServeClient(base func() string) *ServeClient {
	c := &ServeClient{base: base, http: &http.Client{Timeout: serveRequestTimeout}}
	c.state.path = "/api/state"
	c.now.path = "/api/spectator/now"
	c.routines.path = "/api/routines"
	c.clock.path = "/api/player/clock"
	c.control.path = "/api/player/control"
	c.control.keep503 = true
	return c
}

// Each method fetches its endpoint once and returns the feed's Reading.
func (c *ServeClient) State(ctx context.Context) Reading[httpapi.State] {
	return refresh(ctx, c, &c.state)
}
func (c *ServeClient) Now(ctx context.Context) Reading[spectator.Now] {
	return refresh(ctx, c, &c.now)
}
func (c *ServeClient) Routines(ctx context.Context) Reading[DevelopmentView] {
	return refresh(ctx, c, &c.routines)
}
func (c *ServeClient) Clock(ctx context.Context) Reading[ClockView] {
	return refresh(ctx, c, &c.clock)
}
func (c *ServeClient) Control(ctx context.Context) Reading[ControlView] {
	return refresh(ctx, c, &c.control)
}

func refresh[T any](ctx context.Context, c *ServeClient, f *feed[T]) Reading[T] {
	value, notServed, err := fetch[T](ctx, c, f.path, f.keep503)
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

func fetch[T any](ctx context.Context, c *ServeClient, path string, keep503 bool) (*T, bool, error) {
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
	case resp.StatusCode == http.StatusServiceUnavailable && keep503 && !bareFailure(body):
		// a control record with an "uncertain" error: decoded below
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

package nativeaccept

import (
	"context"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
)

// ViewerClient is one dashboard video tile driven from the harness (#621):
// it does what dashboard/src/features/manager/GameVideoGo.tsx does against
// a serve process, so a speed row measures the viewing overhead a real
// viewer adds. It demands rendering, holds a 15 s screen lease renewed
// every 10 s, mints a ticket, connects the WebSocket and drains frames at
// the stream's own cadence (the server's default poll interval), retrying a
// second after a closed socket as the tile does. Beside it a second socket
// on the same source is opened and never read (#631): a stalled or hidden
// tab, which must cost neither the draining tile nor the simulation
// anything. A game that cannot capture (batch mode) answers the lease
// unsupported; the client records that and stops, as the tile does.
type ViewerClient struct {
	service *ServiceProcess
	token   string
	opts    ViewerOptions
	cancel  context.CancelFunc
	done    chan struct{}
	started time.Time

	mu       sync.Mutex
	summary  viewerSummary
	sourceID string
}

type viewerSummary struct {
	Supported   bool   `json:"supported"`
	Active      bool   `json:"active"`
	Unavailable string `json:"unavailable,omitempty"`
	SourceID    string `json:"source_id,omitempty"`
	Frames      uint64 `json:"frames"`
	Bytes       uint64 `json:"bytes"`
	Connects    int    `json:"connects"`
	// StalledConnects counts the never-read companion sockets opened; the
	// server ends each once its write outlasts its timeout.
	StalledConnects int      `json:"stalled_connects"`
	Leases          int      `json:"leases"`
	Errors          []string `json:"errors,omitempty"`
	DurationS       float64  `json:"duration_s"`
	FramesPerSec    float64  `json:"frames_per_second"`
	CaptureMethod   string   `json:"capture_method,omitempty"`
}

const (
	viewerLeaseSeconds = 15
	viewerRenewEvery   = 10 * time.Second
	viewerRetryAfter   = time.Second
	viewerID           = "speedmatrix-viewer"
)

// ViewerOptions shape a viewer beyond the tile's defaults (#633): ID is
// the lease's viewerId (default viewerID); Stalled connects the stream and
// then never reads it, a dashboard client that stopped consuming frames
// while holding its socket and renewing its lease.
type ViewerOptions struct {
	ID      string
	Stalled bool
}

// StartViewer starts the client against the service; Stop ends it and
// returns its summary.
func StartViewer(ctx context.Context, service *ServiceProcess, token string) *ViewerClient {
	return StartViewerWith(ctx, service, token, ViewerOptions{})
}

// StartViewerWith is StartViewer with options.
func StartViewerWith(ctx context.Context, service *ServiceProcess, token string, opts ViewerOptions) *ViewerClient {
	if opts.ID == "" {
		opts.ID = viewerID
	}
	ctx, cancel := context.WithCancel(ctx)
	v := &ViewerClient{service: service, token: token, opts: opts, cancel: cancel, done: make(chan struct{}), started: time.Now()}
	go v.run(ctx)
	return v
}

// Stop ends the client, releases its lease and reports what it streamed.
func (v *ViewerClient) Stop() map[string]any {
	v.cancel()
	<-v.done
	v.mu.Lock()
	defer v.mu.Unlock()
	v.summary.DurationS = time.Since(v.started).Seconds()
	if v.summary.DurationS > 0 {
		v.summary.FramesPerSec = float64(v.summary.Frames) / v.summary.DurationS
	}
	return map[string]any{
		"supported": v.summary.Supported, "active": v.summary.Active, "unavailable": v.summary.Unavailable, "source_id": v.summary.SourceID,
		"frames": v.summary.Frames, "bytes": v.summary.Bytes, "connects": v.summary.Connects, "stalled_connects": v.summary.StalledConnects, "leases": v.summary.Leases,
		"errors": v.summary.Errors, "duration_s": v.summary.DurationS, "frames_per_second": v.summary.FramesPerSec, "capture_method": v.summary.CaptureMethod,
		"viewer_id": v.opts.ID, "stalled": v.opts.Stalled,
	}
}

func (v *ViewerClient) note(err error) {
	if err == nil {
		return
	}
	v.mu.Lock()
	defer v.mu.Unlock()
	if len(v.summary.Errors) < 16 {
		v.summary.Errors = append(v.summary.Errors, err.Error())
	}
}

// lease demands rendering and takes or renews the screen lease. It returns
// false once the game says video is unsupported.
func (v *ViewerClient) lease() bool {
	_, _, _ = v.service.API("POST", "/api/presentation/render-demand", map[string]any{"leaseSeconds": viewerLeaseSeconds}, v.token)
	state, status, err := v.service.API("POST", "/api/presentation/video-lease", map[string]any{
		"leaseSeconds": viewerLeaseSeconds, "source": map[string]any{"kind": "screen"}, "viewerId": v.opts.ID,
	}, v.token)
	v.mu.Lock()
	defer v.mu.Unlock()
	v.summary.Leases++
	if err != nil {
		v.summary.Errors = append(v.summary.Errors, err.Error())
		return true
	}
	if status != 200 {
		if len(v.summary.Errors) < 16 {
			v.summary.Errors = append(v.summary.Errors, "video-lease status "+http.StatusText(status))
		}
		return status != 404
	}
	supported, _ := AsBool(state["supported"])
	active, _ := AsBool(state["active"])
	v.summary.Supported, v.summary.Active = supported, active
	v.summary.Unavailable = AsString(state["unavailable"])
	v.summary.CaptureMethod = AsString(state["captureMethod"])
	if active {
		v.sourceID = AsString(state["sourceId"])
		v.summary.SourceID = v.sourceID
	}
	return supported
}

func (v *ViewerClient) run(ctx context.Context) {
	defer close(v.done)
	defer func() {
		// Release the lease the way a closed tile does, off the ended context.
		v.mu.Lock()
		supported := v.summary.Supported
		v.mu.Unlock()
		if supported {
			_, _, _ = v.service.API("POST", "/api/presentation/video-lease", map[string]any{"leaseSeconds": 0, "viewerId": v.opts.ID}, v.token)
		}
	}()
	if !v.lease() {
		return
	}
	renew := time.NewTicker(viewerRenewEvery)
	defer renew.Stop()
	go func() {
		for {
			select {
			case <-ctx.Done():
				return
			case <-renew.C:
				v.lease()
			}
		}
	}()
	for ctx.Err() == nil {
		v.mu.Lock()
		source := v.sourceID
		v.mu.Unlock()
		if source == "" {
			// Not active yet (no map, or a reload in flight): the renewal
			// keeps asking.
			if !sleep(ctx, viewerRetryAfter) {
				return
			}
			continue
		}
		v.stream(ctx, source)
		if !sleep(ctx, viewerRetryAfter) {
			return
		}
	}
}

// stream mints a ticket for the source and drains its socket until it
// closes or the context ends.
func (v *ViewerClient) stream(ctx context.Context, source string) {
	ticket, status, err := v.service.API("POST", "/api/presentation/video-stream/ticket", map[string]any{"sourceId": source}, v.token)
	if err != nil || status != 200 {
		v.note(err)
		return
	}
	url := strings.Replace(v.service.URL, "http://", "ws://", 1) + "/api/presentation/video-stream?ticket=" + AsString(ticket["ticket"])
	conn, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: http.Header{"Origin": []string{v.service.URL}}})
	if err != nil {
		v.note(err)
		return
	}
	defer conn.CloseNow()
	conn.SetReadLimit(64 << 20)
	v.mu.Lock()
	v.summary.Connects++
	v.mu.Unlock()
	if stalled := v.stalledCompanion(ctx, source); stalled != nil {
		defer stalled.CloseNow()
	}
	if v.opts.Stalled {
		// The socket stays open and unread until the client is stopped.
		<-ctx.Done()
		return
	}
	for {
		_, data, err := conn.Read(ctx)
		if err != nil {
			return
		}
		v.mu.Lock()
		v.summary.Frames++
		v.summary.Bytes += uint64(len(data))
		v.mu.Unlock()
	}
}

// stalledCompanion opens a second socket on source that is never read,
// the way a hidden tab's stream stalls; nil when it could not connect.
func (v *ViewerClient) stalledCompanion(ctx context.Context, source string) *websocket.Conn {
	ticket, status, err := v.service.API("POST", "/api/presentation/video-stream/ticket", map[string]any{"sourceId": source}, v.token)
	if err != nil || status != 200 {
		return nil
	}
	url := strings.Replace(v.service.URL, "http://", "ws://", 1) + "/api/presentation/video-stream?ticket=" + AsString(ticket["ticket"])
	conn, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{HTTPHeader: http.Header{"Origin": []string{v.service.URL}}})
	if err != nil {
		return nil
	}
	v.mu.Lock()
	v.summary.StalledConnects++
	v.mu.Unlock()
	return conn
}

func sleep(ctx context.Context, d time.Duration) bool {
	select {
	case <-ctx.Done():
		return false
	case <-time.After(d):
		return true
	}
}

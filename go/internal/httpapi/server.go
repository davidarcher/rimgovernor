package httpapi

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"sync"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type Server struct {
	player       PlayerBuildings
	controls     ControlReader
	playerToken  string
	config       Config
	snapshots    SnapshotProvider
	plans        PlanReader
	assets       *os.Root
	closeOnce    sync.Once
	closeErr     error
	videoTickets sync.Map // hex ticket -> videoTicket; single-use, short-lived
	chat         *interpreter.Interpreter
	chatNative   buildingruntime.ChatFactsNative
	chatJournal  buildingruntime.ChatFactsJournal
}

// EnableChat wires the guidance chat endpoint (POST /api/chat) into a server
// already constructed with NewWithPlayer. All three dependencies are required
// together: without them the route responds 501, matching every other
// not-available mutation under /api/.
func (s *Server) EnableChat(interp *interpreter.Interpreter, native buildingruntime.ChatFactsNative, journal buildingruntime.ChatFactsJournal) {
	s.chat, s.chatNative, s.chatJournal = interp, native, journal
}

func New(config Config, snapshots SnapshotProvider, plans PlanReader) (*Server, error) {
	if config.ReadTimeout <= 0 || config.ShutdownTimeout <= 0 || config.MaxResponseBytes < 256 || config.MaxResponseBytes > 16<<20 || snapshots == nil || plans == nil {
		return nil, errors.New("read timeout, shutdown timeout, response bound and read providers required")
	}
	assets, err := openAssets(config.AssetsDir)
	if err != nil {
		return nil, err
	}
	return &Server{config: config, snapshots: snapshots, plans: plans, assets: assets}, nil
}

// Close releases owned asset directory handles; provider/store ownership remains
// with the caller. Concurrent calls are safe and return the same result.
func (s *Server) Close() error {
	s.closeOnce.Do(func() {
		if s.assets != nil {
			s.closeErr = s.assets.Close()
		}
	})
	return s.closeErr
}
func (s *Server) Handler() http.Handler { return http.HandlerFunc(s.handle) }

// Serve owns the supplied loopback TCP listener until shutdown. Cancellation
// reaches active providers before graceful shutdown; no dependency is closed.
func (s *Server) Serve(ctx context.Context, listener net.Listener) error {
	defer s.Close()
	address, ok := listener.Addr().(*net.TCPAddr)
	if !ok || !address.IP.IsLoopback() {
		return errors.New("HTTP API requires a loopback TCP listener")
	}
	server := &http.Server{Handler: s.Handler(), ReadHeaderTimeout: s.config.ReadTimeout, ReadTimeout: s.config.ReadTimeout, WriteTimeout: s.config.ReadTimeout, IdleTimeout: s.config.ReadTimeout, MaxHeaderBytes: 8192, BaseContext: func(net.Listener) context.Context { return ctx }}
	done := make(chan error, 1)
	go func() { done <- server.Serve(listener) }()
	select {
	case err := <-done:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		shutdown, cancel := context.WithTimeout(context.Background(), s.config.ShutdownTimeout)
		defer cancel()
		err := server.Shutdown(shutdown)
		if err != nil {
			_ = server.Close()
		}
		serveErr := <-done
		if err != nil {
			return err
		}
		if serveErr != nil && !errors.Is(serveErr, http.ErrServerClosed) {
			return serveErr
		}
		return nil
	}
}

func localHost(authority string) bool {
	host := authority
	if h, p, err := net.SplitHostPort(authority); err == nil {
		host = h
		port, err := strconv.Atoi(p)
		if err != nil || port < 1 || port > 65535 {
			return false
		}
	} else if strings.Contains(authority, ":") {
		if authority != "[::1]" {
			return false
		}
		host = "::1"
	}
	return strings.EqualFold(host, "localhost") || host == "127.0.0.1" || host == "::1"
}
func (s *Server) handle(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'")
	if !localHost(r.Host) {
		s.failure(w, r, 403, "local_only", "Use the local controller address")
		return
	}
	if r.Header.Get("Sec-Fetch-Site") == "cross-site" {
		s.failure(w, r, 403, "cross_origin", "Cross-site access is unavailable")
		return
	}
	if origin := r.Header.Get("Origin"); origin != "" {
		parsed, err := url.Parse(origin)
		if err != nil || parsed.Scheme != "http" || !strings.EqualFold(parsed.Host, r.Host) || parsed.User != nil || parsed.Path != "" || parsed.RawQuery != "" || parsed.Fragment != "" {
			s.failure(w, r, 403, "cross_origin", "Origin must match this local controller")
			return
		}
	}
	if s.handlePprof(w, r) {
		return
	}
	if s.handleTelemetry(w, r) {
		return
	}
	if s.handleNotifications(w, r) {
		return
	}
	if s.handlePresentation(w, r) {
		return
	}
	if s.handlePresentationMedia(w, r) {
		return
	}
	if s.handleLifecycle(w, r) {
		return
	}
	if s.handleVideoStream(w, r) {
		return
	}
	if s.handlePlayer(w, r) {
		return
	}
	known := r.URL.Path == "/api/state" || r.URL.Path == "/api/health" || r.URL.Path == "/api/plan" || r.URL.Path == "/api/routines"
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		if known {
			w.Header().Set("Allow", "GET, HEAD")
			s.failure(w, r, 405, "method_not_allowed", "This route is read-only")
		} else if strings.HasPrefix(r.URL.Path, "/api/") {
			s.failure(w, r, 501, "unsupported", "Controller mutations are not available")
		} else {
			s.failure(w, r, 404, "not_found", "Route not found")
		}
		return
	}
	if !known {
		if !strings.HasPrefix(r.URL.Path, "/api/") && r.URL.Path != "/api" && s.assets != nil {
			s.serveAsset(w, r)
			return
		}
		s.failure(w, r, 404, "not_found", "Route not found")
		return
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) > 0 {
		s.failure(w, r, 400, "invalid_request", "Read requests require a bounded URL and no body")
		return
	}
	query, err := url.ParseQuery(r.URL.RawQuery)
	if err != nil {
		s.failure(w, r, 400, "invalid_query", "Invalid query")
		return
	}
	if r.URL.Path != "/api/plan" && len(query) != 0 {
		s.failure(w, r, 400, "invalid_query", "This route accepts no query parameters")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	switch r.URL.Path {
	case "/api/health":
		s.write(w, r, 200, struct {
			Service string `json:"service"`
			Backend string `json:"backend"`
			PID     int    `json:"pid"`
		}{"rimgovernor", "go", os.Getpid()})
	case "/api/state":
		snapshot, err := s.snapshots.Snapshot(ctx)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		result, err := state(snapshot)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, result)
	case "/api/routines":
		if s.config.Routines == nil {
			s.failure(w, r, 404, "not_found", "Routine diagnostics are not enabled")
			return
		}
		status, err := s.config.Routines.RoutineStatus(ctx)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, routineStatus(status))
	case "/api/plan":
		ids := query["id"]
		if len(query) != 1 || len(ids) != 1 || strings.TrimSpace(ids[0]) == "" || len(ids[0]) > 256 || !utf8.ValidString(ids[0]) {
			s.failure(w, r, 400, "invalid_query", "One explicit plan id is required")
			return
		}
		stored, err := s.plans.LoadPlan(ctx, domain.PlanID(ids[0]))
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		result, err := plan(stored)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, result)
	}
}
func (s *Server) readFailure(w http.ResponseWriter, r *http.Request, err error) {
	switch {
	case errors.Is(err, context.DeadlineExceeded):
		s.failure(w, r, 504, "read_timeout", "Controller read timed out")
	case errors.Is(err, context.Canceled):
		s.failure(w, r, 408, "cancelled", "Controller read was cancelled")
	case errors.Is(err, store.ErrNotFound):
		s.failure(w, r, 404, "not_found", "Plan not found")
	default:
		s.failure(w, r, 503, "unavailable", "Controller data is unavailable")
	}
}
func (s *Server) failure(w http.ResponseWriter, r *http.Request, status int, code, detail string) {
	s.write(w, r, status, Failure{code, detail})
}

type boundedBuffer struct {
	bytes.Buffer
	limit int
}

func (b *boundedBuffer) Write(data []byte) (int, error) {
	if len(data) > b.limit-b.Len() {
		return 0, io.ErrShortBuffer
	}
	return b.Buffer.Write(data)
}
func (s *Server) write(w http.ResponseWriter, r *http.Request, status int, value any) {
	// The only type-erased surface is serialization of already concrete wire DTOs.
	buffer := boundedBuffer{limit: s.config.MaxResponseBytes}
	if err := json.NewEncoder(&buffer).Encode(value); err != nil {
		status = 503
		buffer.Reset()
		_, _ = buffer.Write([]byte("{\"code\":\"response_limit\",\"detail\":\"Controller response exceeds its configured bound\"}\n"))
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Content-Length", strconv.Itoa(buffer.Len()))
	w.WriteHeader(status)
	if r.Method != http.MethodHead {
		_, _ = w.Write(buffer.Bytes())
	}
}
func state(snapshot Snapshot) (State, error) {
	if snapshot.Mode == "" {
		snapshot.Mode = "manual"
	}
	if snapshot.Mode != "manual" && snapshot.Mode != "automate" {
		return State{}, fmt.Errorf("invalid mode")
	}
	if len(snapshot.SessionID) > 256 || len(snapshot.Status) > 2048 {
		return State{}, errors.New("oversized snapshot metadata")
	}
	result := State{SessionID: snapshot.SessionID, Connected: snapshot.Connected, Mode: snapshot.Mode, Status: Status{snapshot.Status}, ActivePlanID: value(snapshot.ActivePlanID)}
	result.Game = Game{Tick: value(snapshot.Tick), Paused: value(snapshot.Paused), ObservedAt: value(snapshot.ObservedAt), Stale: snapshot.Stale}
	if raw, known := snapshot.Generation.Value(); known {
		if err := raw.Validate(); err != nil {
			return State{}, err
		}
		result.Generation = generation(raw)
	}
	if raw, known := snapshot.Identity.Value(); known {
		if err := raw.Validate(); err != nil {
			return State{}, err
		}
		result.Identity = &Identity{string(raw.Colony), int32(raw.Map), string(raw.Load)}
	}
	if result.Game.Tick != nil && *result.Game.Tick < 0 {
		return State{}, errors.New("negative tick")
	}
	if !result.Connected || result.Identity == nil || result.Game.Tick == nil || result.Game.Paused == nil || result.Game.ObservedAt == nil {
		result.Game.Stale = true
	}
	return result, nil
}
func plan(stored store.PlanState) (Plan, error) {
	if err := stored.Spec.Validate(); err != nil {
		return Plan{}, err
	}
	actions := stored.Spec.Actions()
	if len(actions) > 1024 || len(actions) != len(stored.Progress) {
		return Plan{}, errors.New("unavailable or oversized plan progress")
	}
	result := Plan{ID: stored.Spec.ID(), Revision: stored.Spec.Revision(), Actions: make([]Action, 0, len(actions))}
	for n, action := range actions {
		view := stored.Progress[n].View()
		if view.Action != action.ID() || view.Plan != stored.Spec.ID() || view.Revision != stored.Spec.Revision() || view.Stage == "" {
			return Plan{}, errors.New("mismatched plan progress")
		}
		heldReasons, _ := view.FreshHeldReason()
		projected := Action{ID: action.ID(), Kind: action.Kind(), Progress: Progress{Stage: view.Stage, Attempt: view.Attempt, Tick: view.Tick, Unresolved: view.Unresolved, Receipt: value(view.Receipt), Effect: value(view.Effect), UnsuccessfulReason: value(view.UnsuccessfulReason), HeldReasons: heldReasons}}
		if stored.Progress[n].Action() != action {
			return Plan{}, errors.New("mismatched action progress")
		}
		if building, ok := action.Building(); ok {
			projected.Building = &Building{building.Definition(), building.Cell().X, building.Cell().Z, building.Rotation(), building.Stuff()}
		} else if draft, ok := action.OwnedDraft(); ok {
			projected.Draft = &Draft{draft.Pawn()}
		} else {
			return Plan{}, errors.New("unsupported action")
		}
		if cleanup, known := view.DraftCleanup.Value(); known {
			if projected.Draft == nil {
				return Plan{}, errors.New("building has draft cleanup")
			}
			switch cleanup.Stage {
			case domain.DraftAwaitingClaim, domain.DraftNotAcquired, domain.DraftCleanupRequired, domain.DraftCleanupDispatched, domain.DraftCleanupUncertain, domain.DraftReleased, domain.DraftSuperseded:
			default:
				return Plan{}, errors.New("invalid draft cleanup")
			}
			projected.Progress.DraftCleanup = &DraftCleanup{cleanup.Stage}
		}
		result.Actions = append(result.Actions, projected)
	}
	return result, nil
}

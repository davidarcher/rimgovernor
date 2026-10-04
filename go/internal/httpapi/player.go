package httpapi

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"errors"
	"mime"
	"net/http"
	"net/url"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type PlayerControl interface {
	Resume(context.Context, store.ControlRequest) (store.ControlRecord, error)
	Pause(context.Context, store.ControlRequest) (store.ControlRecord, error)
	State() buildingruntime.ControlState
}
type ControlReader interface {
	CurrentControl(context.Context) (store.ControlRecord, error)
}

// NewWithPlayer explicitly enables authenticated player intent. Dependencies and
// their lifetimes remain owned by the caller; New remains strictly read-only.
func NewWithPlayer(config Config, snapshots SnapshotProvider, plans PlanReader, player PlayerControl, controls ControlReader) (*Server, error) {
	if player == nil || controls == nil {
		return nil, errors.New("player and control reader required")
	}
	var secret [32]byte
	if _, err := rand.Read(secret[:]); err != nil {
		return nil, err
	}
	s, err := New(config, snapshots, plans)
	if err != nil {
		return nil, err
	}
	s.player = player
	s.controls = controls
	s.playerToken = hex.EncodeToString(secret[:])
	return s, nil
}

type controlRecordDTO struct {
	RequestID        string                  `json:"requestId"`
	Kind             store.ControlKind       `json:"kind"`
	Expected         Identity                `json:"expected"`
	Phase            store.ControlPhase      `json:"phase"`
	NativeGeneration domain.NativeGeneration `json:"nativeGeneration,string"`
}
type playerStateDTO struct {
	Enabled          bool        `json:"enabled"`
	ObservationKnown bool        `json:"observationKnown"`
	Generation       *Generation `json:"generation"`
}
type controlDTO struct {
	Record *controlRecordDTO `json:"record"`
	State  playerStateDTO    `json:"state"`
	Error  *Failure          `json:"error"`
}

func playerWorldDTO(world store.World) Identity {
	return Identity{ColonyID: string(world.Colony), LoadToken: string(world.Load), MapID: int32(world.Map)}
}
func projectControl(v store.ControlRecord) (*controlRecordDTO, error) {
	if v == (store.ControlRecord{}) {
		return nil, nil
	}
	q := v.Request
	if buildingRequestID(q.RequestID) != nil || q.World.Validate() != nil {
		return nil, errors.New("invalid control record")
	}
	switch q.Kind {
	case store.ResumeControl, store.PauseControl:
	default:
		return nil, errors.New("unknown control kind")
	}
	switch v.Phase {
	case store.RunningControl:
		if q.Kind != store.ResumeControl || v.NativeGeneration == 0 {
			return nil, errors.New("invalid running record")
		}
	case store.PausedControl:
		if q.Kind != store.PauseControl || v.NativeGeneration != 0 {
			return nil, errors.New("invalid paused record")
		}
	case store.PendingControl, store.RefusedControl, store.UncertainControl:
		if v.NativeGeneration != 0 {
			return nil, errors.New("invalid control generation")
		}
	default:
		return nil, errors.New("unknown control phase")
	}
	return &controlRecordDTO{q.RequestID, q.Kind, playerWorldDTO(q.World), v.Phase, v.NativeGeneration}, nil
}
func projectPlayerState(v buildingruntime.ControlState) (playerStateDTO, error) {
	out := playerStateDTO{Enabled: v.Enabled, ObservationKnown: v.ObservationKnown}
	if !v.ObservationKnown {
		if v.Enabled {
			return out, errors.New("enabled without observation")
		}
		return out, nil
	}
	if err := v.Snapshot.Validate(); err != nil {
		return out, err
	}
	if v.Enabled && (v.Snapshot.Native == 0 || v.Snapshot.Revision == 0) {
		return out, errors.New("invalid live authority")
	}
	out.Generation = generation(v.Snapshot)
	return out, nil
}
func playerFailure(err error) (int, *Failure) {
	switch {
	case errors.Is(err, store.ErrConflict):
		return 409, &Failure{"conflict", "Request conflicts with current intent"}
	case errors.Is(err, store.ErrNotFound):
		return 404, &Failure{"not_found", "Requested intent was not found"}
	case errors.Is(err, store.ErrCapacity):
		return 409, &Failure{"capacity", "Intent history is full"}
	default:
		return 503, &Failure{"unavailable", "Player operation is unavailable; check its request ID before retrying"}
	}
}
func (s *Server) writeControl(w http.ResponseWriter, r *http.Request, record store.ControlRecord, cause error) {
	projected, err := projectControl(record)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	actual, err := projectPlayerState(s.player.State())
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	status := 200
	var failure *Failure
	if cause != nil {
		status, failure = playerFailure(cause)
		if projected != nil && (record.Phase == store.UncertainControl || record.Phase == store.PendingControl) {
			status = 503
			failure = &Failure{"uncertain", "Control outcome is unresolved; inspect this request ID"}
		}
	}
	s.write(w, r, status, controlDTO{projected, actual, failure})
}
func (s *Server) handlePlayer(w http.ResponseWriter, r *http.Request) bool {
	if s.player == nil {
		return false
	}
	path := r.URL.Path
	read := path == "/api/player/session" || path == "/api/player/control" || path == "/api/player/clock" || path == "/api/player/colony"
	write := path == "/api/player/control/resume" || path == "/api/player/control/pause" || path == "/api/player/clock/acknowledge"
	if !read && !write {
		return false
	}
	want := http.MethodGet
	if write {
		want = http.MethodPost
	}
	if r.Method != want {
		w.Header().Set("Allow", want)
		s.failure(w, r, 405, "method_not_allowed", "Method not allowed")
		return true
	}
	if len(r.RequestURI) > 2048 {
		s.failure(w, r, 400, "invalid_request", "Request URL exceeds limit")
		return true
	}
	query, err := url.ParseQuery(r.URL.RawQuery)
	if err != nil {
		s.failure(w, r, 400, "invalid_query", "Invalid query")
		return true
	}
	if write {
		tokens := r.Header.Values("X-RimGovernor-Player")
		if len(tokens) != 1 || subtle.ConstantTimeCompare([]byte(tokens[0]), []byte(s.playerToken)) != 1 {
			s.failure(w, r, 403, "player_auth", "Player session token required")
			return true
		}
		kind, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
		if err != nil || kind != "application/json" {
			s.failure(w, r, 415, "content_type", "Use application/json")
			return true
		}
		if len(query) != 0 || r.URL.ForceQuery || r.ContentLength > buildingRequestLimit {
			s.failure(w, r, 400, "invalid_request", "Mutation requires a bounded body and no query")
			return true
		}
	} else if r.ContentLength != 0 || len(r.TransferEncoding) != 0 {
		s.failure(w, r, 400, "invalid_request", "Read requests require no body")
		return true
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	if path == "/api/player/clock" || path == "/api/player/clock/acknowledge" {
		if len(query) != 0 || r.URL.ForceQuery {
			s.failure(w, r, 400, "invalid_request", "Clock review accepts no query")
		} else {
			s.handleClockReview(ctx, w, r, write)
		}
		return true
	}
	if path == "/api/player/colony" {
		if len(query) != 0 || r.URL.ForceQuery {
			s.failure(w, r, 400, "invalid_request", "Colony status accepts no query")
		} else {
			s.handleColonyStatus(ctx, w, r)
		}
		return true
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return true
	}
	if write {
		kind := store.PauseControl
		if strings.HasSuffix(path, "/resume") {
			kind = store.ResumeControl
		}
		q, err := decodeControl(r.Body, kind)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Invalid control request")
			return true
		}
		if ctx.Err() != nil {
			s.readFailure(w, r, ctx.Err())
			return true
		}
		var record store.ControlRecord
		if q.Kind == store.ResumeControl {
			record, err = s.player.Resume(ctx, q)
		} else {
			record, err = s.player.Pause(ctx, q)
		}
		if err == nil {
			err = ctx.Err()
		}
		s.writeControl(w, r, record, err)
		return true
	}
	if path == "/api/player/session" {
		if len(query) != 0 || r.URL.ForceQuery {
			s.failure(w, r, 400, "invalid_query", "Session accepts no query")
			return true
		}
		s.write(w, r, 200, struct {
			Token string `json:"token"`
			Mode  string `json:"mode"`
		}{s.playerToken, "explicit-player"})
		return true
	}
	if len(query) != 0 || r.URL.ForceQuery {
		s.failure(w, r, 400, "invalid_query", "Control accepts no query")
		return true
	}
	record, err := s.controls.CurrentControl(ctx)
	if errors.Is(err, store.ErrNotFound) {
		err = nil
	}
	if err == nil {
		err = ctx.Err()
	}
	s.writeControl(w, r, record, err)
	return true
}

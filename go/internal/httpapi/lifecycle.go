package httpapi

import (
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"mime"
	"net/http"
	"net/url"

	bridgepkg "github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// errNoLifecycleIdentity means this controller has no fresh, matching
// generation to attach to a lifecycle mutation or to project from a
// completed reply; it never reaches native.
var errNoLifecycleIdentity = errors.New("no current lifecycle identity")

// blockingAttentionID extracts a GABS attention id from a games_call_tool
// Refusal whose structured detail reports status "blocked_by_attention" (see
// bridge.Client.AckAttention). It never inspects the attention's own content
// (severity, summary) to decide anything -- only that GABS named an id to
// acknowledge.
func blockingAttentionID(err error) (string, bool) {
	var refusal *bridgepkg.Refusal
	if !errors.As(err, &refusal) {
		return "", false
	}
	var body struct {
		Status    string `json:"status"`
		Attention struct {
			AttentionID string `json:"attentionId"`
		} `json:"attention"`
	}
	if json.Unmarshal(refusal.Result.Structured, &body) != nil {
		return "", false
	}
	if body.Status != "blocked_by_attention" || body.Attention.AttentionID == "" {
		return "", false
	}
	return body.Attention.AttentionID, true
}

// retryAfterAttentionAck re-issues op once when err is a GABS
// blocked-by-attention refusal and an AttentionAcknowledger is configured; it
// returns the error to use afterward (the retry's outcome, or the original
// err when no retry was attempted or the ack itself failed).
func (s *Server) retryAfterAttentionAck(ctx context.Context, err error, op func() error) error {
	attentionID, ok := blockingAttentionID(err)
	if !ok || s.config.Attention == nil {
		return err
	}
	if ackErr := s.config.Attention.AckAttention(ctx, attentionID); ackErr != nil {
		return err
	}
	return op()
}

type saveRequestDTO struct {
	RequestID string `json:"requestId"`
	SaveName  string `json:"saveName"`
}
type saveReplyDTO struct {
	RequestID  string      `json:"requestId"`
	SaveName   string      `json:"saveName"`
	Identity   Identity    `json:"identity"`
	Tick       domain.Tick `json:"tick,string"`
	Paused     bool        `json:"paused"`
	ByteLength uint64      `json:"byteLength,string"`
}
type loadRequestDTO struct {
	RequestID string `json:"requestId"`
	SaveName  string `json:"saveName"`
	Readiness string `json:"readiness"`
	TimeoutMs uint32 `json:"timeoutMs"`
}
type loadReplyDTO struct {
	RequestID string   `json:"requestId"`
	SaveName  string   `json:"saveName"`
	Identity  Identity `json:"identity"`
	Paused    bool     `json:"paused"`
	Readiness string   `json:"readiness"`
}

// handleLifecycle serves the explicit session lifecycle mutations (Save, Load)
// under the same X-RimGovernor-Player gate as other player writes, plus their
// free (id-gated, unauthenticated) recovery reads -- the same shape as every
// other player-submission lookup in player.go.
func (s *Server) handleLifecycle(w http.ResponseWriter, r *http.Request) bool {
	switch r.URL.Path {
	case "/api/lifecycle/save", "/api/lifecycle/load":
	default:
		return false
	}
	if s.config.Lifecycle == nil || s.playerToken == "" {
		s.failure(w, r, 404, "not_found", "Lifecycle is unavailable")
		return true
	}
	switch r.Method {
	case http.MethodPost:
		s.handleLifecycleMutation(w, r)
	case http.MethodGet:
		s.handleLifecycleRead(w, r)
	default:
		w.Header().Set("Allow", "GET, POST")
		s.failure(w, r, 405, "method_not_allowed", "Lifecycle requires GET or POST")
	}
	return true
}

func (s *Server) handleLifecycleMutation(w http.ResponseWriter, r *http.Request) {
	tokens := r.Header.Values("X-RimGovernor-Player")
	if len(tokens) != 1 || subtle.ConstantTimeCompare([]byte(tokens[0]), []byte(s.playerToken)) != 1 {
		s.failure(w, r, 403, "player_auth", "Player session token required")
		return
	}
	kind, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || kind != "application/json" {
		s.failure(w, r, 415, "content_type", "Use application/json")
		return
	}
	if r.URL.RawQuery != "" || r.URL.ForceQuery || r.ContentLength > buildingRequestLimit {
		s.failure(w, r, 400, "invalid_request", "Mutation requires a bounded body and no query")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	if r.URL.Path == "/api/lifecycle/save" {
		s.handleLifecycleSave(w, r, ctx)
		return
	}
	s.handleLifecycleLoad(w, r, ctx)
}

func (s *Server) handleLifecycleRead(w http.ResponseWriter, r *http.Request) {
	if r.ContentLength != 0 || len(r.TransferEncoding) != 0 {
		s.failure(w, r, 400, "invalid_request", "Read requests require no body")
		return
	}
	query, err := url.ParseQuery(r.URL.RawQuery)
	if err != nil {
		s.failure(w, r, 400, "invalid_query", "Invalid query")
		return
	}
	ids := query["requestId"]
	if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
		s.failure(w, r, 400, "invalid_query", "One requestId is required")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	if r.URL.Path == "/api/lifecycle/save" {
		reply, _, err := s.config.Lifecycle.ReadSave(ctx, ids[0])
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, projectSaveCompleted(reply.GetCompleted()))
		return
	}
	reply, _, err := s.config.Lifecycle.ReadLoad(ctx, ids[0])
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		err = s.retryAfterAttentionAck(ctx, err, func() error {
			var opErr error
			reply, _, opErr = s.config.Lifecycle.ReadLoad(ctx, ids[0])
			if opErr == nil {
				opErr = ctx.Err()
			}
			return opErr
		})
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	dto, err := projectLoadCompleted(reply.GetCompleted())
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}

// currentLifecycleIdentity reads the fresh, matching identity and control mode
// this controller currently observes. Save and Load
// both require a live, non-stale, matching generation; the caller decides
// whether Save's additional manual-mode requirement also applies.
func (s *Server) currentLifecycleIdentity(ctx context.Context) (*c.Identity, string, error) {
	snapshot, err := s.snapshots.Snapshot(ctx)
	if err != nil {
		return nil, "", err
	}
	identity, knownIdentity := snapshot.Identity.Value()
	_, knownGeneration := snapshot.Generation.Value()
	if !snapshot.Connected || snapshot.Stale || !knownIdentity || !knownGeneration {
		return nil, "", errNoLifecycleIdentity
	}
	wire := &c.Identity{ColonyId: proto.String(string(identity.Colony)), LoadToken: proto.String(string(identity.Load)), MapId: proto.Int32(int32(identity.Map))}
	return wire, snapshot.Mode, nil
}

func (s *Server) handleLifecycleSave(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	var body saveRequestDTO
	if err := decodeMediaBody(r, &body); err != nil || buildingRequestID(body.RequestID) != nil || body.SaveName == "" {
		s.failure(w, r, 400, "invalid_request", "requestId and saveName are required")
		return
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	wire, mode, err := s.currentLifecycleIdentity(ctx)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	if mode != "manual" {
		s.failure(w, r, 409, "conflict", "Save requires manual control")
		return
	}
	// No expected tick: the snapshot's tick is a cached observation up to a
	// refresh interval old, so a save posted right after a pause carried the
	// pre-pause tick and native refused it as moved (#322). The completed
	// reply must report the game paused and the identity above unchanged;
	// its tick is the checkpoint's.
	request := &l.SaveRequest{
		Player:   &l.PlayerLifecycleContext{Identity: wire, PlayerDirection: proto.Uint64(bridgepkg.LifecycleDirection), RequestId: proto.String(body.RequestID)},
		SaveName: proto.String(body.SaveName),
	}
	reply, _, err := s.config.Lifecycle.Save(ctx, request)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 201, projectSaveCompleted(reply.GetCompleted()))
}

func (s *Server) handleLifecycleLoad(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	var body loadRequestDTO
	if err := decodeMediaBody(r, &body); err != nil || buildingRequestID(body.RequestID) != nil || body.SaveName == "" || body.TimeoutMs < 1000 || body.TimeoutMs > 120000 {
		s.failure(w, r, 400, "invalid_request", "requestId, saveName and a timeoutMs of 1000-120000 are required")
		return
	}
	var readiness l.Readiness
	switch body.Readiness {
	case "map":
		readiness = l.Readiness_READINESS_MAP
	case "visual":
		readiness = l.Readiness_READINESS_VISUAL
	default:
		s.failure(w, r, 400, "invalid_request", "readiness must be map or visual")
		return
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	// A cold bootstrap load (no colony currently observed, e.g. the game is
	// still at its main menu) has no prior identity to assert against, so
	// ExpectedPlayer is left nil in that case; the proto itself treats it as
	// optional (see validateLoadRequest in internal/bridge/lifecycle_load.go).
	// Any other read failure still aborts the request.
	var expectedPlayer *l.PlayerLifecycleContext
	wire, _, err := s.currentLifecycleIdentity(ctx)
	switch {
	case err == nil:
		expectedPlayer = &l.PlayerLifecycleContext{
			Identity:        wire,
			PlayerDirection: proto.Uint64(bridgepkg.LifecycleDirection),
			RequestId:       proto.String(body.RequestID),
		}
	case errors.Is(err, errNoLifecycleIdentity):
	default:
		s.readFailure(w, r, err)
		return
	}
	request := &l.LoadRequest{
		RequestId:      proto.String(body.RequestID),
		SaveName:       proto.String(body.SaveName),
		Readiness:      readiness.Enum(),
		TimeoutMs:      proto.Uint32(body.TimeoutMs),
		ExpectedPlayer: expectedPlayer,
	}
	reply, _, err := s.config.Lifecycle.Load(ctx, request)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		err = s.retryAfterAttentionAck(ctx, err, func() error {
			var opErr error
			reply, _, opErr = s.config.Lifecycle.Load(ctx, request)
			if opErr == nil {
				opErr = ctx.Err()
			}
			return opErr
		})
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	dto, err := projectLoadCompleted(reply.GetCompleted())
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 201, dto)
}

func projectSaveCompleted(v *l.SaveCompleted) saveReplyDTO {
	observed := v.GetContext()
	identity := observed.GetIdentity()
	return saveReplyDTO{
		RequestID:  v.GetRequestId(),
		SaveName:   v.GetSaveName(),
		Identity:   Identity{ColonyID: identity.GetColonyId(), MapID: identity.GetMapId(), LoadToken: identity.GetLoadToken()},
		Tick:       domain.Tick(observed.GetTick()),
		Paused:     v.GetPaused(),
		ByteLength: v.GetByteLength(),
	}
}
func projectLoadCompleted(v *l.LoadCompleted) (loadReplyDTO, error) {
	loaded := v.GetLoaded()
	identity := loaded.GetContext().GetIdentity()
	var readiness string
	switch v.GetReadiness() {
	case l.Readiness_READINESS_MAP:
		readiness = "map"
	case l.Readiness_READINESS_VISUAL:
		readiness = "visual"
	default:
		return loadReplyDTO{}, errNoLifecycleIdentity
	}
	return loadReplyDTO{
		RequestID: v.GetRequestId(),
		SaveName:  v.GetSaveName(),
		Identity:  Identity{ColonyID: identity.GetColonyId(), MapID: identity.GetMapId(), LoadToken: identity.GetLoadToken()},
		Paused:    loaded.GetPaused(),
		Readiness: readiness,
	}, nil
}

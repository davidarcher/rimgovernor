package httpapi

import (
	"context"
	"errors"
	"net/http"
	"strings"

	bridgepkg "github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// NewColonySpecDTO is the typed spec of a new colony. Required fields left at
// their zero value fail the shared bridge validation; the optional ones are
// pointers or empty strings.
type NewColonySpecDTO struct {
	Scenario           string               `json:"scenario"`
	ColonistCount      uint32               `json:"colonistCount"`
	Seed               string               `json:"seed"`
	Biomes             []string             `json:"biomes"`
	FlatTile           bool                 `json:"flatTile"`
	Difficulty         string               `json:"difficulty"`
	Storyteller        string               `json:"storyteller"`
	MinTemperature     *float32             `json:"minTemperature"`
	MaxTemperature     *float32             `json:"maxTemperature"`
	WorldTemperature   string               `json:"worldTemperature"`
	MapSize            uint32               `json:"mapSize"`
	PlanetCoverage     float32              `json:"planetCoverage"`
	SaveName           string               `json:"saveName"`
	Ideoligion         *IdeoligionDesignDTO `json:"ideoligion,omitempty"`
	GovernorIdeoligion bool                 `json:"governorIdeoligion,omitempty"`
	NewColonyPartsDTO
}
type IdeoligionDesignDTO struct {
	Memes    []string `json:"memes"`
	Precepts []string `json:"precepts"`
	Fluid    *bool    `json:"fluid"`
}

// Proto builds the wire spec. A malformed scenario-part enum name is an error;
// range and presence rules are the bridge's (ValidateNewColonySpec).
func (d NewColonySpecDTO) Proto() (*l.NewColonySpec, error) {
	spec := &l.NewColonySpec{
		Scenario:       proto.String(d.Scenario),
		ColonistCount:  proto.Uint32(d.ColonistCount),
		Seed:           proto.String(d.Seed),
		Biomes:         d.Biomes,
		FlatTile:       proto.Bool(d.FlatTile),
		Difficulty:     proto.String(d.Difficulty),
		Storyteller:    proto.String(d.Storyteller),
		MinTemperature: d.MinTemperature,
		MaxTemperature: d.MaxTemperature,
		MapSize:        proto.Uint32(d.MapSize),
		PlanetCoverage: proto.Float32(d.PlanetCoverage),
		SaveName:       proto.String(d.SaveName),
	}
	if d.WorldTemperature != "" {
		spec.WorldTemperature = proto.String(d.WorldTemperature)
	}
	if d.Ideoligion != nil {
		spec.Ideoligion = &c.IdeoligionDesign{Memes: d.Ideoligion.Memes, Precepts: d.Ideoligion.Precepts, Fluid: d.Ideoligion.Fluid}
	}
	if d.GovernorIdeoligion {
		spec.GovernorIdeoligion = proto.Bool(true)
	}
	if err := applyNewColonyParts(spec, d.NewColonyPartsDTO); err != nil {
		return nil, err
	}
	return spec, nil
}

type NewColonyRequestDTO struct {
	RequestID string           `json:"requestId"`
	TimeoutMs uint32           `json:"timeoutMs"`
	Spec      NewColonySpecDTO `json:"spec"`
}

// A new colony reply is one of two DTOs on POST and GET, told apart by Status:
// "pending" (the poller's ordinary case) or "completed".
type newColonyPendingDTO struct {
	Status      string `json:"status"`
	RequestID   string `json:"requestId"`
	Phase       string `json:"phase"`
	Detail      string `json:"detail"`
	ElapsedMs   uint64 `json:"elapsedMs"`
	RerollCount uint32 `json:"rerollCount"`
}
type newColonyCompletedDTO struct {
	Status     string      `json:"status"`
	RequestID  string      `json:"requestId"`
	SaveName   string      `json:"saveName"`
	Identity   Identity    `json:"identity"`
	Tick       domain.Tick `json:"tick,string"`
	Paused     bool        `json:"paused"`
	ByteLength uint64      `json:"byteLength,string"`
	Seed       string      `json:"seed"`
}

var newColonyPhaseNames = map[l.NewColonyPhase]string{
	l.NewColonyPhase_NEW_COLONY_PHASE_GENERATING_WORLD:  "generating_world",
	l.NewColonyPhase_NEW_COLONY_PHASE_CHOOSING_TILE:     "choosing_tile",
	l.NewColonyPhase_NEW_COLONY_PHASE_ROLLING_COLONISTS: "rolling_colonists",
	l.NewColonyPhase_NEW_COLONY_PHASE_GENERATING_MAP:    "generating_map",
	l.NewColonyPhase_NEW_COLONY_PHASE_FINISHING:         "finishing",
	l.NewColonyPhase_NEW_COLONY_PHASE_SAVING:            "saving",
}

func (s *Server) handleLifecycleNew(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	var body NewColonyRequestDTO
	if err := decodeMediaBody(r, &body); err != nil || buildingRequestID(body.RequestID) != nil {
		s.failure(w, r, 400, "invalid_request", "requestId and a spec are required")
		return
	}
	spec, err := body.Spec.Proto()
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid new colony request: scenario part "+err.Error())
		return
	}
	request := &l.NewColonyRequest{RequestId: proto.String(body.RequestID), Spec: spec, TimeoutMs: proto.Uint32(body.TimeoutMs)}
	if err := bridgepkg.ValidateNewColonyRequest(request); err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid new colony request: "+strings.TrimPrefix(err.Error(), bridgepkg.ErrContract.Error()+": "))
		return
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	call := func() (*l.NewColonyReply, error) {
		reply, _, err := s.config.Lifecycle.NewColony(ctx, request)
		if err == nil {
			err = ctx.Err()
		}
		return reply, err
	}
	reply, err := call()
	if err != nil {
		err = s.retryAfterAttentionAck(ctx, err, func() (opErr error) { reply, opErr = call(); return })
	}
	s.writeNewColony(w, r, reply, err, 201, 202)
}

func (s *Server) handleLifecycleReadNew(w http.ResponseWriter, r *http.Request, ctx context.Context, requestID string) {
	call := func() (*l.NewColonyReply, error) {
		reply, _, err := s.config.Lifecycle.ReadNewColony(ctx, requestID)
		if err == nil {
			err = ctx.Err()
		}
		return reply, err
	}
	reply, err := call()
	if err != nil {
		err = s.retryAfterAttentionAck(ctx, err, func() (opErr error) { reply, opErr = call(); return })
	}
	s.writeNewColony(w, r, reply, err, 200, 200)
}

// writeNewColony projects a bridge outcome: a completed reply, a still-pending
// one (the poller's ordinary case), a superseded request (409) or a refusal.
func (s *Server) writeNewColony(w http.ResponseWriter, r *http.Request, reply *l.NewColonyReply, err error, completedStatus, pendingStatus int) {
	var pending *bridgepkg.NewColonyPending
	var superseded *bridgepkg.NewColonySuperseded
	var refused *bridgepkg.NativeFailure
	switch {
	case errors.As(err, &pending):
		s.write(w, r, pendingStatus, projectNewColonyPending(pending.Value))
	case errors.As(err, &superseded):
		s.failure(w, r, 409, "superseded", superseded.Value.GetDetail())
	case errors.As(err, &refused):
		s.failure(w, r, newColonyRefusalStatus(refused.Value.GetCode()), "native_"+strings.ToLower(strings.TrimPrefix(refused.Value.GetCode().String(), "FAILURE_CODE_")), refused.Value.GetDetail())
	case err != nil:
		s.readFailure(w, r, err)
	default:
		s.write(w, r, completedStatus, projectNewColonyCompleted(reply.GetCompleted()))
	}
}

func newColonyRefusalStatus(code c.FailureCode) int {
	switch code {
	case c.FailureCode_FAILURE_CODE_INVALID_REQUEST:
		return 400
	case c.FailureCode_FAILURE_CODE_NOT_FOUND:
		return 404
	case c.FailureCode_FAILURE_CODE_OWNER_CONFLICT, c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT:
		return 409
	default:
		return 502
	}
}

func projectNewColonyPending(v *l.NewColonyPending) newColonyPendingDTO {
	return newColonyPendingDTO{
		Status: "pending", RequestID: v.GetRequestId(), Phase: newColonyPhaseNames[v.GetPhase()],
		Detail: v.GetDetail(), ElapsedMs: v.GetElapsedMs(), RerollCount: v.GetRerollCount(),
	}
}

func projectNewColonyCompleted(v *l.NewColonyCompleted) newColonyCompletedDTO {
	observed := v.GetContext()
	identity := observed.GetIdentity()
	return newColonyCompletedDTO{
		Status: "completed", RequestID: v.GetRequestId(), SaveName: v.GetSaveName(),
		Identity:   Identity{ColonyID: identity.GetColonyId(), MapID: identity.GetMapId(), LoadToken: identity.GetLoadToken()},
		Tick:       domain.Tick(observed.GetTick()),
		Paused:     v.GetPaused(),
		ByteLength: v.GetByteLength(),
		Seed:       v.GetSeed(),
	}
}

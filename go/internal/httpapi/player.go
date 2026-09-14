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

type PlayerBuildings interface {
	SubmitDraft(context.Context, store.DraftSubmissionRequest) (store.DraftSubmission, bool, error)
	Submit(context.Context, store.SubmissionRequest) (store.Submission, bool, error)
	SubmitCaravanDeparture(context.Context, store.CaravanDepartureSubmissionRequest) (store.CaravanDepartureSubmission, bool, error)
	SubmitQuestAccept(context.Context, store.QuestAcceptSubmissionRequest) (store.QuestAcceptSubmission, bool, error)
	SubmitSettlementGift(context.Context, store.SettlementGiftSubmissionRequest) (store.SettlementGiftSubmission, bool, error)
	SubmitQuestFulfill(context.Context, store.QuestFulfillSubmissionRequest) (store.QuestFulfillSubmission, bool, error)
	SubmitTrade(context.Context, store.TradeSubmissionRequest) (store.TradeSubmission, bool, error)
	SubmitZoneCreate(context.Context, store.ZoneCreateSubmissionRequest) (store.ZoneCreateSubmission, bool, error)
	SubmitZoneEdit(context.Context, store.ZoneEditSubmissionRequest) (store.ZoneEditSubmission, bool, error)
	SubmitResearchSelect(context.Context, store.ResearchSelectSubmissionRequest) (store.ResearchSelectSubmission, bool, error)
	SubmitTravelCaravan(context.Context, store.TravelCaravanSubmissionRequest) (store.TravelCaravanSubmission, bool, error)
	SubmitBuildRoom(context.Context, store.BuildRoomSubmissionRequest) (store.BuildRoomSubmission, bool, error)
	SubmitCancelConstruction(context.Context, store.CancelConstructionSubmissionRequest) (store.CancelConstructionSubmission, bool, error)
	SubmitRelocateConstruction(context.Context, store.RelocateConstructionSubmissionRequest) (store.RelocateConstructionSubmission, bool, error)
	SubmitTend(context.Context, store.TendSubmissionRequest) (store.TendSubmission, bool, error)
	SubmitRescue(context.Context, store.RescueSubmissionRequest) (store.RescueSubmission, bool, error)
	Acquire(context.Context, store.ControlRequest) (store.ControlRecord, error)
	Manual(context.Context, store.ControlRequest) (store.ControlRecord, error)
	State() buildingruntime.ControlState
}
type ControlReader interface {
	LookupDraftSubmission(context.Context, string) (store.DraftSubmission, error)
	CurrentControl(context.Context) (store.ControlRecord, error)
	LookupControl(context.Context, string) (store.ControlRecord, error)
	LookupSubmission(context.Context, string) (store.Submission, error)
	LookupCaravanDepartureSubmission(context.Context, string) (store.CaravanDepartureSubmission, error)
	LookupQuestAcceptSubmission(context.Context, string) (store.QuestAcceptSubmission, error)
	LookupSettlementGiftSubmission(context.Context, string) (store.SettlementGiftSubmission, error)
	LookupQuestFulfillSubmission(context.Context, string) (store.QuestFulfillSubmission, error)
	LookupTradeSubmission(context.Context, string) (store.TradeSubmission, error)
	LookupZoneCreateSubmission(context.Context, string) (store.ZoneCreateSubmission, error)
	LookupZoneEditSubmission(context.Context, string) (store.ZoneEditSubmission, error)
	LookupResearchSelectSubmission(context.Context, string) (store.ResearchSelectSubmission, error)
	LookupTravelCaravanSubmission(context.Context, string) (store.TravelCaravanSubmission, error)
	LookupBuildRoomSubmission(context.Context, string) (store.BuildRoomSubmission, error)
	LookupCancelConstructionSubmission(context.Context, string) (store.CancelConstructionSubmission, error)
	LookupRelocateConstructionSubmission(context.Context, string) (store.RelocateConstructionSubmission, error)
	LookupTendSubmission(context.Context, string) (store.TendSubmission, error)
	LookupRescueSubmission(context.Context, string) (store.RescueSubmission, error)
}

// NewWithPlayer explicitly enables authenticated player intent. Dependencies and
// their lifetimes remain owned by the caller; New remains strictly read-only.
func NewWithPlayer(config Config, snapshots SnapshotProvider, plans PlanReader, player PlayerBuildings, controls ControlReader) (*Server, error) {
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

type submissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Building  Building            `json:"building"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}
type controlRecordDTO struct {
	RequestID         string                  `json:"requestId"`
	Kind              store.ControlKind       `json:"kind"`
	Expected          Identity                `json:"expected"`
	PlanID            *domain.PlanID          `json:"planId"`
	Revision          domain.PlanRevision     `json:"revision,string"`
	ExpectedDirection domain.DirectionID      `json:"expectedDirection,string"`
	Direction         domain.DirectionID      `json:"direction,string"`
	Phase             store.ControlPhase      `json:"phase"`
	NativeGeneration  domain.NativeGeneration `json:"nativeGeneration,string"`
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
func projectSubmission(v store.Submission) (submissionDTO, error) {
	var zero submissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid submission")
	}
	b := v.Request.Building
	if _, err := domain.NewBuilding(b.Definition(), b.Cell(), b.Rotation(), b.Stuff()); err != nil {
		return zero, err
	}
	return submissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Building{b.Definition(), b.Cell().X, b.Cell().Z, b.Rotation(), b.Stuff()}, v.Plan, v.Action, v.Revision}, nil
}
func projectControl(v store.ControlRecord) (*controlRecordDTO, error) {
	if v == (store.ControlRecord{}) {
		return nil, nil
	}
	q := v.Request
	if buildingRequestID(q.RequestID) != nil || q.World.Validate() != nil || v.Direction == 0 {
		return nil, errors.New("invalid control record")
	}
	var plan *domain.PlanID
	switch q.Kind {
	case store.AcquireControl:
		if buildingRequestID(string(q.Plan)) != nil || q.Revision == 0 {
			return nil, errors.New("invalid acquire record")
		}
		plan = &q.Plan
	case store.ManualControl:
		if q.Plan != "" || q.Revision != 0 || q.ExpectedDirection != 0 {
			return nil, errors.New("invalid manual record")
		}
	default:
		return nil, errors.New("unknown control kind")
	}
	switch v.Phase {
	case store.GrantedControl:
		if q.Kind != store.AcquireControl || v.NativeGeneration == 0 {
			return nil, errors.New("invalid grant")
		}
	case store.DisabledControl:
		if q.Kind != store.ManualControl || v.NativeGeneration != 0 {
			return nil, errors.New("invalid disable")
		}
	case store.PendingControl, store.RefusedControl, store.UncertainControl:
		if v.NativeGeneration != 0 {
			return nil, errors.New("invalid control generation")
		}
	default:
		return nil, errors.New("unknown control phase")
	}
	return &controlRecordDTO{q.RequestID, q.Kind, playerWorldDTO(q.World), plan, q.Revision, q.ExpectedDirection, v.Direction, v.Phase, v.NativeGeneration}, nil
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
	if v.Enabled && (v.Snapshot.Native == 0 || v.Snapshot.Direction == 0 || v.Snapshot.Revision == 0) {
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
	read := path == "/api/player/session" || path == "/api/player/control" || (path == "/api/buildings/submission" || path == "/api/drafts/submission") || path == "/api/player/clock" || path == "/api/player/world-evaluation" || path == "/api/player/work-preferences" || path == "/api/caravan-departures/submission" || path == "/api/quest-accepts/submission" || path == "/api/settlement-gifts/submission" || path == "/api/quest-fulfills/submission" || path == "/api/trades/submission" || path == "/api/trade-economies/submission" || path == "/api/zone-creates/submission" || path == "/api/zone-edits/submission" || path == "/api/research-selects/submission" || path == "/api/travel-caravans/submission" || path == "/api/player/population-policy" || path == "/api/player/population-policy/submission" || path == "/api/player/expedition-policy" || path == "/api/player/expedition-policy/submission" || path == "/api/player/population-decision" || path == "/api/player/population-decision/submission" || path == "/api/player/resource-policy" || path == "/api/player/resource-policy/submission" || path == "/api/player/goals" || path == "/api/player/goals/submission" || path == "/api/player/adopt-room" || path == "/api/player/adopt-room/submission" || path == "/api/build-rooms/submission" || path == "/api/cancel-constructions/submission" || path == "/api/relocate-constructions/submission" || path == "/api/tends/submission" || path == "/api/rescues/submission"
	write :=path == "/api/drafts/plans" || path == "/api/buildings/plans" || path == "/api/player/control/acquire" || path == "/api/player/control/manual" || path == "/api/player/clock/acknowledge" || path == "/api/player/work-preferences/replace" || path == "/api/caravan-departures/plans" || path == "/api/quest-accepts/plans" || path == "/api/settlement-gifts/plans" || path == "/api/quest-fulfills/plans" || path == "/api/trades/plans" || path == "/api/trade-economies/plans" || path == "/api/zone-creates/plans" || path == "/api/zone-edits/plans" || path == "/api/research-selects/plans" || path == "/api/travel-caravans/plans" || path == "/api/player/population-policy/replace" || path == "/api/player/expedition-policy/update" || path == "/api/player/population-decision/replace" || path == "/api/player/resource-policy/update" || path == "/api/player/goals/activate" || path == "/api/player/goals/cancel" || path == "/api/player/adopt-room/claim" || path == "/api/build-rooms/plans" || path == "/api/cancel-constructions/plans" || path == "/api/relocate-constructions/plans" || path == "/api/tends/plans" || path == "/api/rescues/plans"
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
	if path == "/api/player/work-preferences" || path == "/api/player/work-preferences/replace" {
		s.handleWorkPreferences(ctx, w, r, query, write)
		return true
	}
	if strings.HasPrefix(path, "/api/player/population-policy") {
		s.handlePopulationPolicy(ctx, w, r, query, path)
		return true
	}
	if strings.HasPrefix(path, "/api/player/expedition-policy") {
		s.handleExpeditionPolicy(ctx, w, r, query, path)
		return true
	}
	if strings.HasPrefix(path, "/api/player/population-decision") {
		s.handlePopulationDecision(ctx, w, r, query, path)
		return true
	}
	if strings.HasPrefix(path, "/api/player/resource-policy") {
		s.handleResourcePolicy(ctx, w, r, query, path)
		return true
	}
	if strings.HasPrefix(path, "/api/player/adopt-room") {
		s.handleAdoptRoom(ctx, w, r, query, path)
		return true
	}
	if strings.HasPrefix(path, "/api/player/goals") {
		s.handlePlayerGoals(ctx, w, r, query, path)
		return true
	}
	if path == "/api/player/clock" || path == "/api/player/clock/acknowledge" {
		if len(query) != 0 || r.URL.ForceQuery {
			s.failure(w, r, 400, "invalid_request", "Clock review accepts no query")
		} else {
			s.handleClockReview(ctx, w, r, write)
		}
		return true
	}
	if path == "/api/player/world-evaluation" {
		if len(query) != 0 || r.URL.ForceQuery {
			s.failure(w, r, 400, "invalid_request", "World evaluation accepts no query")
		} else {
			s.handleWorldEvaluation(ctx, w, r)
		}
		return true
	}
	if err := ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return true
	}
	if write {
		if path == "/api/drafts/plans" {
			s.submitDraft(w, r, ctx)
			return true
		}
		if path == "/api/buildings/plans" {
			q, err := decodeBuildingSubmission(r.Body)
			if err != nil {
				s.failure(w, r, 400, "invalid_request", "Invalid building submission")
				return true
			}
			if ctx.Err() != nil {
				s.readFailure(w, r, ctx.Err())
				return true
			}
			v, created, err := s.player.Submit(ctx, q)
			if err != nil {
				status, failure := playerFailure(err)
				s.write(w, r, status, failure)
				return true
			}
			dto, err := projectSubmission(v)
			if err != nil {
				s.readFailure(w, r, err)
				return true
			}
			status := 200
			if created {
				status = 201
			}
			s.write(w, r, status, dto)
			return true
		}
		if path == "/api/caravan-departures/plans" {
			s.submitCaravanDeparture(w, r, ctx)
			return true
		}
		if path == "/api/quest-accepts/plans" {
			s.submitQuestAccept(w, r, ctx)
			return true
		}
		if path == "/api/settlement-gifts/plans" {
			s.submitSettlementGift(w, r, ctx)
			return true
		}
		if path == "/api/quest-fulfills/plans" {
			s.submitQuestFulfill(w, r, ctx)
			return true
		}
		if path == "/api/trades/plans" {
			s.submitTrade(w, r, ctx)
			return true
		}
		if path == "/api/trade-economies/plans" {
			s.submitTradeEconomy(w, r, ctx)
			return true
		}
		if path == "/api/zone-creates/plans" {
			s.submitZoneCreate(w, r, ctx)
			return true
		}
		if path == "/api/zone-edits/plans" {
			s.submitZoneEdit(w, r, ctx)
			return true
		}
		if path == "/api/cancel-constructions/plans" {
			s.submitCancelConstruction(w, r, ctx)
			return true
		}
		if path == "/api/relocate-constructions/plans" {
			s.submitRelocateConstruction(w, r, ctx)
			return true
		}
		if path == "/api/research-selects/plans" {
			s.submitResearchSelect(w, r, ctx)
			return true
		}
		if path == "/api/travel-caravans/plans" {
			s.submitTravelCaravan(w, r, ctx)
			return true
		}
		if path == "/api/tends/plans" {
			s.submitTend(w, r, ctx)
			return true
		}
		if path == "/api/rescues/plans" {
			s.submitRescue(w, r, ctx)
			return true
		}
		if path == "/api/build-rooms/plans" {
			s.submitBuildRoom(w, r, ctx)
			return true
		}
		var q store.ControlRequest
		var err error
		if strings.HasSuffix(path, "/acquire") {
			q, err = decodeBuildingAcquire(r.Body)
		} else {
			q, err = decodeBuildingManual(r.Body)
		}
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Invalid control request")
			return true
		}
		if ctx.Err() != nil {
			s.readFailure(w, r, ctx.Err())
			return true
		}
		var record store.ControlRecord
		if q.Kind == store.AcquireControl {
			record, err = s.player.Acquire(ctx, q)
		} else {
			record, err = s.player.Manual(ctx, q)
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
	ids := query["requestId"]
	submissionLookup := path == "/api/buildings/submission" || path == "/api/drafts/submission" || path == "/api/caravan-departures/submission" || path == "/api/quest-accepts/submission" || path == "/api/settlement-gifts/submission" || path == "/api/quest-fulfills/submission" || path == "/api/trades/submission" || path == "/api/trade-economies/submission" || path == "/api/zone-creates/submission" || path == "/api/zone-edits/submission" || path == "/api/research-selects/submission" || path == "/api/travel-caravans/submission" || path == "/api/build-rooms/submission" || path == "/api/cancel-constructions/submission" || path == "/api/relocate-constructions/submission" || path == "/api/tends/submission" || path == "/api/rescues/submission"
	if (len(query) != 0 && (len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil)) || (submissionLookup && len(ids) != 1) {
		s.failure(w, r, 400, "invalid_query", "One requestId is required")
		return true
	}
	if path == "/api/drafts/submission" {
		s.lookupDraft(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/caravan-departures/submission" {
		s.lookupCaravanDeparture(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/quest-accepts/submission" {
		s.lookupQuestAccept(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/settlement-gifts/submission" {
		s.lookupSettlementGift(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/quest-fulfills/submission" {
		s.lookupQuestFulfill(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/trades/submission" {
		s.lookupTrade(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/trade-economies/submission" {
		s.lookupTradeEconomy(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/zone-creates/submission" {
		s.lookupZoneCreate(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/research-selects/submission" {
		s.lookupResearchSelect(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/zone-edits/submission" {
		s.lookupZoneEdit(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/travel-caravans/submission" {
		s.lookupTravelCaravan(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/tends/submission" {
		s.lookupTend(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/rescues/submission" {
		s.lookupRescue(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/build-rooms/submission" {
		s.lookupBuildRoom(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/cancel-constructions/submission" {
		s.lookupCancelConstruction(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/relocate-constructions/submission" {
		s.lookupRelocateConstruction(w, r, ctx, ids[0])
		return true
	}
	if path == "/api/buildings/submission" {
		v, err := s.controls.LookupSubmission(ctx, ids[0])
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return true
		}
		dto, err := projectSubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return true
		}
		s.write(w, r, 200, dto)
		return true
	}
	var record store.ControlRecord
	if len(ids) == 1 {
		record, err = s.controls.LookupControl(ctx, ids[0])
	} else {
		record, err = s.controls.CurrentControl(ctx)
		if errors.Is(err, store.ErrNotFound) {
			err = nil
		}
	}
	if err == nil {
		err = ctx.Err()
	}
	s.writeControl(w, r, record, err)
	return true
}

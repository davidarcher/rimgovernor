package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// playerAdoptRoom is an optional player capability, asserted the same way
// playerGoals is, so enabling the adoption routes never widens the required
// PlayerBuildings interface.
type playerAdoptRoom interface {
	SubmitRoomAdoption(context.Context, store.RoomAdoptionSubmissionRequest) (store.RoomAdoptionSubmission, bool, error)
	LookupRoomAdoptionSubmission(context.Context, string) (store.RoomAdoptionSubmission, error)
	AdoptedShelter(context.Context, store.World) (store.RoomAdoptionSubmission, error)
}

// AdoptedRoom is the wire form of one adopted room: the inspected rectangle and
// entrance side, and for a nonrectangular room the exact observed interior and
// boundary door. interiorCells and entranceCell are present together or not at
// all, the same rule Python's AdoptRoom geometry validator enforces.
type AdoptedRoom struct {
	X            int32           `json:"x"`
	Z            int32           `json:"z"`
	Width        int32           `json:"width"`
	Height       int32           `json:"height"`
	Entrance     domain.Rotation `json:"entrance"`
	InteriorCell []ZoneCell      `json:"interiorCells,omitempty"`
	EntranceCell *ZoneCell       `json:"entranceCell,omitempty"`
}

// AdoptedNativeRoom is the player's inspection of the native room being claimed:
// which room the game reported, how many cells it held, and the role it carried.
type AdoptedNativeRoom struct {
	RoomID    string `json:"roomId"`
	Cells     int32  `json:"cells"`
	Role      string `json:"role"`
	RoleLabel string `json:"roleLabel"`
}

type roomAdoptionDTO struct {
	RequestID string            `json:"requestId"`
	Expected  Identity          `json:"expected"`
	IntentID  string            `json:"intentId"`
	Room      AdoptedRoom       `json:"room"`
	Native    AdoptedNativeRoom `json:"native"`
	// State is the completed shelter goal's state now, which is what the
	// request produced for a freshly accepted request and may be newer when an
	// older request ID is replayed.
	State goalStateDTO `json:"state"`
	// Orders restates Python's own reply so a caller cannot mistake adoption
	// for construction.
	Orders string `json:"orders"`
}

const adoptionOrders = "Existing construction preserved; no new construction issued"

func decodeCells(raw json.RawMessage) ([]domain.Cell, error) {
	var wire []ZoneCell
	if err := json.Unmarshal(raw, &wire); err != nil {
		return nil, err
	}
	if len(wire) == 0 {
		return nil, errors.New("interiorCells must not be empty")
	}
	out := make([]domain.Cell, 0, len(wire))
	for _, cell := range wire {
		out = append(out, domain.Cell{X: cell.X, Z: cell.Z})
	}
	return out, nil
}

// decodeRoomAdoption reads one adoption. Like decodeGoalCreate it takes the
// snapshot whole rather than assembling one here: this server observes no native
// state and must not invent a generation the completed goal is then judged
// against.
func decodeRoomAdoption(reader io.Reader) (store.RoomAdoptionSubmissionRequest, error) {
	var q store.RoomAdoptionSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "expectedDirection", "planId", "intentId", "room", "native", "tick")
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["requestId"], &q.RequestID); err != nil {
		return q, err
	}
	if err = buildingRequestID(q.RequestID); err != nil {
		return q, err
	}
	world, err := buildingWorld(fields["expected"])
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["intentId"], &q.IntentID); err != nil {
		return q, err
	}
	if err = domain.ValidateRoomIntent(q.IntentID); err != nil {
		return q, err
	}
	if q.Adoption, err = decodeAdoptedRoom(fields["room"]); err != nil {
		return q, err
	}
	if q.Evidence, err = decodeAdoptedNativeRoom(fields["native"]); err != nil {
		return q, err
	}
	direction, err := buildingUint(fields["expectedDirection"])
	if err != nil || direction == 0 {
		return q, errors.New("expectedDirection must be a positive canonical uint64 string")
	}
	var plan domain.PlanID
	if err = json.Unmarshal(fields["planId"], &plan); err != nil {
		return q, err
	}
	if err = buildingRequestID(string(plan)); err != nil {
		return q, err
	}
	var tick json.Number
	if err = json.Unmarshal(fields["tick"], &tick); err != nil {
		return q, err
	}
	n, err := strconv.ParseInt(tick.String(), 10, 64)
	if err != nil || n < 0 {
		return q, errors.New("tick must be a nonnegative integer")
	}
	q.Tick = domain.Tick(n)
	q.Snapshot = domain.GenerationSnapshot{Colony: world.Colony, Load: world.Load, Map: world.Map, Direction: domain.DirectionID(direction), Plan: plan}
	if q.World() != world {
		return store.RoomAdoptionSubmissionRequest{}, errors.New("room adoption names two worlds")
	}
	return q, nil
}

func decodeAdoptedRoom(raw json.RawMessage) (domain.RoomAdoption, error) {
	var zero domain.RoomAdoption
	var room AdoptedRoom
	fields, err := buildingOptionalFields(raw, []string{"x", "z", "width", "height", "entrance"}, []string{"interiorCells", "entranceCell"})
	if err != nil {
		return zero, err
	}
	for key, target := range map[string]any{"x": &room.X, "z": &room.Z, "width": &room.Width, "height": &room.Height, "entrance": &room.Entrance} {
		if err = json.Unmarshal(fields[key], target); err != nil {
			return zero, err
		}
	}
	var interior []domain.Cell
	if raw, ok := fields["interiorCells"]; ok {
		if interior, err = decodeCells(raw); err != nil {
			return zero, err
		}
	}
	var door domain.Cell
	doorSet := false
	if raw, ok := fields["entranceCell"]; ok {
		var cell ZoneCell
		if err = json.Unmarshal(raw, &cell); err != nil {
			return zero, err
		}
		door, doorSet = domain.Cell{X: cell.X, Z: cell.Z}, true
	}
	return domain.NewRoomAdoption(domain.RoomBounds{X: room.X, Z: room.Z, Width: room.Width, Height: room.Height}, room.Entrance, interior, door, doorSet)
}

func decodeAdoptedNativeRoom(raw json.RawMessage) (domain.AdoptionEvidence, error) {
	var e domain.AdoptionEvidence
	fields, err := buildingFields(raw, "roomId", "cells", "role", "roleLabel")
	if err != nil {
		return e, err
	}
	for key, target := range map[string]any{"roomId": &e.RoomID, "cells": &e.Cells, "role": &e.Role, "roleLabel": &e.RoleLabel} {
		if err = json.Unmarshal(fields[key], target); err != nil {
			return e, err
		}
	}
	return e, e.Validate()
}

func adoptedRoomDTO(r domain.RoomAdoption) AdoptedRoom {
	b := r.Bounds()
	out := AdoptedRoom{X: b.X, Z: b.Z, Width: b.Width, Height: b.Height, Entrance: r.Entrance()}
	if r.Rectangular() {
		return out
	}
	for _, cell := range r.InteriorCells() {
		out.InteriorCell = append(out.InteriorCell, ZoneCell{X: cell.X, Z: cell.Z})
	}
	door := r.EntranceCell()
	out.EntranceCell = &ZoneCell{X: door.X, Z: door.Z}
	return out
}

func projectRoomAdoption(v store.RoomAdoptionSubmission) (roomAdoptionDTO, error) {
	var zero roomAdoptionDTO
	if buildingRequestID(v.Request.RequestID) != nil || domain.ValidateRoomIntent(v.Request.IntentID) != nil || v.Request.World().Validate() != nil ||
		v.State.Goal.Validate() != nil || v.State.Goal.ID != v.Goal || v.State.Goal.Source != domain.PlayerGoal {
		return zero, errors.New("invalid room adoption submission")
	}
	e := v.Request.Evidence
	return roomAdoptionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World()), v.Request.IntentID,
		adoptedRoomDTO(v.Request.Adoption), AdoptedNativeRoom{e.RoomID, e.Cells, e.Role, e.RoleLabel},
		goalStateWire(v.State), adoptionOrders}, nil
}

func (s *Server) handleAdoptRoom(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerAdoptRoom)
	if !ok {
		s.failure(w, r, 404, "not_found", "Room adoption is not enabled")
		return
	}
	switch path {
	case "/api/player/adopt-room/claim":
		q, err := decodeRoomAdoption(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world, expected direction, plan ID, intent ID, inspected room, observed native room and a tick")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitRoomAdoption(ctx, q)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			status, failure := playerFailure(err)
			s.write(w, r, status, failure)
			return
		}
		if !v.Request.Same(q) {
			s.readFailure(w, r, errors.New("mismatched submission"))
			return
		}
		dto, err := projectRoomAdoption(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/adopt-room/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupRoomAdoptionSubmission(ctx, ids[0])
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		if v.Request.RequestID != ids[0] {
			s.readFailure(w, r, errors.New("mismatched submission"))
			return
		}
		dto, err := projectRoomAdoption(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, dto)
	default:
		world, err := policyWorld(query)
		if err != nil {
			s.failure(w, r, 400, "invalid_query", "One colonyId, loadToken and mapId are required")
			return
		}
		v, err := player.AdoptedShelter(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		dto, err := projectRoomAdoption(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, dto)
	}
}

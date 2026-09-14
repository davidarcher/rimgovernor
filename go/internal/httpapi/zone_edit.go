package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type zoneEditSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Edit      ZoneEdit            `json:"edit"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeZoneEditFields(raw json.RawMessage) (domain.ZoneEdit, error) {
	var zero domain.ZoneEdit
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		return zero, err
	}
	if fields["zoneId"] == nil || fields["before"] == nil || fields["op"] == nil {
		return zero, errors.New("required field missing or null")
	}
	var zoneID, before string
	var op domain.ZoneEditOp
	if err := json.Unmarshal(fields["zoneId"], &zoneID); err != nil {
		return zero, err
	}
	if err := json.Unmarshal(fields["before"], &before); err != nil {
		return zero, err
	}
	if err := json.Unmarshal(fields["op"], &op); err != nil {
		return zero, err
	}
	switch op {
	case domain.ZoneEditAdd, domain.ZoneEditRemove:
		if len(fields) != 4 || fields["cells"] == nil {
			return zero, errors.New("unexpected or missing fields")
		}
		var cellDTOs []ZoneCell
		if err := json.Unmarshal(fields["cells"], &cellDTOs); err != nil {
			return zero, err
		}
		cells := make([]domain.Cell, 0, len(cellDTOs))
		for _, c := range cellDTOs {
			cells = append(cells, domain.Cell{X: c.X, Z: c.Z})
		}
		if op == domain.ZoneEditAdd {
			return domain.NewZoneEditAdd(zoneID, before, cells)
		}
		return domain.NewZoneEditRemove(zoneID, before, cells)
	case domain.ZoneEditDelete:
		if len(fields) != 3 {
			return zero, errors.New("unexpected or missing fields")
		}
		return domain.NewZoneEditDelete(zoneID, before)
	default:
		return zero, errors.New("unsupported zone edit operation")
	}
}
func zoneEditDTO(z domain.ZoneEdit) ZoneEdit {
	cells := make([]ZoneCell, 0, len(z.Cells()))
	for _, c := range z.Cells() {
		cells = append(cells, ZoneCell{X: c.X, Z: c.Z})
	}
	return ZoneEdit{ZoneID: z.ZoneID(), Before: z.BeforeToken(), Op: z.Op(), Cells: cells}
}
func decodeZoneEditSubmission(reader io.Reader) (store.ZoneEditSubmissionRequest, error) {
	var q store.ZoneEditSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "edit")
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["requestId"], &q.RequestID); err != nil {
		return q, err
	}
	if err = buildingRequestID(q.RequestID); err != nil {
		return q, err
	}
	if q.World, err = buildingWorld(fields["expected"]); err != nil {
		return q, err
	}
	q.Edit, err = decodeZoneEditFields(fields["edit"])
	return q, err
}
func projectZoneEditSubmission(v store.ZoneEditSubmission) (zoneEditSubmissionDTO, error) {
	var zero zoneEditSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid zone edit submission")
	}
	return zoneEditSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), zoneEditDTO(v.Request.Edit), v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitZoneEdit(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeZoneEditSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid zone edit submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitZoneEdit(ctx, q)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	if v.Request != q {
		s.readFailure(w, r, errors.New("mismatched submission"))
		return
	}
	dto, err := projectZoneEditSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	status := 200
	if created {
		status = 201
	}
	s.write(w, r, status, dto)
}
func (s *Server) lookupZoneEdit(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupZoneEditSubmission(ctx, id)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	if v.Request.RequestID != id {
		s.readFailure(w, r, errors.New("mismatched submission"))
		return
	}
	dto, err := projectZoneEditSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}

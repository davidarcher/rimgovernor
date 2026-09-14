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

type zoneCreateSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Zone      ZoneCreate          `json:"zone"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeZoneCreateFields(raw json.RawMessage) (domain.ZoneCreate, error) {
	var zero domain.ZoneCreate
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		return zero, err
	}
	if fields["kind"] == nil || fields["cells"] == nil {
		return zero, errors.New("required field missing or null")
	}
	var kind domain.ZoneKind
	if err := json.Unmarshal(fields["kind"], &kind); err != nil {
		return zero, err
	}
	var cellDTOs []ZoneCell
	if err := json.Unmarshal(fields["cells"], &cellDTOs); err != nil {
		return zero, err
	}
	cells := make([]domain.Cell, 0, len(cellDTOs))
	for _, c := range cellDTOs {
		cells = append(cells, domain.Cell{X: c.X, Z: c.Z})
	}
	switch kind {
	case domain.GrowingZone:
		if len(fields) != 3 || fields["crop"] == nil {
			return zero, errors.New("unexpected or missing fields")
		}
		var crop string
		if err := json.Unmarshal(fields["crop"], &crop); err != nil {
			return zero, err
		}
		return domain.NewZoneCreate(domain.GrowingZone, crop, cells)
	case domain.StockpileZone:
		if fields["preset"] == nil || fields["priority"] == nil {
			return zero, errors.New("required field missing or null")
		}
		var preset domain.StockpilePreset
		var priority domain.StockpilePriority
		if err := json.Unmarshal(fields["preset"], &preset); err != nil {
			return zero, err
		}
		if err := json.Unmarshal(fields["priority"], &priority); err != nil {
			return zero, err
		}
		switch preset {
		case domain.FoodPreset:
			if len(fields) != 4 {
				return zero, errors.New("unexpected or missing fields")
			}
			return domain.NewStockpileZone(preset, priority, cells)
		case domain.NothingPreset:
			if len(fields) != 5 || fields["allow"] == nil {
				return zero, errors.New("unexpected or missing fields")
			}
			var allow []string
			if err := json.Unmarshal(fields["allow"], &allow); err != nil {
				return zero, err
			}
			return domain.NewAllowListStockpileZone(priority, allow, cells)
		default:
			return zero, errors.New("unsupported stockpile preset")
		}
	default:
		return zero, errors.New("unsupported zone kind")
	}
}
func zoneCreateDTO(z domain.ZoneCreate) ZoneCreate {
	cells := make([]ZoneCell, 0, len(z.Cells()))
	for _, c := range z.Cells() {
		cells = append(cells, ZoneCell{X: c.X, Z: c.Z})
	}
	return ZoneCreate{Kind: z.Kind(), Crop: z.Crop(), Preset: z.Preset(), Priority: z.Priority(), Cells: cells, Allow: z.Allow()}
}
func decodeZoneCreateSubmission(reader io.Reader) (store.ZoneCreateSubmissionRequest, error) {
	var q store.ZoneCreateSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "zone")
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
	q.Zone, err = decodeZoneCreateFields(fields["zone"])
	return q, err
}
func projectZoneCreateSubmission(v store.ZoneCreateSubmission) (zoneCreateSubmissionDTO, error) {
	var zero zoneCreateSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid zone create submission")
	}
	return zoneCreateSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), zoneCreateDTO(v.Request.Zone), v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitZoneCreate(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeZoneCreateSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid zone create submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitZoneCreate(ctx, q)
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
	dto, err := projectZoneCreateSubmission(v)
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
func (s *Server) lookupZoneCreate(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupZoneCreateSubmission(ctx, id)
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
	dto, err := projectZoneCreateSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}

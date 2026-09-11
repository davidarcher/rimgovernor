package httpapi

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/url"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type playerWorkPreferences interface {
	WorkPreferences(context.Context, domain.PlanID) (store.WorkPreferences, error)
	SetWorkPreferences(context.Context, store.WorkPreferenceRequest) (store.WorkPreferenceRecord, error)
}
type workOverrideDTO struct {
	Pawn     policy.PawnID   `json:"pawn"`
	Work     policy.WorkType `json:"work"`
	Priority int             `json:"priority"`
}
type workPreferencesDTO struct {
	Plan      domain.PlanID     `json:"planId"`
	Expected  Identity          `json:"expected"`
	Revision  uint64            `json:"revision,string"`
	Overrides []workOverrideDTO `json:"overrides"`
}

func decodeWorkPreferences(reader io.Reader) (store.WorkPreferenceRequest, error) {
	var q store.WorkPreferenceRequest
	fields, err := buildingRequest(reader, "requestId", "planId", "expected", "expectedRevision", "overrides")
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["requestId"], &q.RequestID); err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["planId"], &q.Plan); err != nil {
		return q, err
	}
	if q.World, err = buildingWorld(fields["expected"]); err != nil {
		return q, err
	}
	if q.ExpectedRevision, err = buildingUint(fields["expectedRevision"]); err != nil {
		return q, err
	}
	var rows []json.RawMessage
	if err = json.Unmarshal(fields["overrides"], &rows); err != nil {
		return q, err
	}
	q.Overrides = []policy.WorkOverride{}
	for _, raw := range rows {
		f, err := buildingFields(raw, "pawn", "work", "priority")
		if err != nil {
			return q, err
		}
		var value policy.WorkOverride
		if err = json.Unmarshal(f["pawn"], &value.Pawn); err != nil {
			return q, err
		}
		if err = json.Unmarshal(f["work"], &value.Work); err != nil {
			return q, err
		}
		if err = json.Unmarshal(f["priority"], &value.Priority); err != nil {
			return q, err
		}
		q.Overrides = append(q.Overrides, value)
	}
	return q, q.Validate()
}
func (s *Server) handleWorkPreferences(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, write bool) {
	player, ok := s.player.(playerWorkPreferences)
	if !ok {
		s.failure(w, r, 404, "not_found", "Work preferences are not enabled")
		return
	}
	var preferences store.WorkPreferences
	var err error
	if write {
		q, e := decodeWorkPreferences(r.Body)
		if e != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a plan, current preference revision and complete work overrides")
			return
		}
		var record store.WorkPreferenceRecord
		record, err = player.SetWorkPreferences(ctx, q)
		preferences = record.Preferences
	} else {
		ids := query["planId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One planId is required")
			return
		}
		preferences, err = player.WorkPreferences(ctx, domain.PlanID(ids[0]))
	}
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	out := workPreferencesDTO{Plan: preferences.Plan, Expected: playerWorldDTO(preferences.World), Revision: preferences.Revision, Overrides: []workOverrideDTO{}}
	for _, v := range preferences.Overrides {
		out.Overrides = append(out.Overrides, workOverrideDTO{v.Pawn, v.Work, v.Priority})
	}
	s.write(w, r, 200, out)
}

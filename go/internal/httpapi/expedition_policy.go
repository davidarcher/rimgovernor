package httpapi

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// playerExpeditionPolicy is an optional player capability, asserted the same
// way playerPopulationPolicy is, so enabling the expedition policy routes
// never widens the required PlayerBuildings interface.
type playerExpeditionPolicy interface {
	SubmitExpeditionPolicy(context.Context, store.ExpeditionPolicySubmissionRequest) (store.ExpeditionPolicySubmission, bool, error)
	ExpeditionPolicy(context.Context, store.World) (domain.ExpeditionPolicy, error)
	LookupExpeditionPolicySubmission(context.Context, string) (store.ExpeditionPolicySubmission, error)
}

type expeditionPolicyDTO struct {
	Expected Identity         `json:"expected"`
	Policy   ExpeditionPolicy `json:"policy"`
}
type expeditionPolicySubmissionDTO struct {
	RequestID string   `json:"requestId"`
	Expected  Identity `json:"expected"`
	// Policy is the partial request as the player sent it, so a reader can
	// see what was asked for and not only what it produced.
	Policy ExpeditionPolicyPatch `json:"policy"`
	// Applied is the merged whole this request produced when it was accepted.
	Applied ExpeditionPolicy `json:"applied"`
	// Current is the world's limits as they stand now, which is Applied for a
	// freshly accepted request and a newer value when an older request ID is
	// replayed.
	Current ExpeditionPolicy `json:"current"`
}

func expeditionPolicyWire(p domain.ExpeditionPolicy) ExpeditionPolicy {
	f := p.Fields()
	return ExpeditionPolicy{f.MinimumHomeColonists, f.MinimumHomeFoodDays, f.TravelFoodMarginDays,
		f.MaximumTravelDays, f.MaximumCaravans, f.MinimumGoodwill, f.MinimumDestinationTemperature,
		f.MaximumDestinationTemperature, f.KeepHomeDoctor, f.RequireReturnStorage}
}

func expeditionPatchWire[T comparable](o domain.Optional[T]) *T {
	if value, ok := o.Get(); ok {
		return &value
	}
	return nil
}

func expeditionPolicyPatchWire(q domain.ExpeditionPolicyPatch) ExpeditionPolicyPatch {
	return ExpeditionPolicyPatch{
		MinimumHomeColonists:          expeditionPatchWire(q.MinimumHomeColonists),
		MinimumHomeFoodDays:           expeditionPatchWire(q.MinimumHomeFoodDays),
		TravelFoodMarginDays:          expeditionPatchWire(q.TravelFoodMarginDays),
		MaximumTravelDays:             expeditionPatchWire(q.MaximumTravelDays),
		MaximumCaravans:               expeditionPatchWire(q.MaximumCaravans),
		MinimumGoodwill:               expeditionPatchWire(q.MinimumGoodwill),
		MinimumDestinationTemperature: expeditionPatchWire(q.MinimumDestinationTemperature),
		MaximumDestinationTemperature: expeditionPatchWire(q.MaximumDestinationTemperature),
		KeepHomeDoctor:                expeditionPatchWire(q.KeepHomeDoctor),
		RequireReturnStorage:          expeditionPatchWire(q.RequireReturnStorage),
	}
}

// expeditionPolicyField decodes one optionally supplied limit. Unlike
// buildingFields' all-or-nothing shape check, absence here is meaningful:
// a missing key keeps the established value, while an explicit null is a
// malformed request rather than a way to clear a limit.
func expeditionPolicyField[T comparable](fields map[string]json.RawMessage, key string, target *domain.Optional[T]) error {
	raw, ok := fields[key]
	if !ok {
		return nil
	}
	if bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
		return errors.New("null field")
	}
	var value T
	if err := json.Unmarshal(raw, &value); err != nil {
		return err
	}
	*target = domain.Some(value)
	return nil
}

func decodeExpeditionPolicyPatch(raw json.RawMessage) (domain.ExpeditionPolicyPatch, error) {
	var patch domain.ExpeditionPolicyPatch
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil || fields == nil {
		return patch, errors.New("expected a policy object")
	}
	read := []func() error{
		func() error {
			return expeditionPolicyField(fields, "minimumHomeColonists", &patch.MinimumHomeColonists)
		},
		func() error { return expeditionPolicyField(fields, "minimumHomeFoodDays", &patch.MinimumHomeFoodDays) },
		func() error {
			return expeditionPolicyField(fields, "travelFoodMarginDays", &patch.TravelFoodMarginDays)
		},
		func() error { return expeditionPolicyField(fields, "maximumTravelDays", &patch.MaximumTravelDays) },
		func() error { return expeditionPolicyField(fields, "maximumCaravans", &patch.MaximumCaravans) },
		func() error { return expeditionPolicyField(fields, "minimumGoodwill", &patch.MinimumGoodwill) },
		func() error {
			return expeditionPolicyField(fields, "minimumDestinationTemperature", &patch.MinimumDestinationTemperature)
		},
		func() error {
			return expeditionPolicyField(fields, "maximumDestinationTemperature", &patch.MaximumDestinationTemperature)
		},
		func() error { return expeditionPolicyField(fields, "keepHomeDoctor", &patch.KeepHomeDoctor) },
		func() error {
			return expeditionPolicyField(fields, "requireReturnStorage", &patch.RequireReturnStorage)
		},
	}
	// Every supplied key must be one this contract knows, so a misspelled
	// limit is refused rather than silently ignored.
	if len(fields) != len(read) {
		for key := range fields {
			if !expeditionPolicyKeys[key] {
				return patch, errors.New("unexpected policy field")
			}
		}
	}
	for _, decode := range read {
		if err := decode(); err != nil {
			return patch, err
		}
	}
	return patch, patch.Validate()
}

var expeditionPolicyKeys = map[string]bool{
	"minimumHomeColonists": true, "minimumHomeFoodDays": true, "travelFoodMarginDays": true,
	"maximumTravelDays": true, "maximumCaravans": true, "minimumGoodwill": true,
	"minimumDestinationTemperature": true, "maximumDestinationTemperature": true,
	"keepHomeDoctor": true, "requireReturnStorage": true,
}

func decodeExpeditionPolicySubmission(reader io.Reader) (store.ExpeditionPolicySubmissionRequest, error) {
	var q store.ExpeditionPolicySubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "policy")
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
	q.Patch, err = decodeExpeditionPolicyPatch(fields["policy"])
	return q, err
}

func projectExpeditionPolicySubmission(v store.ExpeditionPolicySubmission) (expeditionPolicySubmissionDTO, error) {
	var zero expeditionPolicySubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil ||
		v.Request.Patch.Validate() != nil || !v.Applied.Set() || !v.Current.Set() {
		return zero, errors.New("invalid expedition policy submission")
	}
	return expeditionPolicySubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World),
		expeditionPolicyPatchWire(v.Request.Patch), expeditionPolicyWire(v.Applied), expeditionPolicyWire(v.Current)}, nil
}

func (s *Server) handleExpeditionPolicy(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerExpeditionPolicy)
	if !ok {
		s.failure(w, r, 404, "not_found", "Expedition policy is not enabled")
		return
	}
	switch path {
	// The route is /update rather than population policy's /replace because
	// the request is a partial patch: limits it does not name are preserved,
	// never reset.
	case "/api/player/expedition-policy/update":
		q, err := decodeExpeditionPolicySubmission(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world and at least one bounded expedition limit")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitExpeditionPolicy(ctx, q)
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
		dto, err := projectExpeditionPolicySubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/expedition-policy/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupExpeditionPolicySubmission(ctx, ids[0])
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
		dto, err := projectExpeditionPolicySubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		s.write(w, r, 200, dto)
	default:
		// A world the player has never set limits for still has limits: the
		// contract defaults, which is what a first patch merges onto.
		world, err := policyWorld(query)
		if err != nil {
			s.failure(w, r, 400, "invalid_query", "One colonyId, loadToken and mapId are required")
			return
		}
		policy, err := player.ExpeditionPolicy(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		if !policy.Set() {
			s.readFailure(w, r, errors.New("invalid expedition policy"))
			return
		}
		s.write(w, r, 200, expeditionPolicyDTO{playerWorldDTO(world), expeditionPolicyWire(policy)})
	}
}

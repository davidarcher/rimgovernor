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

// playerPopulationPolicy is an optional player capability, asserted the same
// way playerWorkPreferences is, so enabling the population policy routes
// never widens the required PlayerBuildings interface.
type playerPopulationPolicy interface {
	SubmitPopulationPolicy(context.Context, store.PopulationPolicySubmissionRequest) (store.PopulationPolicySubmission, bool, error)
	PopulationPolicy(context.Context, store.World) (domain.PopulationPolicy, error)
	LookupPopulationPolicySubmission(context.Context, string) (store.PopulationPolicySubmission, error)
}

type populationPolicyDTO struct {
	Expected Identity         `json:"expected"`
	Policy   PopulationPolicy `json:"policy"`
}
type populationPolicySubmissionDTO struct {
	RequestID string           `json:"requestId"`
	Expected  Identity         `json:"expected"`
	Policy    PopulationPolicy `json:"policy"`
	// Current is the world's population policy as it stands now, which is
	// Policy for a freshly accepted request and may be a newer value when an
	// older request ID is replayed.
	Current PopulationPolicy `json:"current"`
}

func populationPolicyWire(p domain.PopulationPolicy) PopulationPolicy {
	return PopulationPolicy{Maximum: p.Maximum(), FoodDays: p.FoodDays()}
}
func decodePopulationPolicyFields(raw json.RawMessage) (domain.PopulationPolicy, error) {
	fields, err := buildingFields(raw, "maximum", "foodDays")
	if err != nil {
		return domain.PopulationPolicy{}, err
	}
	var maximum int32
	var foodDays float64
	if err = json.Unmarshal(fields["maximum"], &maximum); err != nil {
		return domain.PopulationPolicy{}, err
	}
	if err = json.Unmarshal(fields["foodDays"], &foodDays); err != nil {
		return domain.PopulationPolicy{}, err
	}
	return domain.NewPopulationPolicy(maximum, foodDays)
}
func decodePopulationPolicySubmission(reader io.Reader) (store.PopulationPolicySubmissionRequest, error) {
	var q store.PopulationPolicySubmissionRequest
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
	q.Policy, err = decodePopulationPolicyFields(fields["policy"])
	return q, err
}
func projectPopulationPolicySubmission(v store.PopulationPolicySubmission) (populationPolicySubmissionDTO, error) {
	var zero populationPolicySubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || !v.Request.Policy.Set() || !v.Current.Set() {
		return zero, errors.New("invalid population policy submission")
	}
	return populationPolicySubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), populationPolicyWire(v.Request.Policy), populationPolicyWire(v.Current)}, nil
}

// policyWorld reads a colonyId/loadToken/mapId query triple, shared by the
// population and expedition policy reads. A current policy is scoped per
// world rather than per plan, so a read cannot borrow work-preferences'
// single planId query shape.
func policyWorld(query url.Values) (store.World, error) {
	var world store.World
	colony, load, mapID := query["colonyId"], query["loadToken"], query["mapId"]
	if len(query) != 3 || len(colony) != 1 || len(load) != 1 || len(mapID) != 1 {
		return world, errors.New("colonyId, loadToken and mapId are required")
	}
	n, err := strconv.ParseInt(mapID[0], 10, 32)
	if err != nil || strconv.FormatInt(n, 10) != mapID[0] {
		return world, errors.New("expected canonical mapId")
	}
	world = store.World{Colony: domain.ColonyID(colony[0]), Load: domain.LoadID(load[0]), Map: domain.MapID(n)}
	return world, world.Validate()
}

func (s *Server) handlePopulationPolicy(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerPopulationPolicy)
	if !ok {
		s.failure(w, r, 404, "not_found", "Population policy is not enabled")
		return
	}
	switch path {
	case "/api/player/population-policy/replace":
		q, err := decodePopulationPolicySubmission(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world and a bounded population policy")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitPopulationPolicy(ctx, q)
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
		dto, err := projectPopulationPolicySubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/population-policy/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupPopulationPolicySubmission(ctx, ids[0])
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
		dto, err := projectPopulationPolicySubmission(v)
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
		policy, err := player.PopulationPolicy(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		if !policy.Set() {
			s.readFailure(w, r, errors.New("invalid population policy"))
			return
		}
		s.write(w, r, 200, populationPolicyDTO{playerWorldDTO(world), populationPolicyWire(policy)})
	}
}

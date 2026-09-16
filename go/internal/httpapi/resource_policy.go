package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// playerResourcePolicy is an optional player capability, asserted the same way
// playerPopulationDecision is, so enabling the per-resource production policy
// routes never widens the required PlayerBuildings interface.
type playerResourcePolicy interface {
	SubmitResourcePolicy(context.Context, store.ResourcePolicySubmissionRequest) (store.ResourcePolicySubmission, bool, error)
	ResourcePolicies(context.Context, store.World) ([]domain.ResourceDirective, error)
	LookupResourcePolicySubmission(context.Context, string) (store.ResourcePolicySubmission, error)
}

type resourcePoliciesDTO struct {
	Expected Identity `json:"expected"`
	// Policies is every resource the player has named in this world, ordered by
	// resource. It is empty, never absent, for a world with none.
	Policies []ResourcePolicy `json:"policies"`
}
type resourcePolicySubmissionDTO struct {
	RequestID string   `json:"requestId"`
	Expected  Identity `json:"expected"`
	// Applied is the named resource's merged directive this request produced
	// when it was accepted; Current is that resource's directive now, which is
	// Applied for a freshly accepted request and may be newer when an older
	// request ID is replayed.
	Applied  ResourcePolicy      `json:"applied"`
	Current  ResourcePolicy      `json:"current"`
	PlanID   domain.PlanID       `json:"planId"`
	ActionID domain.ActionID     `json:"actionId"`
	Revision domain.PlanRevision `json:"revision,string"`
}

func resourcePolicyWire(d domain.ResourceDirective) ResourcePolicy {
	return ResourcePolicy{Resource: d.Resource(), Reserve: d.Reserve(), Spending: string(d.Spending())}
}

// decodeResourcePolicyFields reads the one half of a resource's policy the
// request changes. Exactly one of spending or reserve is accepted, matching the
// two separate commands this one route serves: a body naming both is a
// third command neither contract defines.
func decodeResourcePolicyFields(raw json.RawMessage) (domain.ResourcePolicyPatch, error) {
	var patch domain.ResourcePolicyPatch
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		return patch, err
	}
	if len(fields) != 2 || fields["resource"] == nil {
		return patch, errors.New("unexpected or missing fields")
	}
	if err := json.Unmarshal(fields["resource"], &patch.Resource); err != nil {
		return patch, err
	}
	switch {
	case fields["spending"] != nil:
		var spending string
		if err := json.Unmarshal(fields["spending"], &spending); err != nil {
			return patch, err
		}
		patch.Spending = domain.Some(domain.ResourceSpending(spending))
	case fields["reserve"] != nil:
		var reserve int64
		if err := json.Unmarshal(fields["reserve"], &reserve); err != nil {
			return patch, err
		}
		patch.Reserve = domain.Some(reserve)
	default:
		return patch, errors.New("unexpected or missing fields")
	}
	return patch, patch.Validate()
}
func decodeResourcePolicySubmission(reader io.Reader) (store.ResourcePolicySubmissionRequest, error) {
	var q store.ResourcePolicySubmissionRequest
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
	q.Patch, err = decodeResourcePolicyFields(fields["policy"])
	return q, err
}
func projectResourcePolicySubmission(v store.ResourcePolicySubmission) (resourcePolicySubmissionDTO, error) {
	var zero resourcePolicySubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || v.Request.Patch.Validate() != nil ||
		!v.Applied.Set() || !v.Current.Set() || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid resource policy submission")
	}
	return resourcePolicySubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World),
		resourcePolicyWire(v.Applied), resourcePolicyWire(v.Current), v.Plan, v.Action, v.Revision}, nil
}

func (s *Server) handleResourcePolicy(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerResourcePolicy)
	if !ok {
		s.failure(w, r, 404, "not_found", "Resource policies are not enabled")
		return
	}
	switch path {
	case "/api/player/resource-policy/update":
		q, err := decodeResourcePolicySubmission(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world, an observed resource and exactly one of spending or reserve")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitResourcePolicy(ctx, q)
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
		dto, err := projectResourcePolicySubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/resource-policy/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupResourcePolicySubmission(ctx, ids[0])
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
		dto, err := projectResourcePolicySubmission(v)
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
		directives, err := player.ResourcePolicies(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		policies := make([]ResourcePolicy, 0, len(directives))
		for _, directive := range directives {
			if !directive.Set() {
				s.readFailure(w, r, errors.New("invalid resource policy"))
				return
			}
			policies = append(policies, resourcePolicyWire(directive))
		}
		s.write(w, r, 200, resourcePoliciesDTO{playerWorldDTO(world), policies})
	}
}

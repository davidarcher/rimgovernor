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

// playerPopulationDecision is an optional player capability, asserted the
// same way playerPopulationPolicy is, so enabling the per-pawn population
// decision routes never widens the required PlayerBuildings interface.
type playerPopulationDecision interface {
	SubmitPopulationDecision(context.Context, store.PopulationDecisionSubmissionRequest) (store.PopulationDecisionSubmission, bool, error)
	PopulationDecisions(context.Context, store.World) ([]domain.PopulationDirective, error)
	LookupPopulationDecisionSubmission(context.Context, string) (store.PopulationDecisionSubmission, error)
}

type populationDecisionsDTO struct {
	Expected Identity `json:"expected"`
	// Decisions is every pawn the player has named in this world, ordered by
	// pawn ID. It is empty, never absent, for a world with none.
	Decisions []PopulationDecision `json:"decisions"`
}
type populationDecisionSubmissionDTO struct {
	RequestID string             `json:"requestId"`
	Expected  Identity           `json:"expected"`
	Decision  PopulationDecision `json:"decision"`
	// Current is that pawn's direction as it stands now, which is Decision for
	// a freshly accepted request and may be a newer value when an older
	// request ID is replayed.
	Current PopulationDecision `json:"current"`
}

func populationDecisionWire(d domain.PopulationDirective) PopulationDecision {
	return PopulationDecision{Pawn: string(d.Pawn()), Decision: string(d.Decision())}
}
func decodePopulationDecisionFields(raw json.RawMessage) (domain.PopulationDirective, error) {
	fields, err := buildingFields(raw, "pawn", "decision")
	if err != nil {
		return domain.PopulationDirective{}, err
	}
	var pawn, decision string
	if err = json.Unmarshal(fields["pawn"], &pawn); err != nil {
		return domain.PopulationDirective{}, err
	}
	if err = json.Unmarshal(fields["decision"], &decision); err != nil {
		return domain.PopulationDirective{}, err
	}
	return domain.NewPopulationDirective(domain.PawnID(pawn), domain.PopulationDecision(decision))
}
func decodePopulationDecisionSubmission(reader io.Reader) (store.PopulationDecisionSubmissionRequest, error) {
	var q store.PopulationDecisionSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "decision")
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
	q.Directive, err = decodePopulationDecisionFields(fields["decision"])
	return q, err
}
func projectPopulationDecisionSubmission(v store.PopulationDecisionSubmission) (populationDecisionSubmissionDTO, error) {
	var zero populationDecisionSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || !v.Request.Directive.Set() || !v.Current.Set() {
		return zero, errors.New("invalid population decision submission")
	}
	return populationDecisionSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), populationDecisionWire(v.Request.Directive), populationDecisionWire(v.Current)}, nil
}

func (s *Server) handlePopulationDecision(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerPopulationDecision)
	if !ok {
		s.failure(w, r, 404, "not_found", "Population decisions are not enabled")
		return
	}
	switch path {
	case "/api/player/population-decision/replace":
		q, err := decodePopulationDecisionSubmission(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world, an observed pawn and one of rescue, capture, recruit or ignore")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitPopulationDecision(ctx, q)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			// A missing population policy reports ErrNotFound, the same
			// precondition Python states as "Set an explicit population
			// maximum and food reserve first".
			status, failure := playerFailure(err)
			s.write(w, r, status, failure)
			return
		}
		if v.Request != q {
			s.readFailure(w, r, errors.New("mismatched submission"))
			return
		}
		dto, err := projectPopulationDecisionSubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/population-decision/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupPopulationDecisionSubmission(ctx, ids[0])
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
		dto, err := projectPopulationDecisionSubmission(v)
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
		directives, err := player.PopulationDecisions(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		decisions := make([]PopulationDecision, 0, len(directives))
		for _, directive := range directives {
			if !directive.Set() {
				s.readFailure(w, r, errors.New("invalid population decision"))
				return
			}
			decisions = append(decisions, populationDecisionWire(directive))
		}
		s.write(w, r, 200, populationDecisionsDTO{playerWorldDTO(world), decisions})
	}
}

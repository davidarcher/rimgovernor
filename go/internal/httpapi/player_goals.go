package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"sort"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// playerGoals is an optional player capability, asserted the same way
// playerResourcePolicy is, so enabling the maintained-goal routes never widens
// the required PlayerBuildings interface.
type playerGoals interface {
	SubmitGoalCreate(context.Context, store.GoalCreateSubmissionRequest) (store.GoalCreateSubmission, bool, error)
	CancelGoal(context.Context, store.World, domain.GoalID, uint64) (store.GoalState, error)
	PlayerGoals(context.Context, store.World) (map[domain.GoalKind]domain.GoalID, error)
	LookupGoalCreateSubmission(context.Context, string) (store.GoalCreateSubmission, error)
}

// PlayerGoal is one kind the player has activated in a world and the goal
// identity that activation produced, the identity a cancellation names.
type PlayerGoal struct {
	Kind   string        `json:"kind"`
	GoalID domain.GoalID `json:"goalId"`
}

type playerGoalsDTO struct {
	Expected Identity `json:"expected"`
	// Goals is every kind the player has activated in this world, ordered by
	// kind. It is empty, never absent, for a world with none.
	Goals []PlayerGoal `json:"goals"`
}

// goalStateDTO is one goal's lifecycle state. Revision is the local CAS token a
// cancellation must present; it is not native authority or a native generation.
type goalStateDTO struct {
	GoalID   domain.GoalID `json:"goalId"`
	Source   string        `json:"source"`
	Status   string        `json:"status"`
	Need     string        `json:"need"`
	Priority int           `json:"priority"`
	Epoch    uint64        `json:"epoch,string"`
	Revision uint64        `json:"revision,string"`
	Tick     domain.Tick   `json:"tick"`
}

type goalCreateSubmissionDTO struct {
	RequestID string   `json:"requestId"`
	Expected  Identity `json:"expected"`
	Kind      string   `json:"kind"`
	// State is the activated goal's state now, which is what the request
	// produced for a freshly accepted request and may be newer when an older
	// request ID is replayed.
	State goalStateDTO `json:"state"`
}

func goalStateWire(v store.GoalState) goalStateDTO {
	return goalStateDTO{v.Goal.ID, string(v.Goal.Source), string(v.Goal.Status), string(v.Goal.Need), v.Goal.Priority, v.Goal.Epoch, v.Revision, v.Goal.Tick}
}

// decodeGoalCreate reads one goal activation. The snapshot is supplied whole
// rather than assembled here, exactly as the routine reviewer supplies its own
// Current/Tick: this server observes no native state and must not invent a
// generation the goal is then judged against.
func decodeGoalCreate(reader io.Reader) (store.GoalCreateSubmissionRequest, error) {
	var q store.GoalCreateSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "planId", "goal", "tick")
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
	var kind string
	if err = json.Unmarshal(fields["goal"], &kind); err != nil {
		return q, err
	}
	if q.Kind, err = domain.NewGoalKind(kind); err != nil {
		return q, err
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
	q.Snapshot = domain.GenerationSnapshot{Colony: world.Colony, Load: world.Load, Map: world.Map, Plan: plan}
	if q.World() != world {
		return store.GoalCreateSubmissionRequest{}, errors.New("goal activation names two worlds")
	}
	return q, nil
}

// goalCancelRequest is one cancellation: the world it is bound to, the exact
// recorded goal identity, and that goal's local CAS revision.
type goalCancelRequest struct {
	World    store.World
	Goal     domain.GoalID
	Revision uint64
}

func decodeGoalCancel(reader io.Reader) (goalCancelRequest, error) {
	var q goalCancelRequest
	fields, err := buildingRequest(reader, "expected", "goalId", "revision")
	if err != nil {
		return q, err
	}
	if q.World, err = buildingWorld(fields["expected"]); err != nil {
		return q, err
	}
	var id string
	if err = json.Unmarshal(fields["goalId"], &id); err != nil {
		return q, err
	}
	if err = buildingRequestID(id); err != nil {
		return q, err
	}
	q.Goal = domain.GoalID(id)
	if q.Revision, err = buildingUint(fields["revision"]); err != nil {
		return q, err
	}
	return q, nil
}

func projectGoalCreateSubmission(v store.GoalCreateSubmission) (goalCreateSubmissionDTO, error) {
	var zero goalCreateSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.Kind.Validate() != nil || v.Request.World().Validate() != nil ||
		v.State.Goal.Validate() != nil || v.State.Goal.ID != v.Goal || v.State.Goal.Source != domain.PlayerGoal {
		return zero, errors.New("invalid goal activation submission")
	}
	return goalCreateSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World()), string(v.Request.Kind), goalStateWire(v.State)}, nil
}

func (s *Server) handlePlayerGoals(ctx context.Context, w http.ResponseWriter, r *http.Request, query url.Values, path string) {
	player, ok := s.player.(playerGoals)
	if !ok {
		s.failure(w, r, 404, "not_found", "Maintained goals are not enabled")
		return
	}
	switch path {
	case "/api/player/goals/activate":
		q, err := decodeGoalCreate(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide a request ID, expected world, expected direction, plan ID, a supported goal kind and a tick")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, created, err := player.SubmitGoalCreate(ctx, q)
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
		dto, err := projectGoalCreateSubmission(v)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		status := 200
		if created {
			status = 201
		}
		s.write(w, r, status, dto)
	case "/api/player/goals/cancel":
		q, err := decodeGoalCancel(r.Body)
		if err != nil {
			s.failure(w, r, 400, "invalid_request", "Provide an expected world, an exact recorded goal ID and its revision")
			return
		}
		if err = ctx.Err(); err != nil {
			s.readFailure(w, r, err)
			return
		}
		v, err := player.CancelGoal(ctx, q.World, q.Goal, q.Revision)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			status, failure := playerFailure(err)
			s.write(w, r, status, failure)
			return
		}
		if v.Goal.ID != q.Goal || v.Goal.Status != domain.GoalCancelled {
			s.readFailure(w, r, errors.New("mismatched cancellation"))
			return
		}
		s.write(w, r, 200, goalStateWire(v))
	case "/api/player/goals/submission":
		ids := query["requestId"]
		if len(query) != 1 || len(ids) != 1 || buildingRequestID(ids[0]) != nil {
			s.failure(w, r, 400, "invalid_query", "One requestId is required")
			return
		}
		v, err := player.LookupGoalCreateSubmission(ctx, ids[0])
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
		dto, err := projectGoalCreateSubmission(v)
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
		bindings, err := player.PlayerGoals(ctx, world)
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		goals := make([]PlayerGoal, 0, len(bindings))
		for kind, id := range bindings {
			if kind.Validate() != nil || buildingRequestID(string(id)) != nil {
				s.readFailure(w, r, errors.New("invalid player goal binding"))
				return
			}
			goals = append(goals, PlayerGoal{string(kind), id})
		}
		sort.Slice(goals, func(i, j int) bool { return goals[i].Kind < goals[j].Kind })
		s.write(w, r, 200, playerGoalsDTO{playerWorldDTO(world), goals})
	}
}

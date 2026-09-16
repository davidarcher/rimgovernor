package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// chatGuidanceDTO is the one nudge a chat reply applied, projected through
// the same wire shapes the matching policy route returns. Kind selects which
// field is populated; an explain-only reply carries no guidance at all.
type chatGuidanceDTO struct {
	Kind string `json:"kind"`
	// Goal is the activated or cancelled goal's state now.
	Goal               *goalStateDTO       `json:"goal,omitempty"`
	PopulationPolicy   *PopulationPolicy   `json:"populationPolicy,omitempty"`
	ExpeditionPolicy   *ExpeditionPolicy   `json:"expeditionPolicy,omitempty"`
	PopulationDecision *PopulationDecision `json:"populationDecision,omitempty"`
	ResourcePolicy     *ResourcePolicy     `json:"resourcePolicy,omitempty"`
}

type chatResponseDTO struct {
	RequestID   string           `json:"requestId"`
	Expected    Identity         `json:"expected"`
	Explanation string           `json:"explanation"`
	Guidance    *chatGuidanceDTO `json:"guidance"`
}

func decodeChatRequest(reader io.Reader) (requestID string, world store.World, message string, err error) {
	fields, err := buildingRequest(reader, "requestId", "expected", "message")
	if err != nil {
		return "", world, "", err
	}
	if err = json.Unmarshal(fields["requestId"], &requestID); err != nil {
		return "", world, "", err
	}
	if err = buildingRequestID(requestID); err != nil {
		return "", world, "", err
	}
	if world, err = buildingWorld(fields["expected"]); err != nil {
		return "", world, "", err
	}
	if err = json.Unmarshal(fields["message"], &message); err != nil {
		return "", world, "", err
	}
	return requestID, world, message, nil
}

// chatFailureStatus maps an *interpreter.Failure to an HTTP status. Every
// other error (facts gathering, context cancellation) goes through
// s.readFailure instead, matching how every other handler in this package
// separates request-shaped failures from read-path failures.
func chatFailureStatus(kind interpreter.FailureKind) int {
	switch kind {
	case interpreter.StaleFacts:
		return 409
	case interpreter.ModelFailure:
		return 502
	case interpreter.InvalidGuidance, interpreter.UnknownFacts:
		return 422
	default:
		return 400
	}
}

// submitChat answers one player message. The model reads bounded colony and
// policy facts, replies with an explanation and at most one nudge, and the
// nudge is applied through exactly the store submission the matching policy
// route uses, under the chat request ID. Chat therefore cannot cause a
// native write except through a policy input the routine reviewer reads.
func (s *Server) submitChat(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	requestID, world, message, err := decodeChatRequest(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid chat request")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	facts, current, err := buildingruntime.GatherChatFacts(ctx, s.chatNative, s.chatJournal, world)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	guidance, err := s.chat.Interpret(ctx, interpreter.Input{UserRequest: message, Current: current, Facts: facts})
	if err != nil {
		var failure *interpreter.Failure
		if errors.As(err, &failure) {
			s.failure(w, r, chatFailureStatus(failure.Kind), string(failure.Kind), failure.Cause.Error())
			return
		}
		s.readFailure(w, r, err)
		return
	}
	resp := chatResponseDTO{RequestID: requestID, Expected: playerWorldDTO(world), Explanation: guidance.Explanation}
	if guidance.Kind != interpreter.Explain {
		applied, status, failure, err := s.applyChatGuidance(ctx, requestID, world, current, facts.Colony.Tick, guidance)
		if err != nil {
			s.readFailure(w, r, err)
			return
		}
		if failure != nil {
			s.write(w, r, status, failure)
			return
		}
		resp.Guidance = &applied
	}
	s.write(w, r, 201, resp)
}

// applyChatGuidance feeds one nudge into its policy input. A nil failure with
// a nil error means the guidance was applied; a non-nil failure carries the
// same status and body the policy route would have returned.
func (s *Server) applyChatGuidance(ctx context.Context, requestID string, world store.World, current domain.GenerationSnapshot, tick domain.Tick, guidance interpreter.Guidance) (chatGuidanceDTO, int, *Failure, error) {
	dto := chatGuidanceDTO{Kind: string(guidance.Kind)}
	disabled := func(what string) (chatGuidanceDTO, int, *Failure, error) {
		return dto, 404, &Failure{"not_found", what + " is not enabled"}, nil
	}
	check := func(err error) (int, *Failure) {
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			return playerFailure(err)
		}
		return 0, nil
	}
	switch guidance.Kind {
	case interpreter.ActivateGoal:
		player, ok := s.player.(playerGoals)
		if !ok {
			return disabled("Maintained goals")
		}
		q := store.GoalCreateSubmissionRequest{RequestID: requestID, Kind: guidance.ActivateGoal, Snapshot: current, Tick: tick}
		v, _, err := player.SubmitGoalCreate(ctx, q)
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		projected, err := projectGoalCreateSubmission(v)
		if err != nil {
			return dto, 0, nil, err
		}
		dto.Goal = &projected.State
	case interpreter.CancelGoal:
		player, ok := s.player.(playerGoals)
		if !ok {
			return disabled("Maintained goals")
		}
		state, err := s.chatJournal.LoadGoal(ctx, guidance.CancelGoal)
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		v, err := player.CancelGoal(ctx, world, guidance.CancelGoal, state.Revision)
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		if v.Goal.ID != guidance.CancelGoal || v.Goal.Status != domain.GoalCancelled {
			return dto, 0, nil, errors.New("mismatched cancellation")
		}
		projected := goalStateWire(v)
		dto.Goal = &projected
	case interpreter.SetPopulationPolicy:
		player, ok := s.player.(playerPopulationPolicy)
		if !ok {
			return disabled("Population policy")
		}
		v, _, err := player.SubmitPopulationPolicy(ctx, store.PopulationPolicySubmissionRequest{RequestID: requestID, World: world, Policy: guidance.PopulationPolicy})
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		projected, err := projectPopulationPolicySubmission(v)
		if err != nil {
			return dto, 0, nil, err
		}
		dto.PopulationPolicy = &projected.Current
	case interpreter.SetExpeditionPolicy:
		player, ok := s.player.(playerExpeditionPolicy)
		if !ok {
			return disabled("Expedition policy")
		}
		v, _, err := player.SubmitExpeditionPolicy(ctx, store.ExpeditionPolicySubmissionRequest{RequestID: requestID, World: world, Patch: guidance.ExpeditionPolicy})
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		projected, err := projectExpeditionPolicySubmission(v)
		if err != nil {
			return dto, 0, nil, err
		}
		dto.ExpeditionPolicy = &projected.Current
	case interpreter.SetPopulationDecision:
		player, ok := s.player.(playerPopulationDecision)
		if !ok {
			return disabled("Population decisions")
		}
		v, _, err := player.SubmitPopulationDecision(ctx, store.PopulationDecisionSubmissionRequest{RequestID: requestID, World: world, Directive: guidance.PopulationDecision})
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		projected, err := projectPopulationDecisionSubmission(v)
		if err != nil {
			return dto, 0, nil, err
		}
		dto.PopulationDecision = &projected.Current
	case interpreter.SetResourcePolicy:
		player, ok := s.player.(playerResourcePolicy)
		if !ok {
			return disabled("Resource policies")
		}
		v, _, err := player.SubmitResourcePolicy(ctx, store.ResourcePolicySubmissionRequest{RequestID: requestID, World: world, Patch: guidance.ResourcePolicy})
		if status, failure := check(err); failure != nil {
			return dto, status, failure, nil
		}
		projected, err := projectResourcePolicySubmission(v)
		if err != nil {
			return dto, 0, nil, err
		}
		dto.ResourcePolicy = &projected.Current
	default:
		return dto, 0, nil, errors.New("unsupported guidance kind")
	}
	return dto, 0, nil, nil
}

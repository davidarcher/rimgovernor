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

// chatCommandActionID is the single action ID chat allocates for every model
// call. Chat supports only single-action commands (build a single building,
// research): each self-commits its own fresh
// plan at dispatch (see store.ResearchSelectSubmissionRequest), so nothing
// downstream reads this identifier back -- it exists only to satisfy
// interpreter.Interpret's requirement of a nonempty bounded ActionIDs pool.
const chatCommandActionID = domain.ActionID("chat-1")

type chatResponseDTO struct {
	RequestID string                       `json:"requestId"`
	Command   string                       `json:"command"`
	Building  *submissionDTO               `json:"building,omitempty"`
	Research  *researchSelectSubmissionDTO `json:"research,omitempty"`
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
	case interpreter.NoAuthority:
		return 403
	case interpreter.StaleFacts:
		return 409
	case interpreter.ModelFailure:
		return 502
	case interpreter.InvalidCommand, interpreter.UnknownFacts, interpreter.UnsupportedCommand:
		return 422
	default:
		return 400
	}
}

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
	facts, current, err := buildingruntime.GatherChatFacts(ctx, s.chatNative, domain.GenerationSnapshot{Colony: world.Colony, Load: world.Load, Map: world.Map})
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	proposal, err := s.chat.Interpret(ctx, interpreter.Input{
		UserRequest:           message,
		ExplicitPlayerRequest: true,
		Current:               current,
		Facts:                 facts,
		ActionIDs:             []domain.ActionID{chatCommandActionID},
	})
	if err != nil {
		var failure *interpreter.Failure
		if errors.As(err, &failure) {
			s.failure(w, r, chatFailureStatus(failure.Kind), string(failure.Kind), failure.Cause.Error())
			return
		}
		s.readFailure(w, r, err)
		return
	}
	actions := proposal.Plan.Actions()
	if len(actions) != 1 {
		s.failure(w, r, 422, "unsupported_command", "That request needs a command chat does not support yet")
		return
	}
	action := actions[0]
	resp := chatResponseDTO{RequestID: requestID}
	var status int
	var failure *Failure
	if building, ok := action.Building(); ok {
		v, _, err := s.player.Submit(ctx, store.SubmissionRequest{RequestID: requestID, World: world, Building: building})
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			status, failure = playerFailure(err)
		} else {
			dto, projectErr := projectSubmission(v)
			if projectErr != nil {
				s.readFailure(w, r, projectErr)
				return
			}
			resp.Command, resp.Building = "build", &dto
		}
	} else if research, ok := action.ResearchSelect(); ok {
		v, _, err := s.player.SubmitResearchSelect(ctx, store.ResearchSelectSubmissionRequest{RequestID: requestID, World: world, Select: research})
		if err == nil {
			err = ctx.Err()
		}
		if err != nil {
			status, failure = playerFailure(err)
		} else {
			dto, projectErr := projectResearchSelectSubmission(v)
			if projectErr != nil {
				s.readFailure(w, r, projectErr)
				return
			}
			resp.Command, resp.Research = "research", &dto
		}
	} else {
		s.failure(w, r, 422, "unsupported_command", "That request needs a command chat does not support yet")
		return
	}
	if failure != nil {
		s.write(w, r, status, failure)
		return
	}
	s.write(w, r, 201, resp)
}

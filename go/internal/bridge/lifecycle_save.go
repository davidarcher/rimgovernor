package bridge

import (
	"context"
	"errors"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// ErrSaveUncertain means the native checkpoint save outcome is not confirmed:
// identity, tick or pause moved before or during the save. It is never a
// license to retry blindly; observe current state before another attempt.
var ErrSaveUncertain = errors.New("native save outcome uncertain")

// SaveUncertain preserves the observed context reported alongside an uncertain
// save outcome so a caller can decide whether to re-verify or abandon.
type SaveUncertain struct {
	Value   *l.SaveUncertain
	Receipt Result
}

func (e *SaveUncertain) Error() string { return "native save outcome uncertain: " + e.Value.GetDetail() }
func (e *SaveUncertain) Unwrap() error { return ErrSaveUncertain }

// LifecycleSave is a separately held mutation capability, mirroring
// AuthorityControl/ClockControl. Possession of a Client alone grants no save
// authority; the caller must already hold the writer/pause preconditions.
type LifecycleSave struct{ client *Client }

func NewLifecycleSave(client *Client) (*LifecycleSave, error) {
	if client == nil {
		return nil, contract("lifecycle save client required")
	}
	return &LifecycleSave{client}, nil
}

// Save requests one trusted checkpoint save. The request must carry a complete
// player identity/direction/request id and a save name; expected_tick is
// optional but, when set, is asserted before native persists anything.
func (save *LifecycleSave) Save(ctx context.Context, request *l.SaveRequest) (*l.SaveReply, Result, error) {
	if save == nil || save.client == nil {
		return nil, Result{}, contract("lifecycle save capability required")
	}
	if err := validateSaveRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*l.SaveRequest)
	reply := &l.SaveReply{}
	raw, err := save.client.protoCall(ctx, "rimgovernor/lifecycle_save", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *l.SaveReply_Completed:
		if err = validateSaveCompleted(value.Completed, request); err != nil {
			return nil, raw, err
		}
	case *l.SaveReply_Uncertain:
		if value.Uncertain == nil || !diagnostic(value.Uncertain.Detail) {
			return nil, raw, contract("invalid save uncertain")
		}
		if value.Uncertain.ObservedContext != nil {
			if err = ValidateContext(value.Uncertain.ObservedContext); err != nil {
				return nil, raw, err
			}
		}
		return nil, raw, &SaveUncertain{value.Uncertain, raw}
	case *l.SaveReply_Failure:
		return nil, raw, failure(value.Failure, raw)
	default:
		return nil, raw, contract("save outcome missing")
	}
	return reply, raw, nil
}

func validateSaveRequest(request *l.SaveRequest) error {
	if request == nil || request.Player == nil {
		return contract("save request requires a player lifecycle context")
	}
	player := request.Player
	if err := ValidateIdentity(player.Identity); err != nil {
		return err
	}
	if player.RequestId == nil || validID(player.GetRequestId()) != nil {
		return contract("save request requires a request id")
	}
	if player.PlayerDirection == nil || player.GetPlayerDirection() == 0 {
		return contract("save request requires a nonzero player direction")
	}
	if request.SaveName == nil || validID(request.GetSaveName()) != nil {
		return contract("save request requires a save name")
	}
	if request.ExpectedTick != nil && request.GetExpectedTick() < 0 {
		return contract("save request expected tick must be non-negative")
	}
	return nil
}

func validateSaveCompleted(completed *l.SaveCompleted, request *l.SaveRequest) error {
	if completed == nil {
		return contract("missing completed save")
	}
	if err := ValidateContext(completed.Context); err != nil {
		return err
	}
	if !sameIdentity(completed.Context.Identity, request.Player.Identity) {
		return contract("completed save identity mismatch")
	}
	if completed.Paused == nil || !completed.GetPaused() {
		return contract("completed save must report native paused")
	}
	if completed.RequestId == nil || completed.GetRequestId() != request.Player.GetRequestId() {
		return contract("completed save request id mismatch")
	}
	if completed.SaveName == nil || completed.GetSaveName() != request.GetSaveName() {
		return contract("completed save name mismatch")
	}
	if completed.PlayerDirection == nil || completed.GetPlayerDirection() != request.Player.GetPlayerDirection() {
		return contract("completed save direction mismatch")
	}
	if request.ExpectedTick != nil && completed.Context.GetTick() != request.GetExpectedTick() {
		return contract("completed save tick moved")
	}
	return nil
}

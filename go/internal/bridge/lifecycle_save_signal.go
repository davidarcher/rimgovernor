package bridge

import (
	"context"
	"errors"
	"fmt"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

const (
	waitSaveSignalMethod = "rimgovernor/lifecycle_wait_save_signal"
	flushDoneMethod      = "rimgovernor/lifecycle_flush_done"

	// SaveSignalPreSave is the only signal kind native raises (#2358).
	SaveSignalPreSave = "pre_save"

	// waitSaveSignalMargin is how far the call deadline outlasts the native
	// long-poll, so a quiet poll returns its timeout reply instead of a
	// transport deadline.
	waitSaveSignalMargin = 10 * time.Second
)

// ErrStaleSaveToken means FlushDone named a token native no longer holds:
// the save already went ahead after its ~3 s wait, the token was acked
// before, or it predates a Go or game restart. It is never an ack.
var ErrStaleSaveToken = errors.New("stale or unknown save token")

// WaitSaveSignal long-polls native for a vanilla save about to run (#2358).
// It returns the pre_save token the moment native raises it, or ok=false
// when timeout passes (or a newer wait superseded this one) with no signal.
// While the call is held native parks every non-Go-initiated save until the
// token is acked with FlushDone or ~3 s pass. A signal raised while no call
// is held is dropped. A zero timeout asks for native's default.
func (caller *Client) WaitSaveSignal(ctx context.Context, timeout time.Duration) (token string, ok bool, err error) {
	if timeout < 0 {
		return "", false, contract("save signal wait timeout must not be negative")
	}
	request := &l.WaitSaveSignalRequest{}
	if timeout > 0 {
		request.TimeoutMs = proto.Int32(int32(timeout.Milliseconds()))
		ctx = WithCallTimeout(ctx, timeout+waitSaveSignalMargin)
	} else {
		ctx = WithCallTimeout(ctx, 30*time.Second)
	}
	reply := &l.WaitSaveSignalReply{}
	raw, err := caller.protoCall(ctx, waitSaveSignalMethod, request, reply)
	if err != nil {
		return "", false, err
	}
	switch value := reply.Outcome.(type) {
	case *l.WaitSaveSignalReply_Signal:
		signal := value.Signal
		if signal == nil || signal.GetKind() != SaveSignalPreSave || validID(signal.GetToken()) != nil {
			return "", false, contract("invalid save signal")
		}
		return signal.GetToken(), true, nil
	case *l.WaitSaveSignalReply_Timeout:
		return "", false, nil
	case *l.WaitSaveSignalReply_Failure:
		return "", false, failure(value.Failure, raw)
	default:
		return "", false, contract("save signal outcome missing")
	}
}

// FlushDone acks a pre_save token so native lets the parked save run. A token
// native no longer holds fails with ErrStaleSaveToken.
func (caller *Client) FlushDone(ctx context.Context, token string) error {
	if validID(token) != nil {
		return contract("flush done requires a token")
	}
	reply := &l.FlushDoneReply{}
	raw, err := caller.protoCall(ctx, flushDoneMethod, &l.FlushDoneRequest{Token: &token}, reply)
	if err != nil {
		return err
	}
	switch value := reply.Outcome.(type) {
	case *l.FlushDoneReply_Acked:
		return nil
	case *l.FlushDoneReply_Failure:
		err = failure(value.Failure, raw)
		if value.Failure.GetCode() == c.FailureCode_FAILURE_CODE_NOT_FOUND {
			return fmt.Errorf("%w: %w", ErrStaleSaveToken, err)
		}
		return err
	default:
		return contract("flush done outcome missing")
	}
}

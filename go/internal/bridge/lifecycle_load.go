package bridge

import (
	"context"
	"errors"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// ErrLoadSuperseded means a Load/ReadLoad request no longer names the load
// native is actually tracking: a later Load call preempted it, or the
// observed post-load colony does not match what the caller expected. It is
// never a license to treat the request as completed or to retry blindly.
var ErrLoadSuperseded = errors.New("native load outcome superseded")

// LoadSuperseded preserves the observed context (when native could report
// one) alongside a superseded outcome so a caller can decide whether to
// re-read identity or abandon the request.
type LoadSuperseded struct {
	Value   *l.LoadSuperseded
	Receipt Result
}

func (e *LoadSuperseded) Error() string {
	return "native load outcome superseded: " + e.Value.GetDetail()
}
func (e *LoadSuperseded) Unwrap() error { return ErrLoadSuperseded }

// LifecyclePending carries a still-in-progress LoadPending outcome. It is not
// an error in the Go sense of "something went wrong" -- it means the caller
// must poll ReadLoad again -- but it is returned as an error so callers
// cannot accidentally treat a nil reply as success.
var ErrLoadPending = errors.New("native load still pending")

type LoadPending struct {
	Value   *l.LoadPending
	Receipt Result
}

func (e *LoadPending) Error() string { return "native load still pending: " + e.Value.GetDetail() }
func (e *LoadPending) Unwrap() error { return ErrLoadPending }

// LifecycleLoad is a separately held mutation capability, mirroring
// LifecycleSave. Possession of a Client alone grants no load authority.
type LifecycleLoad struct{ client *Client }

func NewLifecycleLoad(client *Client) (*LifecycleLoad, error) {
	if client == nil {
		return nil, contract("lifecycle load client required")
	}
	return &LifecycleLoad{client}, nil
}

// Load starts a native load of the named save and returns immediately: a
// LoadCompleted/Failure only for an outright pre-flight rejection, or (the
// ordinary case) a LoadPending the caller must poll for with ReadLoad. It is
// never a synchronous wait for the load itself to finish.
func (load *LifecycleLoad) Load(ctx context.Context, request *l.LoadRequest) (*l.LoadReply, Result, error) {
	if load == nil || load.client == nil {
		return nil, Result{}, contract("lifecycle load capability required")
	}
	if err := validateLoadRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*l.LoadRequest)
	reply := &l.LoadReply{}
	raw, err := load.client.protoCall(ctx, "rimgovernor/lifecycle_load", request, reply)
	if err != nil {
		return nil, raw, err
	}
	return interpretLoadReply(reply, request.RequestId, request.SaveName, raw)
}

// ReadLoad polls for the outcome of a request_id a prior Load call started.
func (load *LifecycleLoad) ReadLoad(ctx context.Context, requestID string) (*l.LoadReply, Result, error) {
	if load == nil || load.client == nil {
		return nil, Result{}, contract("lifecycle load capability required")
	}
	if validID(requestID) != nil {
		return nil, Result{}, contract("read load requires a request id")
	}
	request := &l.RequestStatus{RequestId: proto.String(requestID)}
	reply := &l.LoadReply{}
	raw, err := load.client.protoCall(ctx, "rimgovernor/lifecycle_read_load", request, reply)
	if err != nil {
		return nil, raw, err
	}
	return interpretLoadReply(reply, request.RequestId, nil, raw)
}

func interpretLoadReply(reply *l.LoadReply, requestID, saveName *string, raw Result) (*l.LoadReply, Result, error) {
	switch value := reply.Outcome.(type) {
	case *l.LoadReply_Completed:
		if err := validateLoadCompleted(value.Completed, requestID, saveName); err != nil {
			return nil, raw, err
		}
	case *l.LoadReply_Pending:
		if value.Pending == nil || value.Pending.RequestId == nil || requestID != nil && value.Pending.GetRequestId() != *requestID || !diagnostic(value.Pending.Detail) {
			return nil, raw, contract("invalid load pending")
		}
		return nil, raw, &LoadPending{value.Pending, raw}
	case *l.LoadReply_Superseded:
		if value.Superseded == nil || value.Superseded.RequestId == nil || requestID != nil && value.Superseded.GetRequestId() != *requestID || !diagnostic(value.Superseded.Detail) {
			return nil, raw, contract("invalid load superseded")
		}
		if value.Superseded.ObservedContext != nil {
			if err := ValidateContext(value.Superseded.ObservedContext); err != nil {
				return nil, raw, err
			}
		}
		return nil, raw, &LoadSuperseded{value.Superseded, raw}
	case *l.LoadReply_Failure:
		return nil, raw, failure(value.Failure, raw)
	default:
		return nil, raw, contract("load outcome missing")
	}
	return reply, raw, nil
}

func validateLoadRequest(request *l.LoadRequest) error {
	if request == nil {
		return contract("load request required")
	}
	if request.RequestId == nil || validID(request.GetRequestId()) != nil {
		return contract("load request requires a request id")
	}
	if request.SaveName == nil || validID(request.GetSaveName()) != nil {
		return contract("load request requires a save name")
	}
	if request.Readiness != nil {
		if _, ok := l.Readiness_name[int32(request.GetReadiness())]; !ok {
			return contract("load request readiness invalid")
		}
	}
	if request.ExpectedPlayer != nil {
		player := request.ExpectedPlayer
		if player.Identity != nil {
			if err := ValidateIdentity(player.Identity); err != nil {
				return err
			}
		}
		if player.RequestId != nil && validID(player.GetRequestId()) != nil {
			return contract("load request expected player request id invalid")
		}
	}
	if request.ExpectedInstanceId != nil && validID(request.GetExpectedInstanceId()) != nil {
		return contract("load request expected instance id invalid")
	}
	return nil
}

func validateLoadCompleted(completed *l.LoadCompleted, requestID, saveName *string) error {
	if completed == nil || completed.Loaded == nil {
		return contract("missing completed load")
	}
	if err := ValidateContext(completed.Loaded.Context); err != nil {
		return err
	}
	if completed.RequestId == nil || requestID != nil && completed.GetRequestId() != *requestID {
		return contract("completed load request id mismatch")
	}
	if saveName != nil && (completed.SaveName == nil || completed.GetSaveName() != *saveName) {
		return contract("completed load save name mismatch")
	}
	if completed.Readiness == nil {
		return contract("completed load missing readiness")
	}
	if _, ok := l.Readiness_name[int32(completed.GetReadiness())]; !ok || completed.GetReadiness() == l.Readiness_READINESS_UNSPECIFIED {
		return contract("completed load readiness invalid")
	}
	return nil
}

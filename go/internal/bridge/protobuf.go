package bridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"strings"
	"unicode/utf8"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/wrapperspb"
)

const maxProtoBytes = 1 << 20

var ErrUnavailable = errors.New("native observation unavailable")

// NativeFailure is an explicit typed pre-admission refusal, never a transport inference.
type NativeFailure struct {
	Value   *c.Failure
	Receipt Result
}

func (e *NativeFailure) Error() string { return "native read refused: " + e.Value.GetCode().String() }
func (e *NativeFailure) Unwrap() error { return ErrRefused }

type NativeUnavailable struct {
	Value   *c.Unavailable
	Receipt Result
}

func (e *NativeUnavailable) Error() string {
	return "native read unavailable: " + e.Value.GetReason().String()
}
func (e *NativeUnavailable) Unwrap() error { return ErrUnavailable }

func (caller *Client) Identity(ctx context.Context) (*l.IdentityReply, Result, error) {
	reply := &l.IdentityReply{}
	raw, err := caller.protoRead(ctx, "rimgovernor/lifecycle_read_identity", &l.IdentityRequest{}, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *l.IdentityReply_Loaded:
		if value.Loaded == nil {
			return nil, raw, contract("missing loaded identity")
		}
		if err = ValidateContext(value.Loaded.Context); err == nil {
			if len(value.Loaded.Capabilities) > 78 {
				err = contract("too many capabilities")
			}
			seen := map[string]bool{}
			for _, cap := range value.Loaded.Capabilities {
				if cap == nil || cap.Support == nil || cap.GetSupport() < 1 || cap.GetSupport() > 3 || validID(cap.GetFullMethodName()) != nil || !diagnostic(cap.Detail) || seen[cap.GetFullMethodName()] {
					err = contract("invalid capability")
					break
				}
				seen[cap.GetFullMethodName()] = true
			}
		}
	case *l.IdentityReply_Unavailable:
		err = unavailable(value.Unavailable, raw)
	case *l.IdentityReply_Failure:
		err = failure(value.Failure, raw)
	default:
		err = contract("identity outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) && !errors.Is(err, ErrUnavailable) {
		return nil, raw, err
	}
	return reply, raw, err
}

// Status requests context only. Rich colonist/threat projections need their own
// complete typed observation adapters before being exposed to callers.
func (caller *Client) Status(ctx context.Context, identity *c.Identity) (*o.StatusReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.StatusRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Colonists: proto.Bool(false), Threats: proto.Bool(false), ColonistDetail: proto.Bool(false)}
	reply := &o.StatusReply{}
	raw, err := caller.protoRead(ctx, "rimgovernor/observations_read_status", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *o.StatusReply_Observed:
		if value.Observed == nil {
			return nil, raw, contract("missing status")
		}
		v := value.Observed
		err = ValidateContext(v.Context)
		if err == nil && !sameIdentity(v.Context.Identity, identity) {
			err = contract("status identity mismatch")
		}
		if v.Colonists != nil || v.Threats != nil {
			err = contract("unrequested status section")
		}
		if len(v.Issues) > 256 {
			err = contract("too many status issues")
		}
		for _, issue := range v.Issues {
			if issue == nil || validID(issue.GetField()) != nil {
				err = contract("invalid read issue")
				break
			}
			if problem := validateUnavailable(issue.Unavailable); problem != nil {
				err = problem
				break
			}
		}
	case *o.StatusReply_Unavailable:
		err = unavailable(value.Unavailable, raw)
	case *o.StatusReply_Failure:
		err = failure(value.Failure, raw)
	default:
		err = contract("status outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) && !errors.Is(err, ErrUnavailable) {
		return nil, raw, err
	}
	return reply, raw, err
}
func (caller *Client) PlacementPreviews(ctx context.Context, request *p.PlacementRequest) (*p.PlacementReply, Result, error) {
	if err := validatePlacementRequest(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*p.PlacementRequest)
	reply := &p.PlacementReply{}
	raw, err := caller.protoRead(ctx, "rimgovernor/placement_preview", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *p.PlacementReply_Batch:
		err = validatePlacementBatch(request, value.Batch)
	case *p.PlacementReply_Failure:
		err = failure(value.Failure, raw)
	default:
		err = contract("placement outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) {
		return nil, raw, err
	}
	return reply, raw, err
}
func (caller *Client) protoRead(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	switch name {
	case "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications":
	case "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress":
	default:
		return Result{}, contract("unreviewed native read")
	}
	return caller.protoCall(ctx, name, request, reply)
}

// protoCall is the closed transport seam for reviewed typed adapters. Adapters
// validate request semantics and apply their own read or explicit write capability.
func (caller *Client) protoCall(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	switch name {
	case "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications":
	case "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress", "rimgovernor/authority_control", "rimgovernor/operations_execute", "rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/clock_change_speed", "rimgovernor/clock_pause":
	default:
		return Result{}, contract("unreviewed native method")
	}
	inner, err := protojson.Marshal(request)
	if err != nil {
		return Result{}, contract("request encoding: %v", err)
	}
	if len(inner) > maxProtoBytes {
		return Result{}, contract("oversized request")
	}
	args := encode(struct {
		Request string `json:"request"`
	}{string(inner)})
	invoked := false
	result, err := caller.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		detail, err := caller.describe(ctx, live, name)
		if err != nil {
			return detail, err
		}
		if err = validateOwnedStringInput(detail.Structured, "request"); err != nil {
			return Result{}, err
		}
		invoked = true
		return caller.core(ctx, live, "games_call_tool", encode(nativeArgument{caller.gameID, name, args}))
	})
	callErr := err
	if err != nil && (!errors.Is(err, ErrRefused) || !invoked) {
		return result, err
	}
	payload, err := decodePayload(result.Structured)
	if err != nil {
		if callErr != nil {
			return result, callErr
		}
		return result, err
	}
	if err = (protojson.UnmarshalOptions{DiscardUnknown: false, RecursionLimit: 64}).Unmarshal(payload, reply); err != nil {
		if callErr != nil {
			return result, callErr
		}
		return result, contract("reply parsing: %v", err)
	}
	if callErr != nil {
		typedFailure := false
		switch r := reply.(type) {
		case *k.EventsReply:
			typedFailure = r.GetFailure() != nil
		case *k.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *k.ControlReply:
			typedFailure = r.GetFailure() != nil
		case *k.AttemptReply:
			typedFailure = r.GetFailure() != nil
		case *l.IdentityReply:
			typedFailure = r.GetFailure() != nil
		case *o.GetCellsReply:
			typedFailure = r.GetFailure() != nil
		case *o.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *p.PlacementReply:
			typedFailure = r.GetFailure() != nil
		case *a.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *a.ControlReply:
			typedFailure = r.GetFailure() != nil
		case *op.ExecuteReply:
			typedFailure = r.GetFailure() != nil
		case *r.LookupReply:
			typedFailure = r.GetFailure() != nil
		case *r.ProgressReply:
			typedFailure = r.GetFailure() != nil
		}
		if !typedFailure {
			return result, callErr
		}
	}
	return result, nil
}
func decodePayload(raw []byte) ([]byte, error) {
	if len(raw) == 0 || len(raw) > maxResponseBytes {
		return nil, contract("invalid wrapper size")
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	token, err := d.Token()
	if err != nil || token != json.Delim('{') {
		return nil, contract("reply wrapper must be object")
	}
	fields := map[string]json.RawMessage{}
	for d.More() {
		token, err = d.Token()
		if err != nil {
			return nil, contract("wrapper key")
		}
		key, ok := token.(string)
		if !ok {
			return nil, contract("wrapper key")
		}
		if _, ok = fields[key]; ok {
			return nil, contract("duplicate wrapper key")
		}
		if key != "payload" && key != "operation" {
			return nil, contract("unknown wrapper field %s", key)
		}
		var value json.RawMessage
		if err = d.Decode(&value); err != nil {
			return nil, contract("wrapper value")
		}
		fields[key] = value
	}
	if _, err = d.Token(); err != nil {
		return nil, contract("wrapper end")
	}
	if _, err = d.Token(); err != io.EOF {
		return nil, contract("trailing wrapper data")
	}
	value := fields["payload"]
	if len(value) == 0 || value[0] != '"' {
		return nil, contract("payload must be string")
	}
	text := &wrapperspb.StringValue{}
	if err = protojson.Unmarshal(value, text); err != nil {
		return nil, contract("invalid payload string")
	}
	if len(text.Value) > maxProtoBytes {
		return nil, contract("oversized payload")
	}
	return []byte(text.Value), nil
}
func contract(format string, args ...any) error {
	return fmt.Errorf("%w: %s", ErrContract, fmt.Sprintf(format, args...))
}
func validID(value string) error {
	if !utf8.ValidString(value) || strings.TrimSpace(value) == "" || len(value) > 256 || strings.ContainsRune(value, 0) {
		return contract("invalid native identifier")
	}
	return nil
}
func diagnostic(value *string) bool {
	return value == nil || (utf8.ValidString(*value) && utf8.RuneCountInString(*value) <= 4096)
}
func ValidateIdentity(value *c.Identity) error {
	if value == nil || value.ColonyId == nil || value.LoadToken == nil || value.MapId == nil || value.GetMapId() < 0 {
		return contract("identity presence or map")
	}
	if len(value.ProtoReflect().GetUnknown()) != 0 {
		return contract("unknown identity fields")
	}
	return errors.Join(validID(value.GetColonyId()), validID(value.GetLoadToken()))
}
func ValidateContext(value *c.ObservationContext) error {
	if value == nil || value.Tick == nil || value.GetTick() < 0 || (value.NativeGeneration != nil && value.GetNativeGeneration() == 0) {
		return contract("context presence/tick/generation")
	}
	return ValidateIdentity(value.Identity)
}
func sameIdentity(a, b *c.Identity) bool {
	return a.GetColonyId() == b.GetColonyId() && a.GetLoadToken() == b.GetLoadToken() && a.GetMapId() == b.GetMapId()
}
func validateUnavailable(v *c.Unavailable) error {
	if v == nil || v.Reason == nil || v.GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_UNSPECIFIED || v.GetReason().Descriptor().Values().ByNumber(v.GetReason().Number()) == nil || !diagnostic(v.Detail) {
		return contract("invalid unavailable")
	}
	return nil
}
func unavailable(v *c.Unavailable, raw Result) error {
	if err := validateUnavailable(v); err != nil {
		return err
	}
	return &NativeUnavailable{v, raw}
}
func failure(v *c.Failure, raw Result) error {
	if v == nil || v.Code == nil || v.GetCode() < 1 || v.GetCode() > 14 || !diagnostic(v.Detail) {
		return contract("invalid failure")
	}
	if v.ObservedContext != nil {
		if err := ValidateContext(v.ObservedContext); err != nil {
			return err
		}
	}
	return &NativeFailure{v, raw}
}

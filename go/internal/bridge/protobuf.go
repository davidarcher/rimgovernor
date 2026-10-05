package bridge

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	pr "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// maxReplyProtoBytes bounds one gunzipped reply, live or recorded (a
// decompression-bomb guard under the 50 MiB GABP frame); #320 removed every
// smaller payload budget.
const maxReplyProtoBytes = 48 << 20

var ErrUnavailable = errors.New("native observation unavailable")

// NativeFailure is an explicit typed pre-admission refusal, never a transport inference.
type NativeFailure struct {
	Value   *c.Failure
	Receipt Result
}

// The native detail names the rule that refused, so a controller log is
// diagnosable without the game log; it is bounded native diagnostic text.
func (e *NativeFailure) Error() string {
	if detail := e.Value.GetDetail(); detail != "" {
		return "native read refused: " + e.Value.GetCode().String() + ": " + detail
	}
	return "native read refused: " + e.Value.GetCode().String()
}
func (e *NativeFailure) Unwrap() error { return ErrRefused }

type NativeUnavailable struct {
	Value   *c.Unavailable
	Receipt Result
}

func (e *NativeUnavailable) Error() string {
	// The native detail names the bound or field that failed; without it a
	// LIMIT_EXCEEDED census is undiagnosable from the flight recorder.
	if detail := e.Value.GetDetail(); detail != "" {
		return "native read unavailable: " + e.Value.GetReason().String() + ": " + detail
	}
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

// Tick is the bare observation scope: Identity's context and pause state
// without the capability list. It is the read for callers that only need
// the current load, map, tick and generation; the full identity read stays
// with the scheduler step, which validates the capability list once.
func (caller *Client) Tick(ctx context.Context) (*l.TickReply, Result, error) {
	reply := &l.TickReply{}
	raw, err := caller.protoRead(ctx, "rimgovernor/lifecycle_read_tick", &l.TickRequest{}, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *l.TickReply_Loaded:
		if value.Loaded == nil {
			return nil, raw, contract("missing loaded tick")
		}
		err = ValidateContext(value.Loaded.Context)
	case *l.TickReply_Unavailable:
		err = unavailable(value.Unavailable, raw)
	case *l.TickReply_Failure:
		err = failure(value.Failure, raw)
	default:
		err = contract("tick outcome missing")
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
	request := &o.StatusRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Colonists: proto.Bool(false), Threats: proto.Bool(false)}
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

// nativeReadMethod reports whether name is a reviewed read: a method with no
// game-state side effect. Everything else protoCall admits is a write.
func nativeReadMethod(name string) bool {
	switch name {
	case clearanceTool, shrinesTool, plantCutCensusTool, "rimgovernor/observations_list_supplies", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_buildings", "rimgovernor/observations_list_rooms", "rimgovernor/observations_read_research", methodDefinitionCatalog, "rimgovernor/observations_list_wall_upgrade_sites", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_defense_site", "rimgovernor/observations_read_lines_of_fire", "rimgovernor/observations_read_spatial_access", "rimgovernor/observations_read_husbandry":
	case "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications", "rimgovernor/presentation_render_state":
	case combatGeometryMethod:
	case "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/zones_preview", "rimgovernor/observations_list_pawns", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick", "rimgovernor/lifecycle_read_governor_state", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/observations_read_world_progression", "rimgovernor/observations_read_world", "rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_read_population", "rimgovernor/observations_read_trade_sheet", "rimgovernor/observations_read_trade_session", "rimgovernor/observations_list_traders", "rimgovernor/observations_read_excavation_site", methodOpenSnapshotStream:
	default:
		return false
	}
	return true
}

// WritesPending reports whether a typed side-effect call is queued or in
// flight on this client: the clock poll loop then polls without waiting,
// so a command never waits behind an idle poll on a transport that
// serializes calls.
func (caller *Client) WritesPending() bool { return caller.writes.Load() > 0 }

func (caller *Client) protoRead(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	if !nativeReadMethod(name) {
		return Result{}, contract("unreviewed native read")
	}
	noteNativeRead(ctx, name)
	if served, err := caller.frameRead(ctx, name, request, reply); served {
		return Result{}, err
	}
	return caller.protoCall(ctx, name, request, reply)
}

// reviewedNativeMethods is protoCall's allowlist: the typed adapters this
// package exposes. Every entry has an admission class in
// nativeAdmissionClass (admission.go).
var reviewedNativeMethods = map[string]bool{
	clearanceTool:                            true,
	shrinesTool:                              true,
	"rimgovernor/observations_list_supplies": true,
	"rimgovernor/observations_read_colony_facts":       true,
	"rimgovernor/observations_list_buildings":          true,
	"rimgovernor/observations_list_rooms":              true,
	"rimgovernor/observations_read_research":           true,
	methodDefinitionCatalog:                            true,
	"rimgovernor/observations_list_wall_upgrade_sites": true,
	"rimgovernor/observations_list_zones":              true,
	"rimgovernor/observations_read_defense_site":       true,
	"rimgovernor/observations_read_lines_of_fire":      true,
	"rimgovernor/combat_geometry":                      true,
	"rimgovernor/observations_read_spatial_access":     true,
	"rimgovernor/observations_read_husbandry":          true,
	"rimgovernor/presentation_camera":                  true,
	"rimgovernor/presentation_selection":               true,
	"rimgovernor/presentation_colonists":               true,
	"rimgovernor/presentation_notifications":           true,
	"rimgovernor/presentation_render_state":            true,
	"rimgovernor/presentation_render_demand":           true,
	"rimgovernor/clock_read_events":                    true,
	"rimgovernor/clock_read_status":                    true,
	"rimgovernor/clock_read_attempt":                   true,
	"rimgovernor/zones_preview":                        true,
	"rimgovernor/observations_list_pawns":              true,
	"rimgovernor/observations_get_cells":               true,
	"rimgovernor/lifecycle_read_identity":              true,
	"rimgovernor/lifecycle_read_tick":                  true,
	"rimgovernor/lifecycle_read_governor_state":        true,
	"rimgovernor/lifecycle_put_governor_state":         true,
	"rimgovernor/observations_read_status":             true,
	"rimgovernor/placement_preview":                    true,
	"rimgovernor/authority_read_status":                true,
	"rimgovernor/receipts_lookup":                      true,
	"rimgovernor/authority_control":                    true,
	ActionsApplyMethod:                                 true,
	"rimgovernor/clock_start":                          true,
	"rimgovernor/clock_renew":                          true,
	"rimgovernor/clock_change_speed":                   true,
	"rimgovernor/clock_pause":                          true,
	"rimgovernor/observations_read_world_progression":  true,
	"rimgovernor/observations_read_world":              true,
	"rimgovernor/observations_read_bills":              true,
	"rimgovernor/observations_read_recipes":            true,
	"rimgovernor/observations_list_resource_sources":   true,
	"rimgovernor/observations_read_population":         true,
	"rimgovernor/observations_read_trade_sheet":        true,
	"rimgovernor/observations_read_trade_session":      true,
	"rimgovernor/observations_list_traders":            true,
	"rimgovernor/observations_read_excavation_site":    true,
	plantCutCensusTool:                                 true,
	"rimgovernor/lifecycle_save":                       true,
	"rimgovernor/presentation_overlay":                 true,
	"rimgovernor/lifecycle_read_save":                  true,
	"rimgovernor/lifecycle_load":                       true,
	"rimgovernor/lifecycle_read_load":                  true,
	"rimgovernor/lifecycle_new_colony":                 true,
	"rimgovernor/lifecycle_read_new_colony":            true,
	methodOpenSnapshotStream:                           true,
	methodFlushSnapshot:                                true,
}

// protoCall is the closed transport seam for reviewed typed adapters. Adapters
// validate request semantics and apply their own read or explicit write capability.
func (caller *Client) protoCall(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	if !reviewedNativeMethods[name] {
		return Result{}, contract("unreviewed native method")
	}
	if !nativeReadMethod(name) {
		// A queued write tells the clock poll loop not to hold its
		// next poll: the nativeaccept client serializes calls.
		caller.writes.Add(1)
		defer caller.writes.Add(-1)
	}
	if !nativeReadMethod(name) {
		defer caller.noteFrameWrite()
	}
	inner, err := protojson.Marshal(request)
	if err != nil {
		return Result{}, contract("request encoding: %v", err)
	}
	ctx = withRecordedReply(ctx, reply)
	invoked := false
	// The call's native_call row waits here for the reply decode below, so a
	// typed call is still one row (#2057).
	ctx, parked := withDeferredCall(ctx)
	var decodeExtra map[string]any
	defer func() { parked.flush(decodeExtra) }()
	class := admissionClassOf(name)
	result, err := caller.operation(ctx, class, func(ctx context.Context, live *liveSession) (Result, error) {
		// Nothing before games_call_tool reaches native: a failure here is
		// proof the write was never issued (domain.ErrWriteUnsent).
		if detail, err := caller.ensureDescribed(ctx, live, name); err != nil {
			return detail, fmt.Errorf("%w: %w", domain.ErrWriteUnsent, err)
		}
		invoked = true
		// The trace the call runs under (the caller's step or dispatch,
		// else the operation's own) rides beside the request; the
		// companion echoes it in its timing object, so its main-thread
		// phases join the same trace as the bridge's own (#298). The
		// admission class rides with it so the companion services queued
		// control hops first within a frame (#631).
		args := encode(struct {
			Request  string `json:"request"`
			Trace    string `json:"trace,omitempty"`
			Class    string `json:"class,omitempty"`
			Encoding string `json:"encoding"`
		}{string(inner), telemetry.TraceFrom(ctx).Wire(), string(class), caller.replies.encoding()})
		call := encode(nativeArgument{caller.gameID, name, args})
		result, err := caller.core(ctx, live, "games_call_tool", call)
		if id, ok := BlockingAttentionID(err); ok {
			// The game logged an error (a load warning, a mod message) and holds
			// every call until it is acknowledged; acknowledge and retry once.
			if _, ackErr := caller.core(ctx, live, "games_ack_attention", encode(attentionArgument{caller.gameID, id})); ackErr == nil {
				result, err = caller.core(ctx, live, "games_call_tool", call)
			}
		}
		return result, err
	})
	callErr := err
	if err != nil && (!errors.Is(err, ErrRefused) || !invoked) {
		return result, err
	}
	decodeBegan := time.Now()
	wire, err := decodeWrapper(result.Structured, maxReplyProtoBytes, result.slotted)
	if err != nil {
		if callErr != nil {
			return result, callErr
		}
		return result, err
	}
	err = unmarshalReply(wire, reply)
	if caller.recorder != nil && invoked {
		// Reply decoding is the typed adapter's own cost; it joins the
		// call's native_call timing. payload_bytes is the decoded binary
		// reply, wire_bytes the JSON value that carried it.
		decodeExtra = map[string]any{"proto_decode_ms": millis(time.Since(decodeBegan)), "payload_bytes": len(wire.data), "wire_bytes": wire.wire}
		if err != nil {
			decodeExtra["proto_decode_error"] = err.Error()
		}
	}
	if err != nil {
		if callErr != nil {
			return result, callErr
		}
		return result, contract("reply parsing: %v", err)
	}
	if callErr != nil {
		typedFailure := false
		switch r := reply.(type) {
		case *pr.CameraReply:
			typedFailure = r.GetFailure() != nil
		case *pr.SelectionReply:
			typedFailure = r.GetFailure() != nil
		case *pr.ColonistRosterReply:
			typedFailure = r.GetFailure() != nil
		case *pr.NotificationsReply:
			typedFailure = r.GetFailure() != nil
		case *pr.RenderReply:
			typedFailure = r.GetFailure() != nil
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
		case *l.SaveReply:
			typedFailure = r.GetFailure() != nil
		case *o.ListPawnsReply:
			typedFailure = r.GetFailure() != nil
		case *o.ListBuildingsReply:
			typedFailure = r.GetFailure() != nil
		case *o.ListRoomsReply:
			typedFailure = r.GetFailure() != nil
		case *o.ColonyFactsReply:
			typedFailure = r.GetFailure() != nil
		case *o.GetCellsReply:
			typedFailure = r.GetFailure() != nil
		case *o.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *o.ResourceSourcesReply:
			typedFailure = r.GetFailure() != nil
		case *p.PlacementReply:
			typedFailure = r.GetFailure() != nil
		case *a.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *a.ControlReply:
			typedFailure = r.GetFailure() != nil
		case *op.ZonePreviewReply:
			typedFailure = r.GetFailure() != nil
		case *op.ApplyReply:
			typedFailure = r.GetBatchFailure() != nil
		case *r.LookupReply:
			typedFailure = r.GetFailure() != nil
		}
		if !typedFailure {
			return result, callErr
		}
	}
	return result, nil
}

// ensureDescribed validates the method's owned string-wrapper input schema
// once per live session (liveSession.described). A failed describe is never
// remembered, so the next call retries it.
func (caller *Client) ensureDescribed(ctx context.Context, live *liveSession, name string) (Result, error) {
	live.describeMu.Lock()
	known := live.described[name]
	live.describeMu.Unlock()
	if known {
		return Result{}, nil
	}
	detail, err := caller.describe(ctx, live, name)
	if err != nil {
		return detail, fmt.Errorf("describe %s: %w", name, err)
	}
	if err = validateOwnedStringInput(detail.Structured, "request"); err != nil {
		return Result{}, fmt.Errorf("describe %s: %w", name, err)
	}
	live.describeMu.Lock()
	if live.described == nil {
		live.described = map[string]bool{}
	}
	live.described[name] = true
	live.describeMu.Unlock()
	return Result{}, nil
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

// Reference is anything a table row is looked up by: a Ref (#1342).
type Reference interface{ GetId() string }

// validRef reports a reference with a valid id and nothing else (#1342).
func validRef(ref *c.Ref) bool {
	return ref != nil && validID(ref.GetId()) == nil && len(ref.ProtoReflect().GetUnknown()) == 0
}

// refSnapshot reports snapshot as ref's CAS token read under ctx (#1342).
func refSnapshot(snapshot *o.SnapshotRef, ref *c.Ref, ctx *c.ObservationContext) bool {
	return snapshot != nil && ref != nil && snapshot.GetEntityId() == ref.GetId() && validID(snapshot.GetToken()) == nil && proto.Equal(snapshot.Context, ctx)
}

// optionalRef reports an absent reference or a valid one.
func optionalRef(ref *c.Ref) bool { return ref == nil || validRef(ref) }

// validRefs reports every reference valid and distinct.
func validRefs(refs []*c.Ref) bool {
	seen := make(map[string]bool, len(refs))
	for _, ref := range refs {
		if !validRef(ref) || seen[ref.GetId()] {
			return false
		}
		seen[ref.GetId()] = true
	}
	return true
}

// refID is ref's id field, nil without a reference.
func refID(ref *c.Ref) *string {
	if ref == nil {
		return nil
	}
	return ref.Id
}

// RefIDs are refs' ids, in order.
func RefIDs(refs []*c.Ref) []string {
	out := make([]string, 0, len(refs))
	for _, ref := range refs {
		out = append(out, ref.GetId())
	}
	return out
}

// NewRef points at id; nil for an empty id.
func NewRef(id string) *c.Ref {
	if id == "" {
		return nil
	}
	return &c.Ref{Id: &id}
}

// NewRefs points at each of ids.
func NewRefs(ids []string) []*c.Ref {
	out := make([]*c.Ref, 0, len(ids))
	for _, id := range ids {
		out = append(out, &c.Ref{Id: &id})
	}
	return out
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

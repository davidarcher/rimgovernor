package bridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
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
	"google.golang.org/protobuf/types/known/wrapperspb"
)

const maxProtoBytes = 1 << 20

// maxMediaProtoBytes bounds the dedicated media ProtoJSON envelope: a raw
// 3840x2160 RGBA32 frame base64-encoded is ~42 MiB (presentation.proto,
// MediaFrame.data). Only the media methods below decode against it.
const maxMediaProtoBytes = 48 << 20

func payloadLimit(name string) int {
	switch name {
	case "rimgovernor/presentation_read_frame", "rimgovernor/presentation_capture_pawn":
		return maxMediaProtoBytes
	}
	return maxProtoBytes
}

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
	// LIMIT_EXCEEDED census is undiagnosable from the service log.
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

// nativeReadMethod reports whether name is a reviewed read: a method with no
// game-state side effect. Everything else protoCall admits is a write.
func nativeReadMethod(name string) bool {
	switch name {
	case "rimgovernor/observations_list_supplies", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_buildings", "rimgovernor/observations_list_rooms", "rimgovernor/observations_read_research", "rimgovernor/observations_list_wall_upgrade_sites", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_defense_site", "rimgovernor/observations_read_lines_of_fire", "rimgovernor/observations_read_spatial_access":
	case "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications", "rimgovernor/presentation_render_state":
	case "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/operations_preview", "rimgovernor/observations_list_pawns", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress", "rimgovernor/observations_read_caravan_catalog", "rimgovernor/observations_read_world_progression", "rimgovernor/observations_read_world", "rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_read_production_policy", "rimgovernor/observations_read_population", "rimgovernor/observations_read_trade_sheet", "rimgovernor/observations_list_traders", "rimgovernor/observations_read_excavation_site", "rimgovernor/observations_read_bundle":
	default:
		return false
	}
	return true
}

func (caller *Client) protoRead(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	if !nativeReadMethod(name) {
		return Result{}, contract("unreviewed native read")
	}
	cache := StepReadCacheFrom(ctx)
	if cache == nil || !cacheableRead(name) {
		return caller.protoCall(ctx, name, request, reply)
	}
	return caller.cachedRead(ctx, cache, name, request, reply)
}

// cachedRead serves a pure observation read through the step's cache: the
// first caller of a (method, request) pair reads natively and stores the
// reply; identical reads in the same step, concurrent or later, decode the
// stored bytes instead of crossing the bridge. A stored reply is decoded
// into the caller's own message so validation downstream is unchanged.
func (caller *Client) cachedRead(ctx context.Context, cache *StepReadCache, name string, request, reply proto.Message) (Result, error) {
	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
	if err != nil {
		return Result{}, contract("request encoding: %v", err)
	}
	key := readCacheKey{method: name, request: string(encoded)}
	entry, leader := cache.acquire(key)
	if !leader {
		var payload []byte
		var result Result
		var ok bool
		select {
		case <-entry.done:
			payload, result, ok = entry.payload, entry.result, entry.ok
			if ok {
				cache.hit()
			}
		default:
			payload, result, ok, err = cache.wait(ctx, entry)
			if err != nil {
				return Result{}, err
			}
		}
		if ok {
			if err = proto.Unmarshal(payload, reply); err == nil {
				caller.recordCacheHit(ctx, name)
				return result, nil
			}
		}
		// The leader failed or its reply was uncacheable: read natively.
		return caller.protoCall(ctx, name, request, reply)
	}
	if payload, result, ok := cache.fromParent(key, entry); ok {
		if err = proto.Unmarshal(payload, reply); err == nil {
			caller.recordCacheHit(ctx, name)
			return result, nil
		}
		return Result{}, contract("cached reply decoding: %v", err)
	}
	result, err := caller.protoCall(ctx, name, request, reply)
	if err != nil {
		cache.complete(key, entry, nil, nil, Result{})
		return result, err
	}
	scope, ok := replyScope(reply)
	if !ok {
		cache.complete(key, entry, nil, nil, Result{})
		return result, nil
	}
	payload, err := proto.Marshal(reply)
	if err != nil {
		cache.complete(key, entry, nil, nil, Result{})
		return result, nil
	}
	cache.complete(key, entry, &scope, payload, result)
	return result, nil
}

// recordCacheHit leaves a native_cache_hit row so the phases sampler can
// show how many reads of each method the step cache absorbed.
func (caller *Client) recordCacheHit(ctx context.Context, name string) {
	if caller.recorder == nil {
		return
	}
	caller.recorder.Event("native_cache_hit", caller.snapshotRecordingContext(ctx), false, map[string]any{"tool": "games_call_tool", "native_tool": name})
}

// video and frame-acknowledge RPCs are active mutations (lease state, capture
// telemetry), not free reads, so they are reviewed only in protoCall's allowlist.

// protoCall is the closed transport seam for reviewed typed adapters. Adapters
// validate request semantics and apply their own read or explicit write capability.
func (caller *Client) protoCall(ctx context.Context, name string, request, reply proto.Message) (Result, error) {
	switch name {
	case "rimgovernor/observations_list_supplies", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_buildings", "rimgovernor/observations_list_rooms", "rimgovernor/observations_read_research", "rimgovernor/observations_list_wall_upgrade_sites", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_defense_site", "rimgovernor/observations_read_lines_of_fire", "rimgovernor/observations_read_spatial_access":
	case "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications", "rimgovernor/presentation_render_state", "rimgovernor/presentation_render_demand", "rimgovernor/presentation_capture_pawn", "rimgovernor/presentation_lease_video", "rimgovernor/presentation_read_frame", "rimgovernor/presentation_acknowledge_frame":
	case "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/operations_preview", "rimgovernor/observations_list_pawns", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress", "rimgovernor/authority_control", "rimgovernor/operations_release_owned_draft", "rimgovernor/operations_execute", "rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/clock_change_speed", "rimgovernor/clock_pause", "rimgovernor/observations_read_caravan_catalog", "rimgovernor/observations_read_world_progression", "rimgovernor/observations_read_world", "rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_read_production_policy", "rimgovernor/observations_read_population", "rimgovernor/observations_read_trade_sheet", "rimgovernor/observations_list_traders", "rimgovernor/observations_read_excavation_site", "rimgovernor/lifecycle_save", "rimgovernor/lifecycle_read_save", "rimgovernor/lifecycle_load", "rimgovernor/lifecycle_read_load", "rimgovernor/observations_read_bundle":
	default:
		return Result{}, contract("unreviewed native method")
	}
	// A write through a cached step context discards the step's memoized
	// observations, both before it is issued and once it has landed, so a
	// read in flight across the write is never served afterwards.
	cache := StepReadCacheFrom(ctx)
	if cache != nil && !nativeReadMethod(name) {
		cache.Invalidate()
		defer cache.Invalidate()
	}
	inner, err := protojson.Marshal(request)
	if err != nil {
		return Result{}, contract("request encoding: %v", err)
	}
	if len(inner) > maxProtoBytes {
		return Result{}, contract("oversized request")
	}
	invoked := false
	var recordCtx map[string]any
	var requestRow uint64
	result, err := caller.operation(ctx, func(ctx context.Context, live *liveSession) (Result, error) {
		// Nothing before games_call_tool reaches native: a failure here is
		// proof the write was never issued (domain.ErrWriteUnsent).
		if detail, err := caller.ensureDescribed(ctx, live, name); err != nil {
			return detail, fmt.Errorf("%w: %w", domain.ErrWriteUnsent, err)
		}
		invoked = true
		if caller.recorder != nil {
			recordCtx = caller.snapshotRecordingContext(ctx)
		}
		// The trace the call runs under (the caller's step or dispatch,
		// else the operation's own) rides beside the request; the
		// companion echoes it in its timing object, so its main-thread
		// phases join the same trace as the bridge's own (#298).
		args := encode(struct {
			Request string `json:"request"`
			Trace   string `json:"trace,omitempty"`
		}{string(inner), telemetry.TraceFrom(ctx).Wire()})
		result, err := caller.core(ctx, live, "games_call_tool", encode(nativeArgument{caller.gameID, name, args}))
		if timing := callTimingFrom(ctx); timing != nil {
			requestRow = timing.request
		}
		return result, err
	})
	callErr := err
	if err != nil && (!errors.Is(err, ErrRefused) || !invoked) {
		return result, err
	}
	decodeBegan := time.Now()
	payload, err := decodePayload(result.Structured, payloadLimit(name))
	if err != nil {
		if callErr != nil {
			return result, callErr
		}
		return result, err
	}
	err = (protojson.UnmarshalOptions{DiscardUnknown: false, RecursionLimit: 64}).Unmarshal(payload, reply)
	if caller.recorder != nil && invoked {
		// ProtoJSON decoding is the typed adapter's own cost, after the raw
		// receipt row; it is correlated to that row by request sequence.
		caller.recorder.Event("native_decode", recordCtx, false, map[string]any{"request": requestRow, "native_tool": name, "proto_decode_ms": millis(time.Since(decodeBegan)), "payload_bytes": len(payload), "ok": err == nil})
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
		case *pr.PawnImageReply:
			typedFailure = r.GetFailure() != nil
		case *pr.VideoReply:
			typedFailure = r.GetFailure() != nil
		case *pr.FrameReply:
			typedFailure = r.GetFailure() != nil
		case *pr.FrameAcknowledgementReply:
			typedFailure = r.GetRefusal() != nil
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
		case *o.ProductionPolicyReply:
			typedFailure = r.GetFailure() != nil
		case *p.PlacementReply:
			typedFailure = r.GetFailure() != nil
		case *a.StatusReply:
			typedFailure = r.GetFailure() != nil
		case *a.ControlReply:
			typedFailure = r.GetFailure() != nil
		case *op.PreviewReply:
			typedFailure = r.GetFailure() != nil
		case *op.ReleaseOwnedDraftReply:
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

func decodePayload(raw []byte, limit int) ([]byte, error) {
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
		if key != "payload" && key != "operation" && key != "timing" {
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
	if len(text.Value) > limit {
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

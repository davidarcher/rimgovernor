package bridge

import (
	"context"
	"errors"
	"math"
	"unicode/utf8"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

// Presentation reads expose observations only, never an input permission or capture.
func (client *Client) ReadCamera(ctx context.Context, request *p.ReadRequest) (*p.CameraReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("camera request required")
	}
	request = proto.Clone(request).(*p.ReadRequest)
	if err := presentationRequest(request, request.Identity); err != nil {
		return nil, Result{}, err
	}
	reply := &p.CameraReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/presentation_camera", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.CameraReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.CameraReply_Camera:
		err = validateCamera(v.Camera, request.Identity)
	default:
		err = contract("camera outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}
func (client *Client) ReadSelection(ctx context.Context, request *p.ReadRequest) (*p.SelectionReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("selection request required")
	}
	request = proto.Clone(request).(*p.ReadRequest)
	if err := presentationRequest(request, request.Identity); err != nil {
		return nil, Result{}, err
	}
	reply := &p.SelectionReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/presentation_selection", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.SelectionReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.SelectionReply_Selection:
		err = validateSelection(v.Selection, request.Identity)
	default:
		err = contract("selection outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}
func (client *Client) ReadColonistRoster(ctx context.Context, request *p.ColonistRosterRequest) (*p.ColonistRosterReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("roster request required")
	}
	request = proto.Clone(request).(*p.ColonistRosterRequest)
	if err := presentationRequest(request, request.Identity); err != nil {
		return nil, Result{}, err
	}
	if request.CurrentMapOnly == nil {
		return nil, Result{}, contract("currentMapOnly presence required")
	}
	reply := &p.ColonistRosterReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/presentation_colonists", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.ColonistRosterReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.ColonistRosterReply_Roster:
		err = validateRoster(v.Roster, request)
	default:
		err = contract("roster outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// RenderState is a zero-side-effect observation: it never extends or shortens
// the native rendering lease. DemandRendering (presentation_media.go) is the
// only RPC that changes lease state.
func (client *Client) ReadRenderState(ctx context.Context, request *p.ReadRequest) (*p.RenderReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("render state request required")
	}
	request = proto.Clone(request).(*p.ReadRequest)
	if err := presentationRequest(request, request.Identity); err != nil {
		return nil, Result{}, err
	}
	reply := &p.RenderReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/presentation_render_state", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *p.RenderReply_Failure:
		return reply, raw, failure(v.Failure, raw)
	case *p.RenderReply_Status:
		err = validateRenderStatus(v.Status, request.Identity)
	default:
		err = contract("render state outcome required")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}
func validateRenderStatus(v *p.RenderStatus, id *c.Identity) error {
	if v == nil {
		return contract("render status missing")
	}
	if err := presentationContext(v.Context, id); err != nil {
		return err
	}
	if v.Unavailable != nil {
		if err := validateUnavailable(v.Unavailable); err != nil {
			return err
		}
	}
	return nil
}
func presentationRequest(request proto.Message, id *c.Identity) error {
	if len(request.ProtoReflect().GetUnknown()) != 0 {
		return contract("unknown presentation request fields")
	}
	return ValidateIdentity(id)
}
func presentationContext(actual *c.ObservationContext, expected *c.Identity) error {
	if err := ValidateContext(actual); err != nil {
		return err
	}
	if !sameIdentity(actual.Identity, expected) {
		return contract("presentation context mismatch")
	}
	return nil
}
func presentationText(s *string, limit int) bool {
	return s == nil || (utf8.ValidString(*s) && len(*s) <= limit)
}
func presentationID(id *string, seen map[string]bool) error {
	if id == nil {
		return nil
	}
	if validID(*id) != nil || seen[*id] {
		return contract("invalid or duplicate presentation identifier")
	}
	seen[*id] = true
	return nil
}
func presentationCell(cell *c.Cell) error {
	if cell != nil && ((cell.X != nil && cell.GetX() < 0) || (cell.Z != nil && cell.GetZ() < 0)) {
		return contract("negative presentation cell")
	}
	return nil
}
func presentationListing(v *p.Listing, count int) error {
	if count > 4096 {
		return contract("presentation listing exceeds limit")
	}
	if v == nil {
		return nil
	}
	if v.ReturnedCount != nil && uint64(v.GetReturnedCount()) != uint64(count) {
		return contract("presentation returned count mismatch")
	}
	if v.TotalCount != nil && uint64(v.GetTotalCount()) < uint64(count) {
		return contract("presentation total count mismatch")
	}
	if v.GetComplete() && (v.GetTruncated() || (v.TotalCount != nil && uint64(v.GetTotalCount()) != uint64(count))) {
		return contract("inconsistent complete listing")
	}
	if v.GetTruncated() && v.TotalCount != nil && uint64(v.GetTotalCount()) <= uint64(count) {
		return contract("inconsistent truncated listing")
	}
	return nil
}
func validateCamera(v *p.CameraState, id *c.Identity) error {
	if v == nil {
		return contract("camera missing")
	}
	if err := presentationContext(v.Context, id); err != nil {
		return err
	}
	finite := func(value *float64, nonnegative bool) bool {
		return value == nil || (!math.IsNaN(*value) && !math.IsInf(*value, 0) && (!nonnegative || *value >= 0))
	}
	if v.MapPosition != nil && (!finite(v.MapPosition.X, false) || !finite(v.MapPosition.Z, false)) {
		return contract("invalid camera position")
	}
	for _, value := range []*float64{v.RootSize, v.ZoomRootSize, v.MinimumRootSize, v.MaximumRootSize} {
		if !finite(value, true) {
			return contract("invalid camera size")
		}
	}
	if v.MinimumRootSize != nil && v.MaximumRootSize != nil && v.GetMinimumRootSize() > v.GetMaximumRootSize() {
		return contract("invalid camera size bounds")
	}
	if !presentationText(v.NativeZoomRange, 256) {
		return contract("oversized camera zoom range")
	}
	if r := v.ViewRect; r != nil {
		if (r.MinX != nil && r.MaxX != nil && r.GetMinX() > r.GetMaxX()) || (r.MinZ != nil && r.MaxZ != nil && r.GetMinZ() > r.GetMaxZ()) {
			return contract("inverted camera view bounds")
		}
	}
	return nil
}
func validateSelection(v *p.SelectionSnapshot, id *c.Identity) error {
	if v == nil {
		return contract("selection missing")
	}
	if err := errors.Join(presentationContext(v.Context, id), presentationListing(v.Listing, len(v.SelectedObjects))); err != nil {
		return err
	}
	if !presentationText(v.Fingerprint, 4096) {
		return contract("oversized selection fingerprint")
	}
	seen := map[string]bool{}
	for _, item := range v.SelectedObjects {
		if item == nil {
			return contract("missing selected object")
		}
		if err := errors.Join(presentationID(item.Id, seen), presentationCell(item.Position)); err != nil {
			return err
		}
		if item.MapId != nil && item.GetMapId() < 0 {
			return contract("invalid selected map")
		}
		for _, text := range []*string{item.NativeKind, item.NativeType, item.DefName} {
			if !presentationText(text, 256) {
				return contract("oversized selected identifier")
			}
		}
		for _, text := range []*string{item.Label, item.InspectLabel, item.InspectText} {
			if !presentationText(text, 16384) {
				return contract("oversized selection text")
			}
		}
	}
	return nil
}
func validateRoster(v *p.ColonistRoster, q *p.ColonistRosterRequest) error {
	if v == nil {
		return contract("roster missing")
	}
	if err := errors.Join(presentationContext(v.Context, q.Identity), presentationListing(v.Listing, len(v.Colonists))); err != nil {
		return err
	}
	seen := map[string]bool{}
	for _, item := range v.Colonists {
		if item == nil {
			return contract("missing colonist")
		}
		if err := errors.Join(presentationID(item.PawnId, seen), presentationCell(item.Position)); err != nil {
			return err
		}
		if !presentationText(item.Name, 16384) {
			return contract("oversized colonist name")
		}
		if item.MapId != nil && (item.GetMapId() < 0 || (q.GetCurrentMapOnly() && item.GetMapId() != q.Identity.GetMapId())) {
			return contract("invalid roster map")
		}
		if err := validateDossier(item, q); err != nil {
			return err
		}
	}
	return nil
}

// A dossier is the observation PawnState for the same pawn, present exactly
// when the request asked for it; settings and animal detail never ride along.
func validateDossier(item *p.ColonistReference, q *p.ColonistRosterRequest) error {
	if item.Dossier == nil {
		if q.GetIncludeDossier() {
			return contract("missing colonist dossier")
		}
		return nil
	}
	if !q.GetIncludeDossier() {
		return contract("unrequested colonist dossier")
	}
	d := item.Dossier
	if d.GetPawn().GetId() == "" || d.GetPawn().GetId() != item.GetPawnId() {
		return contract("dossier pawn mismatch")
	}
	if d.Settings != nil || d.AnimalState != nil {
		return contract("dossier carries unrequested detail")
	}
	if !d.GetColonist() || d.GetDead() {
		return contract("dossier is not a live colonist")
	}
	if len(d.GetBiography().GetSkills()) > 256 || len(d.GetBiography().GetTraits()) > 256 || len(d.GetHealth().GetHediffs()) > 256 {
		return contract("oversized colonist dossier")
	}
	return nil
}

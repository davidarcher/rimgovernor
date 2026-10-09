package buildingruntime

import (
	"context"

	"google.golang.org/protobuf/reflect/protoreflect"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// combatOrdersFake applies every combat order and keeps each batch.
type combatOrdersFake struct {
	batches []*op.CombatOrders
	keys    []string          // each batch's idempotency key
	refuse  map[string]string // pawn -> refusal
	asks    []*mp.CombatGeometryRequest
	propose []domain.Cell // the game's covered cells behind the line
	// drafted is every pawn an applied draft order drafted; framed shows
	// them drafted in the next frame, as the game does.
	drafted map[string]bool
}

func (f *combatOrdersFake) CombatOrders(ctx context.Context, _ *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	f.keys = append(f.keys, key)
	if err := bridge.ValidateCombatOrders(command); err != nil {
		return nil, err
	}
	f.batches = append(f.batches, command)
	out := make([]bridge.CombatOrderResult, len(command.Orders))
	for i, order := range command.Orders {
		pawn := order.GetPawn().GetEntityId()
		out[i] = bridge.CombatOrderResult{Index: i, PawnID: pawn, Applied: true}
		if reason := f.refuse[pawn]; reason != "" {
			out[i].Applied, out[i].Refusal = false, reason
		} else if order.GetDraft() != nil {
			if f.drafted == nil {
				f.drafted = map[string]bool{}
			}
			f.drafted[pawn] = true
		}
	}
	return out, ctx.Err()
}

func (n *equipTestNative) CombatOrders(ctx context.Context, identity *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	return n.orders.CombatOrders(ctx, identity, key, command)
}

func (n *defenseReplayNative) CombatOrders(ctx context.Context, identity *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	return n.orders.CombatOrders(ctx, identity, key, command)
}

func (n *equipTestNative) combatDrafted() map[string]bool     { return n.orders.drafted }
func (n *defenseReplayNative) combatDrafted() map[string]bool { return n.orders.drafted }

// CombatGeometry validates the ask and proposes the fake's cells.
func (f *combatOrdersFake) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	f.asks = append(f.asks, request)
	if err := bridge.ValidateCombatGeometryRequest(request); err != nil {
		return nil, bridge.Result{}, err
	}
	g := &mp.CombatGeometry{}
	for _, cell := range f.propose {
		g.Proposed = append(g.Proposed, &mp.CombatGeometryCell{Cell: &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}, Standable: proto.Bool(true)})
	}
	return g, bridge.Result{}, ctx.Err()
}

func (n *equipTestNative) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	return n.orders.CombatGeometry(ctx, request)
}

func (n *defenseReplayNative) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	return n.orders.CombatGeometry(ctx, request)
}

// legacyDefense is a fake that answers the fight's inputs read by read
// (the census, the combat detail, the building lines of fire).
type legacyDefense interface {
	CombatOrders(context.Context, *c.Identity, string, *op.CombatOrders) ([]bridge.CombatOrderResult, error)
	CombatGeometry(context.Context, *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	ReadCombatPawns(context.Context, *c.Identity, []string) (*o.ListPawnsReply, bridge.Result, error)
	ReadLinesOfFire(context.Context, *c.Identity, []domain.Cell, []domain.Cell) (bridge.LinesOfFire, bridge.Result, error)
}

// framed serves a legacyDefense's replies as the one frame native
// captures, decoded as ReadCombat decodes it: the census, the detail
// rows of its colonists and the threats the fight answers, and the lines
// of fire from its ranged colonists to hostile buildings. It carries no
// combat pawns or events.
type framed struct{ legacyDefense }

// ReadDefenseSite serves an empty census: no burn-out fuel stands.
func (f framed) ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error) {
	return bridge.DefenseSite{}, bridge.Result{}, nil
}

// FrameThings and DefinitionCatalog pass the fake's tables through, as the
// live native serves them beside the combat frame.
func (f framed) FrameThings(ctx context.Context, identity *c.Identity) (bridge.Things, error) {
	return frameThings(ctx, f.legacyDefense, identity)
}

func (f framed) DefinitionCatalog(ctx context.Context, identity *c.Identity) (*bridge.DefinitionCatalog, error) {
	return pawnCatalog(ctx, f.legacyDefense, identity)
}

func (f framed) ReadCombat(ctx context.Context, identity *c.Identity) (bridge.Combat, error) {
	emergency, _, err := f.ReadEmergency(ctx, identity)
	if err != nil {
		return bridge.Combat{}, err
	}
	frame := &o.BundleSnapshot{Context: emergency.Context}
	frame.Emergency, frame.Pawns = emergencySnapshot(emergency.Context, emergency.Facts)
	if m, ok := f.legacyDefense.(interface{ combatMirror() []*mp.CombatPawn }); ok {
		frame.CombatPawns = m.combatMirror()
	}
	if m, ok := f.legacyDefense.(interface{ combatMortars() []*mp.CombatMortarRow }); ok {
		frame.CombatMortars = m.combatMortars()
	}
	hostiles, _, buildings := defenseTargets(emergency.Facts.Threats)
	var ids []string
	seen := map[string]bool{}
	for _, pawn := range emergency.Facts.Colonists {
		if !seen[string(pawn.ID)] {
			seen[string(pawn.ID)] = true
			ids = append(ids, string(pawn.ID))
		}
	}
	for _, id := range hostiles {
		if !seen[id] {
			seen[id] = true
			ids = append(ids, id)
		}
	}
	if len(ids) == 0 || len(emergency.Facts.Colonists) == 0 || len(hostiles)+len(buildings) == 0 && !hasAggressiveBreak(emergency.Facts) {
		return f.decode(ctx, identity, frame)
	}
	reply, _, err := f.ReadCombatPawns(ctx, identity, ids)
	if err != nil {
		return bridge.Combat{}, err
	}
	// Native captures the combat families into the pawn table rows,
	// scoped to the frame; the census facts stay the fake's.
	var detailRows []*o.PawnState
	if reply.GetObserved() != nil {
		detail := proto.Clone(reply.GetObserved()).(*o.PawnSnapshot)
		detail.Context = proto.Clone(emergency.Context).(*c.ObservationContext)
		drafted := map[string]bool{}
		if d, ok := f.legacyDefense.(interface{ combatDrafted() map[string]bool }); ok {
			drafted = d.combatDrafted()
		}
		for _, row := range detail.Pawns {
			row.Needs, row.Settings, row.Social, row.TendDoctor = nil, nil, nil, nil
			if drafted[row.GetPawn().GetId()] {
				row.Drafted = proto.Bool(true)
			}
		}
		scopeRefs(detail.ProtoReflect(), emergency.Context)
		detailRows = detail.Pawns
		for i, row := range frame.Pawns.Pawns {
			for _, rich := range detail.Pawns {
				if rich.GetPawn().GetId() == row.GetPawn().GetId() {
					merged := proto.Clone(rich).(*o.PawnState)
					proto.Merge(merged, row)
					frame.Pawns.Pawns[i] = merged
				}
			}
		}
	}
	var firing, approach []domain.Cell
	arms, err := readArmament(ctx, f.legacyDefense, identity)
	if err != nil {
		return bridge.Combat{}, err
	}
	for _, row := range detailRows {
		if !seen[row.GetPawn().GetId()] || row.Pawn.Position == nil {
			continue
		}
		if reach, err := arms.primaryRange(row.Equipment); err != nil {
			return bridge.Combat{}, err
		} else if reach <= 0 {
			continue
		}
		firing = append(firing, domain.Cell{X: row.Pawn.Position.GetX(), Z: row.Pawn.Position.GetZ()})
	}
	for _, b := range buildings {
		approach = append(approach, b.Cells...)
	}
	if len(firing) > 0 && len(approach) > 0 {
		lines, _, err := f.ReadLinesOfFire(ctx, identity, firing, approach)
		if err != nil {
			return bridge.Combat{}, err
		}
		snapshot := &o.LinesOfFireSnapshot{Context: emergency.Context}
		for _, line := range lines.Lines {
			row := &o.LineOfFire{From: &c.Cell{X: proto.Int32(line.From.X), Z: proto.Int32(line.From.Z)}, To: &c.Cell{X: proto.Int32(line.To.X), Z: proto.Int32(line.To.Z)}, Distance: proto.Float64(line.Distance)}
			if line.Known {
				row.LineOfSight, row.TargetCover, row.ShooterCover = proto.Bool(line.LineOfSight), proto.Float64(line.TargetCover), proto.Float64(line.ShooterCover)
			} else {
				row.Issues = []*o.ReadIssue{{Field: proto.String("line_of_sight"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			}
			snapshot.Lines = append(snapshot.Lines, row)
		}
		frame.CombatLinesOfFire = snapshot
	}
	return f.decode(ctx, identity, frame)
}

// decode is decodeCombatWithCatalog with the fake's frame things table, which
// the live read takes from the held frame.
func (f framed) decode(ctx context.Context, identity *c.Identity, frame *o.BundleSnapshot) (bridge.Combat, error) {
	var extra []bridge.FixtureDef
	if source, ok := f.legacyDefense.(interface{ recordedWeaponDefs() []bridge.FixtureDef }); ok {
		extra = source.recordedWeaponDefs()
	}
	// The cut frame carries the primary weapons' rows of the things table.
	things, err := frameThings(ctx, f.legacyDefense, identity)
	if err != nil {
		return bridge.Combat{}, err
	}
	frame.Things = &o.ThingsSnapshot{Context: frame.Context}
	for _, row := range frame.GetPawns().GetPawns() {
		if held, ok := things.Get(row.GetEquipment().GetPrimaryId()); ok {
			frame.Things.Things = append(frame.Things.Things, held)
		}
	}
	return decodeCombatWithCatalog(frame, extra...)
}

// scopeRefs scopes every CAS snapshot ref in m to the frame's context, as
// native captures them in one frame.
func scopeRefs(m protoreflect.Message, ctx *c.ObservationContext) {
	if ref, ok := m.Interface().(*o.SnapshotRef); ok {
		ref.Context = proto.Clone(ctx).(*c.ObservationContext)
		return
	}
	m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
		if fd.Message() == nil || fd.IsMap() {
			return true
		}
		if fd.IsList() {
			for i := 0; i < v.List().Len(); i++ {
				scopeRefs(v.List().Get(i).Message(), ctx)
			}
		} else {
			scopeRefs(v.Message(), ctx)
		}
		return true
	})
}

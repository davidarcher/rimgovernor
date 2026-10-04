package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// equipTestNative is a colony of unarmed colonists beside one loose bow.
type equipTestNative struct {
	*roundsNative
	ids      []string
	weapons  []bridge.EquipCandidate
	editPawn func(*o.PawnState)
	// filtered is the pawns the map holds that an exact-ID census excludes:
	// animals, visitors and prisoners a caller never asked for.
	filtered uint64
	orders   combatOrdersFake
}

func (n *equipTestNative) pawn(id string) *o.PawnState {
	v := n.reply.GetObserved()
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId()), Position: proto.Clone(v.Center).(*c.Cell)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
}
func (n *equipTestNative) census() *o.ListPawnsReply {
	v := n.reply.GetObserved()
	var rows []*o.PawnState
	for _, id := range n.ids {
		row := n.pawn(id)
		if n.editPawn != nil {
			n.editPawn(row)
		}
		rows = append(rows, row)
	}
	return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: rows, Completeness: &o.Completeness{Filtered: proto.Uint64(n.filtered)}}}}
}
func (n *equipTestNative) ReadRoundsPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.census(), bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadCombatPawns(ctx context.Context, _ *c.Identity, _ []string) (*o.ListPawnsReply, bridge.Result, error) {
	return n.census(), bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadLinesOfFire(ctx context.Context, _ *c.Identity, _, _ []domain.Cell) (bridge.LinesOfFire, bridge.Result, error) {
	return bridge.LinesOfFire{Context: n.reply.GetObserved().GetContext()}, bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	v, r, err := n.roundsNative.ReadEmergency(ctx, id)
	for _, id := range n.ids {
		v.Facts.Colonists = append(v.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)})
	}
	return v, r, err
}
func (n *equipTestNative) ReadMapBounds(ctx context.Context, _ *c.Identity, _ domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	return bridge.MapBounds{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Bounds: policy.Bounds{Width: 250, Height: 250}}, bridge.Result{}, ctx.Err()
}
func (n *equipTestNative) ReadEquipWeapons(ctx context.Context, _ *c.Identity, _, _ domain.Cell) (bridge.EquipRead, bridge.Result, error) {
	if n.weapons != nil {
		return bridge.EquipRead{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.weapons}, bridge.Result{}, ctx.Err()
	}
	return bridge.EquipRead{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: []bridge.EquipCandidate{{Thing: "bow", Definition: "Bow_Short", Cell: domain.Cell{X: 5, Z: 5}, Token: "bow-token"}}}, bridge.Result{}, ctx.Err()
}

func TestEquipPlannerBiocodeOwnerOnly(t *testing.T) {
	for _, tc := range []struct {
		name  string
		owner domain.PawnID
		coded bool
		want  domain.PawnID
	}{
		{"uncoded", "", false, "a"},
		{"owner", "b", true, "b"},
		{"absent owner", "outside", true, ""},
		{"lost owner", "", true, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			ctx := context.Background()
			reviewer, db, _, _, native := roundsFixture(t)
			n := &equipTestNative{roundsNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{{Thing: "rifle", Definition: "Gun_BoltActionRifle", BiocodedTo: tc.owner, Biocoded: tc.coded}}}
			v := native.reply.GetObserved()
			v.ColonistCount = proto.Uint32(uint32(len(n.ids)))
			v.WorkerCount = proto.Uint32(uint32(len(n.ids)))
			v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
			reviewer.native = n
			reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureBasicDefense})
			if _, err := reviewer.Step(ctx); err != nil {
				t.Fatal(err)
			}
			planner, err := NewRoundsEquipPlanner(reviewer, n)
			if err != nil {
				t.Fatal(err)
			}
			result, err := planner.Step(ctx)
			if err != nil {
				t.Fatal(err)
			}
			if tc.want == "" {
				if !result.Verdict.Is(WaitMethodUsed) {
					t.Fatal(result)
				}
				return
			}
			if result.Verdict != BuildingReasonAdmitted {
				t.Fatal(result)
			}
			plan, err := db.LoadPlan(ctx, result.Plan)
			if err != nil || len(plan.Spec.Actions()) != 1 {
				t.Fatal(plan, err)
			}
			equip, ok := plan.Spec.Actions()[0].Equip()
			if !ok || equip.Pawn() != tc.want || equip.Thing() != "rifle" {
				t.Fatal(equip)
			}
		})
	}
}

func TestEquipPlannerPreservesCompletedBiocodedPrimary(t *testing.T) {
	for _, tc := range []struct {
		name  string
		coded bool
		owner string
	}{
		{"uncoded upgrades", false, ""}, {"coded stays", true, "a"}, {"lost owner stays", true, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			ctx := context.Background()
			reviewer, db, _, _, native := roundsFixture(t)
			n := &equipTestNative{roundsNative: native, ids: []string{"a"}, weapons: []bridge.EquipCandidate{{Thing: "log", Definition: "WoodLog"}}}
			v := native.reply.GetObserved()
			v.ColonistCount = proto.Uint32(uint32(len(n.ids)))
			v.WorkerCount = proto.Uint32(uint32(len(n.ids)))
			v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
			reviewer.native = n
			reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureBasicDefense})
			if _, err := reviewer.Step(ctx); err != nil {
				t.Fatal(err)
			}
			planner, err := NewRoundsEquipPlanner(reviewer, n)
			if err != nil {
				t.Fatal(err)
			}
			result, err := planner.Step(ctx)
			if err != nil || result.Verdict != BuildingReasonAdmitted {
				t.Fatal(result, err)
			}
			plan, err := db.LoadPlan(ctx, result.Plan)
			if err != nil {
				t.Fatal(err)
			}
			action := plan.Spec.Actions()[0]
			snapshot := reviewer.player.State().Snapshot
			snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
			tick := domain.Tick(native.reply.GetObserved().Context.GetTick())
			if _, err := db.Prepare(ctx, result.Plan, action.ID(), snapshot, tick); err != nil {
				t.Fatal(err)
			}
			if _, err := db.Dispatch(ctx, result.Plan, action.ID(), snapshot, tick); err != nil {
				t.Fatal(err)
			}
			if _, err := db.RecordReceipt(ctx, result.Plan, action.ID(), 1, domain.ReceiptAccepted); err != nil {
				t.Fatal(err)
			}
			n.editPawn = func(row *o.PawnState) {
				item := &o.GearItem{Thing: native.entity(&o.EntityRef{Id: proto.String("log"), DefName: proto.String("WoodLog")}), Biocoded: proto.Bool(tc.coded)}
				if tc.owner != "" {
					item.BiocodedTo = proto.String(tc.owner)
				}
				row.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String("log"), Equipped: []*o.GearItem{item}}
			}
			n.weapons = []bridge.EquipCandidate{{Thing: "rifle", Definition: "Gun_AssaultRifle"}}
			next, err := planner.Step(ctx)
			if err != nil {
				t.Fatal(next, err)
			}
			if tc.coded && !next.Verdict.Is(WaitMethodUsed) || !tc.coded && next.Verdict != BuildingReasonAdmitted {
				t.Fatal(next)
			}
		})
	}
}

func TestEquipPawnRaidArmorPresence(t *testing.T) {
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("pawn")}}
	if _, known := mustEquipFacts(t, row).RaidArmor.Value(); known {
		t.Fatal("missing armor became known")
	}
	row.RaidArmor = proto.Float64(0)
	if armor, known := mustEquipFacts(t, row).RaidArmor.Value(); !known || armor != 0 {
		t.Fatal("zero armor lost")
	}
	row.RaidArmor = proto.Float64(.8)
	if armor, known := mustEquipFacts(t, row).RaidArmor.Value(); !known || armor != .8 {
		t.Fatal("armor lost")
	}
	row.Issues = []*o.ReadIssue{{Field: proto.String("raid_armor")}}
	if _, known := mustEquipFacts(t, row).RaidArmor.Value(); known {
		t.Fatal("unavailable armor became known")
	}
}

func TestEquipPlannerOneWave(t *testing.T) {
	ctx := context.Background()
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(3)
	v.WorkerCount = proto.Uint32(3)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	n := &equipTestNative{roundsNative: native, ids: []string{"a", "b", "c"}, weapons: []bridge.EquipCandidate{
		{Thing: "bow1", Definition: "Bow_Short"},
		{Thing: "bow2", Definition: "Bow_Short"},
		{Thing: "bow3", Definition: "Bow_Short"},
	}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureBasicDefense})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsEquipPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Spec.Actions()) != 3 || len(plan.Spec.Dependencies()) != 0 {
		t.Fatal("expected three independent orders", plan.Spec)
	}
	pawns, weapons := map[domain.PawnID]bool{}, map[string]bool{}
	for _, action := range plan.Spec.Actions() {
		equip, ok := action.Equip()
		if !ok || pawns[equip.Pawn()] || weapons[equip.Thing()] {
			t.Fatal(action)
		}
		pawns[equip.Pawn()] = true
		weapons[equip.Thing()] = true
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal("duplicated open wave", next, err)
	}
}

// The live run (#1674) retired the first wave's plan; the goal then listed no
// methods, so the next wave reused "equip-wave-0" and the store refused it on
// every step while colonists stayed disarmed.
func TestNextEquipWaveMethodSkipsRetiredWaves(t *testing.T) {
	var goal store.StandardState
	if got := nextEquipWaveMethod(goal); got != "equip-wave-0" {
		t.Fatal(got)
	}
	goal.History = []domain.Method{{Method: "equip-wave-0"}, {Method: "equip-wave-1"}}
	if got := nextEquipWaveMethod(goal); got != "equip-wave-2" {
		t.Fatal(got)
	}
	goal.Methods = []domain.Method{{Method: "equip-wave-2"}}
	if got := nextEquipWaveMethod(goal); got != "equip-wave-3" {
		t.Fatal(got)
	}
}

// A pawn another planner already claimed this step must not starve the
// other unarmed colonists: colony-2 ended with every survivor unarmed
// beside loose bows because SelectEquip always named the same pawn.
func TestEquipPlannerSkipsClaimedPawn(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, _, native := roundsFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	n := &equipTestNative{roundsNative: native, ids: []string{"a", "b"}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureBasicDefense})
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureBasicDefense {
			continue
		}
		g, err := db.LoadStandard(ctx, binding.Goal)
		if err != nil || g.Standard.Finding != domain.FindingUnmet {
			t.Fatal("EnsureBasicDefense is not in deficit", g, err)
		}
		found = true
	}
	if !found {
		t.Fatal("EnsureBasicDefense missing from the review", review.Goals)
	}
	planner, err := NewRoundsEquipPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := reviewer.player.enter(ctx, "test", false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	arbiter := newStepArbiter()
	if !arbiter.tryClaim([]domain.PawnID{"a"}) {
		t.Fatal("fresh arbiter refused a claim")
	}
	result, err := planner.step(call, epoch, arbiter)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	equip, ok := plan.Spec.Actions()[0].Equip()
	if !ok || equip.Pawn() != "b" {
		t.Fatal("expected the unclaimed pawn b to equip", equip)
	}
}

func (n *equipTestNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}

// mustEquipFacts is equipCandidatePawnFacts over a row whose traits need no catalog.
func mustEquipFacts(t *testing.T, row *o.PawnState) policy.EquipCandidatePawn {
	t.Helper()
	facts, err := equipCandidatePawnFacts(row, nil, bridge.Things{})
	if err != nil {
		t.Fatal(err)
	}
	return facts
}

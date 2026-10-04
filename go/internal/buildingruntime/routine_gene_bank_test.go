package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type geneBankDispatch struct {
	planner  *RoutineGeneBankPlanner
	call     context.Context
	epoch    context.Context
	db       *store.Store
	sleeping *sleepingNative
}

// geneBankFixture is a colony with the given gene-building rows and a
// catalog whose only gene bank carries the genepack container comp under a
// name no Go code knows.
func geneBankFixture(t *testing.T, facts *o.BiotechColonyFacts) geneBankDispatch {
	t.Helper()
	bank := buildable("Vault_Test", 0, 1, 2)
	bank.GeneBank = true
	base, db, sleeping, source, _ := biotechPlannerFixture(t, policy.MaintainGeneBank, facts, bank, false)
	planner, err := NewRoutineGeneBankPlanner(base.reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := base.reviewer.player.enter(context.Background(), "test", false)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(done)
	for i := range sleeping.cells.Cells {
		sleeping.cells.Cells[i].SupportsLight = domain.Known(true)
	}
	return geneBankDispatch{planner: planner, call: call, epoch: epoch, db: db, sleeping: sleeping}
}

func cellAt(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }

func loosePack(id string) *o.GenepackState {
	return &o.GenepackState{ThingId: proto.String(id), DefName: proto.String("Genepack"), Position: cellAt(6, 6), Deteriorating: proto.Bool(true)}
}

func bankedPack(id, bank string) *o.GenepackState {
	return &o.GenepackState{ThingId: proto.String(id), DefName: proto.String("Genepack"), BankId: proto.String(bank), Deteriorating: proto.Bool(false)}
}

func standingBank(id string, packs ...string) *o.GeneBankState {
	return &o.GeneBankState{ThingId: proto.String(id), DefName: proto.String("Vault_Test"), Position: cellAt(4, 4), Powered: proto.Bool(true), Capacity: proto.Int32(4), PackIds: packs}
}

// A pack lying loose with no bank owes one: the planner admits a bank
// through the ordinary building path at the catalog-found definition and
// stages it only once; powering it is the power goal's.
func TestGeneBankPlannerAdmitsOneBankForALoosePack(t *testing.T) {
	d := geneBankFixture(t, &o.BiotechColonyFacts{Genepacks: []*o.GenepackState{loosePack("pack")}})
	result, err := d.planner.step(d.call, d.epoch, nil)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := d.db.LoadPlan(context.Background(), result.Decision.Goal.Methods[len(result.Decision.Goal.Methods)-1].Plan)
	if err != nil {
		t.Fatal(err)
	}
	building, ok := plan.Spec.Actions()[0].Building()
	if !ok || building.Definition() != "Vault_Test" || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan.Spec.Actions())
	}
	if plan.Progress[0].View().Stage != domain.Pending {
		t.Fatal("placement bypassed Hands")
	}
	if next, err := d.planner.step(d.call, d.epoch, nil); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal("a bank being built is not staged again", next, err)
	}
}

// With every bank full a further loose pack still owes a bank; a bank with a
// free slot owes none.
func TestGeneBankPlannerStagesNothingUnlessOwed(t *testing.T) {
	for name, d := range map[string]geneBankDispatch{
		"no packs":     geneBankFixture(t, &o.BiotechColonyFacts{}),
		"room in bank": geneBankFixture(t, &o.BiotechColonyFacts{GeneBanks: []*o.GeneBankState{standingBank("bank", "a")}, Genepacks: []*o.GenepackState{bankedPack("a", "bank"), loosePack("b")}}),
		"all banked":   geneBankFixture(t, &o.BiotechColonyFacts{GeneBanks: []*o.GeneBankState{standingBank("bank", "a")}, Genepacks: []*o.GenepackState{bankedPack("a", "bank")}}),
	} {
		result, err := d.planner.step(d.call, d.epoch, nil)
		if err != nil || result.Verdict == BuildingReasonAdmitted || d.sleeping.previews != 0 {
			t.Errorf("%s: %v %v previews=%d", name, result, err, d.sleeping.previews)
		}
	}
	full := geneBankFixture(t, &o.BiotechColonyFacts{GeneBanks: []*o.GeneBankState{standingBank("bank", "a", "b", "c", "d")}, Genepacks: []*o.GenepackState{bankedPack("a", "bank"), bankedPack("b", "bank"), bankedPack("c", "bank"), bankedPack("d", "bank"), loosePack("e")}})
	if result, err := full.planner.step(full.call, full.epoch, nil); err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal("a full bank owes another", result, err)
	}
}

// A refused preview moves on to the next footprint; with none accepted
// nothing is admitted.
func TestGeneBankPlannerSkipsRefusedFootprintsAndReportsNoSpace(t *testing.T) {
	d := geneBankFixture(t, &o.BiotechColonyFacts{Genepacks: []*o.GenepackState{loosePack("pack")}})
	refuse := 0
	d.sleeping.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		refuse++
		p.Preview.WatchCellsAccessible = domain.Known(true)
		p.Preview.CanPlace = domain.Known(false)
	}
	result, err := d.planner.step(d.call, d.epoch, nil)
	if err != nil || result.Verdict != noSpace("gene_bank_cell") || refuse < 2 {
		t.Fatal(result, err, refuse)
	}
}

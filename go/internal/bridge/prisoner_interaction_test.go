package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func populationReply(persons ...*o.PopulationPerson) *o.PopulationReply {
	return &o.PopulationReply{Outcome: &o.PopulationReply_Observed{Observed: &o.PopulationSnapshot{
		Context:      &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)},
		Persons:      persons,
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
	}}}
}

func prisonerPerson(id, interaction string) *o.PopulationPerson {
	person := &o.PopulationPerson{Pawn: &o.PawnState{
		Pawn: &o.EntityRef{Id: proto.String(id)}, Snapshot: &o.SnapshotRef{Token: proto.String("tok-" + id)},
		Dead: proto.Bool(false), Prisoner: proto.Bool(true),
	}, Recruitable: proto.Bool(true)}
	if interaction != "" {
		person.Interaction = proto.String(interaction)
	}
	return person
}

// Every mode SetPrisonerInteraction writes reads back as a known current
// interaction; any other native defName (Execution, unexposed DLC modes)
// stays unknown instead of being misread as one of them.
func TestPrisonerInteractionReadsEveryExposedMode(t *testing.T) {
	names := map[string]PrisonerInteractionMode{
		"AttemptRecruit": PrisonerInteractionRecruit, "MaintainOnly": PrisonerInteractionMaintain,
		"ReduceResistance": PrisonerInteractionReduceResistance, "Release": PrisonerInteractionRelease,
		"Enslave": PrisonerInteractionEnslave, "Convert": PrisonerInteractionConvert,
	}
	persons := []*o.PopulationPerson{prisonerPerson("p-Execution", "Execution"), prisonerPerson("p-none", "")}
	persons[0].Resistance, persons[0].PrisonerTicks = proto.Float64(12.5), proto.Int64(180000)
	for name := range names {
		persons = append(persons, prisonerPerson("p-"+name, name))
	}
	reply := populationReply(persons...)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_population" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	for name, want := range names {
		target, _, err := client.ReadPrisonerInteractionTarget(context.Background(), pbIdentity(), "p-"+name)
		if err != nil || !target.CurrentInteractionKnown || target.CurrentInteraction != want {
			t.Fatal(name, target, err)
		}
	}
	for _, unknown := range []string{"p-Execution", "p-none"} {
		target, _, err := client.ReadPrisonerInteractionTarget(context.Background(), pbIdentity(), unknown)
		if err != nil || target.CurrentInteractionKnown {
			t.Fatal(unknown, target, err)
		}
	}
	census, _, err := client.ReadRoutinePopulation(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := census.Prisoners.Value()
	seen := map[domain.PawnID]domain.Fact[domain.PrisonerInteractionMode]{}
	for _, row := range rows {
		seen[row.Pawn] = row.CurrentInteraction
	}
	if _, known := seen["p-Execution"].Value(); known || len(seen) != 8 {
		t.Fatal(seen)
	}
	if mode, known := seen["p-Release"].Value(); !known || mode != domain.PrisonerInteractionRelease {
		t.Fatal(seen["p-Release"])
	}
	if mode, known := seen["p-Convert"].Value(); !known || mode != domain.PrisonerInteractionConvert {
		t.Fatal(seen["p-Convert"])
	}
	// The release path's facts decode only when native carried them.
	for _, row := range rows {
		resistance, rk := row.Resistance.Value()
		held, hk := row.HeldTicks.Value()
		if row.Pawn == "p-Execution" {
			if !rk || resistance != 12.5 || !hk || held != 180000 {
				t.Fatal(row)
			}
		} else if rk || hk {
			t.Fatal(row)
		}
	}
}

func TestPrisonerInteractionWireCoversEveryMode(t *testing.T) {
	for mode, want := range map[PrisonerInteractionMode]op.PrisonerInteraction{
		PrisonerInteractionRecruit:          op.PrisonerInteraction_PRISONER_INTERACTION_ATTEMPT_RECRUIT,
		PrisonerInteractionMaintain:         op.PrisonerInteraction_PRISONER_INTERACTION_MAINTAIN_ONLY,
		PrisonerInteractionReduceResistance: op.PrisonerInteraction_PRISONER_INTERACTION_REDUCE_RESISTANCE,
		PrisonerInteractionRelease:          op.PrisonerInteraction_PRISONER_INTERACTION_RELEASE,
		PrisonerInteractionEnslave:          op.PrisonerInteraction_PRISONER_INTERACTION_ENSLAVE,
		PrisonerInteractionConvert:          op.PrisonerInteraction_PRISONER_INTERACTION_CONVERT,
	} {
		if got := prisonerInteractionOperation("pawn", "tok", mode).GetSetPrisonerInteraction().GetInteraction(); got != want {
			t.Fatal(mode, got)
		}
		if err := prisonerInteractionCommand("pawn", "tok", mode); err != nil {
			t.Fatal(mode, err)
		}
	}
	if err := prisonerInteractionCommand("pawn", "tok", PrisonerInteractionModeUnspecified); err == nil {
		t.Fatal("unspecified mode accepted")
	}
	if err := prisonerInteractionCommand("pawn", "tok", PrisonerInteractionConvert+1); err == nil {
		t.Fatal("out-of-range mode accepted")
	}
}

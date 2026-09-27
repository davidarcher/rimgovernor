package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func populationReply(persons ...*o.PopulationPerson) *o.PopulationReply {
	return &o.PopulationReply{Outcome: &o.PopulationReply_Observed{Observed: &o.PopulationSnapshot{
		Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(7), NativeGeneration: proto.Uint64(1)},
		Persons: persons,
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

// Every mode PrisonerInteractionIntent writes reads back as a known current
// interaction; any other native defName (Execution, unexposed DLC modes)
// stays unknown instead of being misread as one of them.
func TestPrisonerInteractionReadsEveryExposedMode(t *testing.T) {
	names := map[string]domain.PrisonerInteractionMode{
		"AttemptRecruit": domain.PrisonerInteractionRecruit, "MaintainOnly": domain.PrisonerInteractionMaintain,
		"ReduceResistance": domain.PrisonerInteractionReduceResistance, "Release": domain.PrisonerInteractionRelease,
		"Enslave": domain.PrisonerInteractionEnslave, "Convert": domain.PrisonerInteractionConvert,
	}
	persons := []*o.PopulationPerson{prisonerPerson("p-Execution", "Execution"), prisonerPerson("p-none", "")}
	persons[0].Resistance, persons[0].PrisonerTicks = proto.Float64(12.5), proto.Int64(180000)
	for name := range names {
		persons = append(persons, prisonerPerson("p-"+name, name))
	}
	reply := populationReply(persons...)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_read_population" {
			t.Fatal(arg.Tool)
		}
		return pbResult(reply), nil
	}}, time.Second)
	census, _, err := client.ReadRoutinePopulation(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := census.Prisoners.Value()
	seen := map[domain.PawnID]domain.Fact[domain.PrisonerInteractionMode]{}
	for _, row := range rows {
		seen[row.Pawn] = row.CurrentInteraction
	}
	if len(seen) != 8 {
		t.Fatal(seen)
	}
	for _, unknown := range []domain.PawnID{"p-Execution", "p-none"} {
		if _, known := seen[unknown].Value(); known {
			t.Fatal(unknown, seen[unknown])
		}
	}
	for name, want := range names {
		if mode, known := seen[domain.PawnID("p-"+name)].Value(); !known || mode != want {
			t.Fatal(name, mode)
		}
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

func TestPrisonerInteractionIntentCoversEveryMode(t *testing.T) {
	for mode, want := range map[domain.PrisonerInteractionMode]op.PrisonerInteraction{
		domain.PrisonerInteractionRecruit:          op.PrisonerInteraction_PRISONER_INTERACTION_ATTEMPT_RECRUIT,
		domain.PrisonerInteractionMaintain:         op.PrisonerInteraction_PRISONER_INTERACTION_MAINTAIN_ONLY,
		domain.PrisonerInteractionReduceResistance: op.PrisonerInteraction_PRISONER_INTERACTION_REDUCE_RESISTANCE,
		domain.PrisonerInteractionRelease:          op.PrisonerInteraction_PRISONER_INTERACTION_RELEASE,
		domain.PrisonerInteractionEnslave:          op.PrisonerInteraction_PRISONER_INTERACTION_ENSLAVE,
		domain.PrisonerInteractionConvert:          op.PrisonerInteraction_PRISONER_INTERACTION_CONVERT,
	} {
		interaction, err := domain.NewPrisonerInteraction("pawn", mode)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewPrisonerInteractionAction("a", interaction)
		if err != nil {
			t.Fatal(err)
		}
		built, err := prisonerInteractionAction(action)
		if err != nil || built.GetPrisoner().GetPawnId() != "pawn" || built.GetPrisoner().GetInteraction() != want {
			t.Fatal(mode, built, err)
		}
	}
}

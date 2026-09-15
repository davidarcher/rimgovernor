package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/interpreter"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// ChatCatalogDefinitions is the fixed buildable-definition catalog chat's
// fact gatherer asks native for. ReadColonyFacts fails its entire read if
// asked for a defName native does not recognize (see bridge/colony.go), so
// this deliberately stays a short, guaranteed-vanilla list rather than a
// broad guess. Chat currently has no build command anyway (see
// GatherChatFacts doc): this list only satisfies interpreter.validateInput's
// unconditional non-empty Definitions/Cells requirement for every command,
// it never bounds what chat may place.
var ChatCatalogDefinitions = []string{"Wall", "Door"}

// ChatFactsNative narrows *bridge.Client to exactly the three reads chat's
// fact gatherer needs.
type ChatFactsNative interface {
	ReadColonyFacts(ctx context.Context, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error)
	ReadHomeColonists(ctx context.Context, identity *c.Identity) (*o.ListPawnsReply, bridge.Result, error)
	ReadResearch(ctx context.Context, identity *c.Identity) (bridge.ResearchRead, bridge.Result, error)
}

// GatherChatFacts assembles the bounded interpreter.Snapshot backing chat's
// currently supported command families -- research, tend, rescue, draft and
// husbandry (slaughter/train) -- plus the corresponding domain.GenerationSnapshot
// the interpreter call must be issued against. It does not chain reads
// through the boundary CAS machinery: unlike an action boundary's Inspect,
// this is a read-only fact gather with no prior admitted snapshot to check
// reads against, and every command it supports re-establishes its own
// admission at dispatch (store.SubmitTend et al. commit a fresh one-action
// plan directly). The returned Generation carries the real native generation
// ReadColonyFacts observed, but session/plan identity is a fixed placeholder:
// nothing downstream keys chat's one-shot commands off plan continuity, only
// off Snapshot.Generation matching Current for interpreter.Interpret's own
// staleness check (see interpreter.validateInput), which this function
// guarantees by construction.
//
// world identifies the colony/load/map the caller already resolved from the
// player's supplied identity; only its Colony/Load/Map fields are read.
func GatherChatFacts(ctx context.Context, native ChatFactsNative, world domain.GenerationSnapshot) (interpreter.Snapshot, domain.GenerationSnapshot, error) {
	if native == nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, errors.New("chat facts native reader required")
	}
	identity := &c.Identity{ColonyId: proto.String(string(world.Colony)), LoadToken: proto.String(string(world.Load)), MapId: proto.Int32(int32(world.Map))}
	colony, _, err := native.ReadColonyFacts(ctx, identity, true, ChatCatalogDefinitions)
	if err != nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, err
	}
	observed := colony.GetObserved()
	if observed == nil || observed.Context == nil || observed.Context.NativeGeneration == nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, errors.New("colony facts read returned no usable context")
	}
	current := domain.GenerationSnapshot{Colony: world.Colony, Load: world.Load, Map: world.Map, Plan: "chat", Revision: 1, Native: domain.NativeGeneration(observed.Context.GetNativeGeneration())}
	if err = current.Validate(); err != nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, err
	}
	snapshot := interpreter.Snapshot{Generation: current, Width: int32(observed.MapSize.GetWidth()), Height: int32(observed.MapSize.GetHeight())}
	planning := observed.GetPlanning().GetObserved()
	if planning == nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, errors.New("colony facts read did not return planning facts")
	}
	for _, row := range planning.Definitions {
		if row == nil || row.Definition == nil {
			continue
		}
		d := interpreter.Definition{DefName: row.Definition.GetDefName()}
		if row.Stuff != nil {
			d.Stuff = []string{row.GetStuff()}
		} else {
			d.AllowDefaultStuff = true
		}
		snapshot.Definitions = append(snapshot.Definitions, d)
	}
	for _, row := range planning.Cells.GetCells() {
		if row == nil || row.Cell == nil || row.Fogged == nil || row.GetFogged() {
			continue
		}
		snapshot.Cells = append(snapshot.Cells, domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()})
	}
	if len(snapshot.Definitions) == 0 || len(snapshot.Cells) == 0 {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, errors.New("colony facts read observed no usable definitions or cells")
	}

	roster, _, err := native.ReadHomeColonists(ctx, identity)
	if err != nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, err
	}
	rosterObserved := roster.GetObserved()
	if rosterObserved == nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, errors.New("home colonist read returned no usable roster")
	}
	for _, row := range rosterObserved.Pawns {
		if row == nil || row.Pawn == nil {
			continue
		}
		snapshot.Pawns = append(snapshot.Pawns, domain.PawnID(row.Pawn.GetId()))
	}

	research, _, err := native.ReadResearch(ctx, identity)
	if err != nil {
		return interpreter.Snapshot{}, domain.GenerationSnapshot{}, err
	}
	finished := map[string]bool{}
	for _, name := range research.Finished {
		finished[name] = true
	}
	for name, facts := range research.Projects {
		if name == research.CurrentProject || finished[name] {
			continue
		}
		prereqs, known := facts.Prerequisites.Value()
		if !known {
			continue
		}
		ready := true
		for _, prereq := range prereqs {
			if !finished[string(prereq)] {
				ready = false
				break
			}
		}
		if ready {
			snapshot.ResearchProjects = append(snapshot.ResearchProjects, name)
		}
	}
	return snapshot, current, nil
}

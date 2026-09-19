package bridge

import (
	"context"
	"errors"
	"fmt"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// GearReplaceRead is the fresh pawn/item/outfit CAS evidence
// InspectGearReplace needs immediately before preview: the same three tokens
// gear_upkeep.compile_upkeep's home/gear_upkeep call carries (pawn, target,
// expectedLoadout).
type GearReplaceRead struct {
	Context      *c.ObservationContext
	PawnToken    string
	ThingToken   string
	Definition   string
	LoadoutToken string
}

// ErrGearCandidateAbsent reports a fresh gear census that lists the pawn
// with a loadout token but no longer offers the exact candidate as apparel:
// it was worn, hauled away, forbidden, outscored by better finds, or it is a
// weapon the wear operation cannot target (#339). The proposal can never
// succeed, so the executor cancels it instead of holding the plan.
var ErrGearCandidateAbsent = errors.New("gear candidate absent")

// ReadGearReplacement reuses the generic colony census (the same read
// PreviewBill and ReadBillTarget drive) to find one already-selected pawn's
// gear loadout token and one already-selected replacement candidate's item
// token. It returns ErrUnavailable when the pawn or candidate is not present
// in the current census, mirroring ReadBillTarget's bench lookup.
func (client *Client) ReadGearReplacement(ctx context.Context, identity *c.Identity, pawn, thing string) (GearReplaceRead, Result, error) {
	reply, raw, err := client.ReadColonyFacts(ctx, identity, true, nil)
	if err != nil {
		return GearReplaceRead{}, raw, err
	}
	observed := reply.GetObserved()
	gear := observed.GetPlanning().GetObserved().GetGear()
	var pawnToken, loadoutToken, thingToken, definition string
	found := false
	// The unavailable error names what the census held so a wear order
	// that keeps failing its prepare is diagnosable from the worker log
	// (the pawn's blocker, or the candidate the census no longer offers).
	var blocker string
	candidates := 0
	for _, row := range gear.GetPawns() {
		if row.GetPawn().GetId() != pawn {
			continue
		}
		if found {
			return GearReplaceRead{}, raw, contract("duplicate gear pawn")
		}
		found = true
		blocker = row.GetBlocker()
		candidates = len(row.GetCandidates())
		pawnToken = row.GetPawn().GetSnapshot().GetToken()
		loadoutToken = row.GetSnapshot().GetToken()
		for _, candidate := range row.GetCandidates() {
			item := candidate.GetItem()
			if item.GetThing().GetId() != thing || !item.GetApparel() {
				continue
			}
			if thingToken != "" {
				return GearReplaceRead{}, raw, contract("duplicate gear candidate")
			}
			thingToken = item.GetThing().GetSnapshot().GetToken()
			definition = item.GetThing().GetDefName()
		}
	}
	if !found {
		return GearReplaceRead{}, raw, fmt.Errorf("%w: pawn %s absent from the gear census of %d pawns", ErrUnavailable, pawn, len(gear.GetPawns()))
	}
	if validID(pawnToken) != nil || validID(loadoutToken) != nil {
		return GearReplaceRead{}, raw, fmt.Errorf("%w: pawn %s has no loadout token (blocker %q)", ErrUnavailable, pawn, blocker)
	}
	if validID(thingToken) != nil || validID(definition) != nil {
		return GearReplaceRead{}, raw, fmt.Errorf("%w: %w: candidate %s not offered for pawn %s (blocker %q, %d candidates)", ErrUnavailable, ErrGearCandidateAbsent, thing, pawn, blocker, candidates)
	}
	return GearReplaceRead{Context: proto.Clone(observed.GetContext()).(*c.ObservationContext), PawnToken: pawnToken, ThingToken: thingToken, Definition: definition, LoadoutToken: loadoutToken}, raw, nil
}

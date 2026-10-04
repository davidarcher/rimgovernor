package bridge

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The royalty read (#1599): what remains of it is the Royalty-applicable gate
// (the ladder and permits are def-mirror rows, DefinitionCatalog.WithTitleDefs,
// #1875; the rest moved to PawnState.royalty and the colony section). The
// client keeps the last read for a load until RoyaltyRefreshTicks pass.

const methodRoyaltyFacts = "rimgovernor/observations_read_royalty_facts"

// RoyaltyRefreshTicks is how long a royalty read is reused (a quarter day).
const RoyaltyRefreshTicks = 15000

type royaltyCache struct {
	mu    sync.Mutex
	token string
	tick  int64
	facts *policy.RoyaltyFacts
}

// RoyaltyFacts is identity's royalty read as of tick now, from the cache while
// it is younger than RoyaltyRefreshTicks. A nil result with a nil error means
// Royalty is not applicable.
func (client *Client) RoyaltyFacts(ctx context.Context, identity *c.Identity, now int64) (*policy.RoyaltyFacts, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	held := &client.royalty
	held.mu.Lock()
	defer held.mu.Unlock()
	if held.token == identity.GetLoadToken() && now >= held.tick && now-held.tick < RoyaltyRefreshTicks {
		return held.facts, nil
	}
	request := &o.RoyaltyFactsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.RoyaltyFactsReply{}
	raw, err := client.protoRead(ctx, methodRoyaltyFacts, request, reply)
	if err != nil {
		return nil, err
	}
	var facts *policy.RoyaltyFacts
	switch v := reply.Outcome.(type) {
	case *o.RoyaltyFactsReply_Failure:
		return nil, failure(v.Failure, raw)
	case *o.RoyaltyFactsReply_Unavailable:
		if v.Unavailable.GetReason() != c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE {
			return nil, unavailable(v.Unavailable, raw)
		}
	case *o.RoyaltyFactsReply_Observed:
		if facts, err = DecodeRoyaltyFacts(v.Observed, identity); err != nil {
			return nil, err
		}
	default:
		return nil, contract("missing royalty outcome")
	}
	held.token, held.tick, held.facts = identity.GetLoadToken(), now, facts
	return facts, nil
}

// DecodeRoyaltyFacts validates a royalty read under identity.
func DecodeRoyaltyFacts(v *o.RoyaltyFacts, identity *c.Identity) (*policy.RoyaltyFacts, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid royalty context")
	}
	out := &policy.RoyaltyFacts{Holders: map[policy.PawnID][]policy.RoyalHolding{},
		Psycasts: map[policy.PawnID][]policy.Psycast{}, Casters: map[policy.PawnID]policy.PsycasterState{}}
	return out, nil
}

// WithPawnRoyalty adds each colonist's own royalty facts (PawnState.royalty,
// #1876) to the colony-level read. facts is the client's cached read and is
// not modified. A colonist with neither a holding nor a psycast is not
// listed. A row whose royalty read failed (ReadIssue "royalty") or whose block
// is invalid is an error: royalty stays unknown rather than read as empty.
func WithPawnRoyalty(facts policy.RoyaltyFacts, pawns *o.PawnSnapshot) (policy.RoyaltyFacts, error) {
	if pawns == nil {
		return policy.RoyaltyFacts{}, contract("no pawn rows for the royalty facts")
	}
	facts.Holders, facts.Psycasts, facts.Casters = map[policy.PawnID][]policy.RoyalHolding{}, map[policy.PawnID][]policy.Psycast{}, map[policy.PawnID]policy.PsycasterState{}
	for _, pawn := range pawns.Pawns {
		id := pawn.GetPawn().GetId()
		for _, issue := range pawn.Issues {
			if issue.GetField() == "royalty" {
				return policy.RoyaltyFacts{}, contract("royalty of pawn %s unread", id)
			}
		}
		row := pawn.Royalty
		if row == nil || len(row.Holdings) == 0 && len(row.Psycasts) == 0 {
			continue
		}
		if validID(id) != nil {
			return policy.RoyaltyFacts{}, contract("invalid royalty pawn")
		}
		holdings := []policy.RoyalHolding{}
		for _, h := range row.Holdings {
			if validID(h.GetFactionDef()) != nil || h.Title != nil && validID(h.GetTitle()) != nil {
				return policy.RoyaltyFacts{}, contract("invalid royalty holding")
			}
			for _, permit := range h.Permits {
				if validID(permit) != nil {
					return policy.RoyaltyFacts{}, contract("invalid royalty holding permit")
				}
			}
			cooldowns := map[string]policy.PermitCooldown{}
			for _, cd := range h.PermitCooldowns {
				if validID(cd.GetPermit()) != nil || cd.GetLastUsedTick() < 0 || cd.GetCooldownRemainingTicks() < 0 {
					return policy.RoyaltyFacts{}, contract("invalid royalty permit cooldown")
				}
				if _, dup := cooldowns[cd.GetPermit()]; dup {
					return policy.RoyaltyFacts{}, contract("duplicate royalty permit cooldown %s", cd.GetPermit())
				}
				cooldowns[cd.GetPermit()] = policy.PermitCooldown{LastUsedTick: optionalFact(intPtr(cd.LastUsedTick)), RemainingTicks: optionalFact(intPtr(cd.CooldownRemainingTicks))}
			}
			holdings = append(holdings, policy.RoyalHolding{FactionDef: h.GetFactionDef(), Title: h.GetTitle(), Favor: optionalFact(intPtr(h.Favor)), PermitPoints: optionalFact(intPtr(h.PermitPoints)), Permits: append([]string{}, h.Permits...), Cooldowns: cooldowns})
		}
		facts.Holders[policy.PawnID(id)] = holdings
		casts := []policy.Psycast{}
		known := map[string]bool{}
		for _, cast := range row.Psycasts {
			name := cast.GetDefName()
			if validID(name) != nil || known[name] || cast.GetPsyfocusCost() < 0 || cast.GetPsyfocusCost() > 1 || cast.GetEntropy() < 0 || cast.GetCooldownTicks() < 0 || cast.GetLevel() < 0 {
				return policy.RoyaltyFacts{}, contract("invalid royalty psycast")
			}
			known[name] = true
			target, ok := psycastTargets[cast.GetTargetKind()]
			if !ok {
				return policy.RoyaltyFacts{}, contract("invalid royalty psycast target")
			}
			casts = append(casts, policy.Psycast{Def: name, Level: optionalFact(intPtr(cast.Level)), PsyfocusCost: optionalFact(cast.PsyfocusCost), Entropy: optionalFact(cast.Entropy),
				Target: target, CooldownTicks: optionalFact(intPtr(cast.CooldownTicks)), CooldownRemaining: optionalFact(intPtr(cast.CooldownRemainingTicks))})
		}
		facts.Psycasts[policy.PawnID(id)] = casts
		if row.GetPsyfocus() < 0 || row.GetPsyfocus() > 1 || row.GetEntropy() < 0 || row.GetEntropyMax() < 0 {
			return policy.RoyaltyFacts{}, contract("invalid royalty psycaster state")
		}
		facts.Casters[policy.PawnID(id)] = policy.PsycasterState{Psyfocus: optionalFact(row.Psyfocus), Entropy: optionalFact(row.Entropy), EntropyMax: optionalFact(row.EntropyMax)}
	}
	return facts, nil
}

var psycastTargets = map[o.PsycastTargetKind]policy.PsycastTarget{
	o.PsycastTargetKind_PSYCAST_TARGET_KIND_UNSPECIFIED: "",
	o.PsycastTargetKind_PSYCAST_TARGET_KIND_SELF:        policy.PsycastTargetSelf,
	o.PsycastTargetKind_PSYCAST_TARGET_KIND_PAWN:        policy.PsycastTargetPawn,
	o.PsycastTargetKind_PSYCAST_TARGET_KIND_THING:       policy.PsycastTargetThing,
	o.PsycastTargetKind_PSYCAST_TARGET_KIND_CELL:        policy.PsycastTargetCell,
}

func intPtr(p *int32) *int {
	if p == nil {
		return nil
	}
	v := int(*p)
	return &v
}

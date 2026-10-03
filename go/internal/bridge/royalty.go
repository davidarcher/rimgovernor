package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The royalty read (#1599): the title ladder, permit catalog and colonist
// holdings. They change on quest completion or a title change, so the client
// keeps the last read for a load until RoyaltyRefreshTicks pass.

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
	return client.royaltyFacts(ctx, identity, now, false)
}

// FreshRoyaltyFacts is the royalty read taken now, bypassing and refreshing the
// cache: the cooldown re-read that resolves an uncertain ability receipt (#1607).
func (client *Client) FreshRoyaltyFacts(ctx context.Context, identity *c.Identity, now int64) (*policy.RoyaltyFacts, error) {
	return client.royaltyFacts(ctx, identity, now, true)
}

func (client *Client) royaltyFacts(ctx context.Context, identity *c.Identity, now int64, fresh bool) (*policy.RoyaltyFacts, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	held := &client.royalty
	held.mu.Lock()
	defer held.mu.Unlock()
	if !fresh && held.token == identity.GetLoadToken() && now >= held.tick && now-held.tick < RoyaltyRefreshTicks {
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
	out := &policy.RoyaltyFacts{Permits: map[string]policy.RoyalPermit{}, Holders: map[policy.PawnID][]policy.RoyalHolding{},
		Psycasts: map[policy.PawnID][]policy.Psycast{}, Neuroformers: map[string]policy.Neuroformer{}}
	titles := map[string]bool{}
	for _, row := range v.Ladder {
		if validID(row.GetDefName()) != nil || titles[row.GetDefName()] {
			return nil, contract("invalid royalty title")
		}
		titles[row.GetDefName()] = true
		for _, thing := range row.ThroneThings {
			if validID(thing) != nil {
				return nil, contract("invalid royalty throne definition")
			}
		}
		rung := policy.RoyalRung{Title: row.GetDefName(), Seniority: optionalFact(intPtr(row.Seniority)), FavorNeeded: optionalFact(intPtr(row.FavorNeeded)),
			ThroneMinImpressiveness: optionalFact(intPtr(row.ThroneMinImpressiveness)), ThroneMinArea: optionalFact(intPtr(row.ThroneMinArea)),
			ThroneThings: append([]string{}, row.ThroneThings...), ThroneAssigned: optionalFact(row.ThroneAssigned),
			BedroomMinArea: optionalFact(intPtr(row.BedroomMinArea)), BedroomMinImpressiveness: optionalFact(intPtr(row.BedroomMinImpressiveness)), BedroomFloored: optionalFact(row.BedroomFloored)}
		for _, req := range row.BedroomThings {
			if req == nil || len(req.AnyOf) == 0 || req.GetCount() < 1 {
				return nil, contract("invalid royalty bedroom requirement")
			}
			thing := policy.BedroomThing{Count: int(req.GetCount())}
			for _, def := range req.AnyOf {
				if validID(def) != nil {
					return nil, contract("invalid royalty bedroom definition")
				}
				thing.AnyOf = append(thing.AnyOf, policy.Resource(def))
			}
			rung.BedroomThings = append(rung.BedroomThings, thing)
		}
		out.Ladder = append(out.Ladder, rung)
	}
	for _, row := range v.Permits {
		name := row.GetDefName()
		if validID(name) != nil || row.MinTitle != nil && validID(row.GetMinTitle()) != nil {
			return nil, contract("invalid royalty permit")
		}
		if _, exists := out.Permits[name]; exists {
			return nil, contract("duplicate royalty permit %s", name)
		}
		out.Permits[name] = policy.RoyalPermit{Name: name, MinTitle: optionalFact(row.MinTitle), PermitPoints: optionalFact(intPtr(row.PermitPoints)), Acts: optionalFact(row.Acts),
			FavorCost: optionalFact(intPtr(row.FavorCost)), CooldownDays: optionalFact(row.CooldownDays)}
	}
	for _, row := range v.Pawns {
		id := row.GetPawn().GetId()
		if validID(id) != nil {
			return nil, contract("invalid royalty pawn")
		}
		if _, exists := out.Holders[policy.PawnID(id)]; exists {
			return nil, contract("duplicate royalty pawn %s", id)
		}
		holdings := []policy.RoyalHolding{}
		for _, h := range row.Holdings {
			if validID(h.GetFactionDef()) != nil || h.Title != nil && validID(h.GetTitle()) != nil {
				return nil, contract("invalid royalty holding")
			}
			for _, permit := range h.Permits {
				if validID(permit) != nil {
					return nil, contract("invalid royalty holding permit")
				}
			}
			cooldowns := map[string]policy.PermitCooldown{}
			for _, cd := range h.PermitCooldowns {
				if validID(cd.GetPermit()) != nil || cd.GetLastUsedTick() < 0 || cd.GetCooldownRemainingTicks() < 0 {
					return nil, contract("invalid royalty permit cooldown")
				}
				if _, dup := cooldowns[cd.GetPermit()]; dup {
					return nil, contract("duplicate royalty permit cooldown %s", cd.GetPermit())
				}
				cooldowns[cd.GetPermit()] = policy.PermitCooldown{LastUsedTick: optionalFact(intPtr(cd.LastUsedTick)), RemainingTicks: optionalFact(intPtr(cd.CooldownRemainingTicks))}
			}
			holdings = append(holdings, policy.RoyalHolding{FactionDef: h.GetFactionDef(), Title: h.GetTitle(), Favor: optionalFact(intPtr(h.Favor)), PermitPoints: optionalFact(intPtr(h.PermitPoints)), Permits: append([]string{}, h.Permits...), Cooldowns: cooldowns})
		}
		out.Holders[policy.PawnID(id)] = holdings
		casts := []policy.Psycast{}
		known := map[string]bool{}
		for _, cast := range row.Psycasts {
			name := cast.GetDefName()
			if validID(name) != nil || known[name] || cast.GetPsyfocusCost() < 0 || cast.GetPsyfocusCost() > 1 || cast.GetEntropy() < 0 || cast.GetCooldownTicks() < 0 || cast.GetLevel() < 0 {
				return nil, contract("invalid royalty psycast")
			}
			known[name] = true
			target, ok := psycastTargets[cast.GetTargetKind()]
			if !ok {
				return nil, contract("invalid royalty psycast target")
			}
			casts = append(casts, policy.Psycast{Def: name, Level: optionalFact(intPtr(cast.Level)), PsyfocusCost: optionalFact(cast.PsyfocusCost), Entropy: optionalFact(cast.Entropy),
				Target: target, CooldownTicks: optionalFact(intPtr(cast.CooldownTicks))})
		}
		out.Psycasts[policy.PawnID(id)] = casts
	}
	for _, row := range v.Neuroformers {
		name := row.GetDefName()
		if validID(name) != nil || row.TeachesPsycast != nil && validID(row.GetTeachesPsycast()) != nil || row.GetHeld() < 0 {
			return nil, contract("invalid royalty neuroformer")
		}
		if _, exists := out.Neuroformers[name]; exists {
			return nil, contract("duplicate royalty neuroformer %s", name)
		}
		out.Neuroformers[name] = policy.Neuroformer{Def: name, TeachesPsycast: row.GetTeachesPsycast(), Held: optionalFact(intPtr(row.Held)),
			Craftable: optionalFact(row.Craftable), Tradeable: optionalFact(row.Tradeable)}
	}
	seen := map[string]bool{}
	for _, row := range v.Ceremonies {
		pawn, bestower := row.GetPawn().GetId(), row.GetBestower().GetId()
		if validID(pawn) != nil || bestower != "" && validID(bestower) != nil || validID(row.GetQuest()) != nil || row.FactionDef != nil && validID(row.GetFactionDef()) != nil ||
			row.Title != nil && validID(row.GetTitle()) != nil || seen[pawn+"/"+row.GetFactionDef()] {
			return nil, contract("invalid royalty ceremony")
		}
		seen[pawn+"/"+row.GetFactionDef()] = true
		c := policy.BestowingCeremony{Quest: domain.QuestID(row.GetQuest()), Pawn: policy.PawnID(pawn), Bestower: policy.PawnID(bestower), Faction: row.GetFactionDef(), Title: row.GetTitle(),
			Accepted: optionalFact(row.Accepted), BestowerWaiting: optionalFact(row.BestowerWaiting), Started: optionalFact(row.Started)}
		if row.Spot != nil {
			c.Spot = domain.Known(domain.Cell{X: row.Spot.GetX(), Z: row.Spot.GetZ()})
		}
		for _, a := range row.Attendees {
			if validID(a.GetId()) != nil {
				return nil, contract("invalid royalty ceremony attendee")
			}
			c.Attendees = append(c.Attendees, policy.PawnID(a.GetId()))
		}
		out.Ceremonies = append(out.Ceremonies, c)
	}
	return out, nil
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

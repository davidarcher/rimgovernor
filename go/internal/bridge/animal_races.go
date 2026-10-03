package bridge

import (
	"context"
	"math"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The animal race catalog (#1625): the static facts of every animal race.
// They hold for a whole load, so the client reads the catalog on the first
// request under a load token and keeps it in memory until the token changes.

const methodAnimalRaceCatalog = "rimgovernor/observations_read_animal_race_catalog"

// AnimalRaces is one load's decoded race catalog.
type AnimalRaces struct {
	LoadToken string
	policy.AnimalRaceCatalog
}

type animalRaceCache struct {
	mu    sync.Mutex
	races *AnimalRaces
}

// AnimalRaceCatalog is the race catalog of identity's load, read over GABP
// the first time the load token is seen.
func (client *Client) AnimalRaceCatalog(ctx context.Context, identity *c.Identity) (*AnimalRaces, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, err
	}
	held := &client.animalRaces
	held.mu.Lock()
	defer held.mu.Unlock()
	if held.races != nil && held.races.LoadToken == identity.GetLoadToken() {
		return held.races, nil
	}
	request := &o.AnimalRaceCatalogRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.AnimalRaceCatalogReply{}
	raw, err := client.protoRead(ctx, methodAnimalRaceCatalog, request, reply)
	if err != nil {
		return nil, err
	}
	var races *AnimalRaces
	switch v := reply.Outcome.(type) {
	case *o.AnimalRaceCatalogReply_Failure:
		return nil, failure(v.Failure, raw)
	case *o.AnimalRaceCatalogReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	case *o.AnimalRaceCatalogReply_Observed:
		if races, err = DecodeAnimalRaceCatalog(v.Observed, identity); err != nil {
			return nil, err
		}
	default:
		return nil, contract("missing animal race catalog outcome")
	}
	held.races = races
	return races, nil
}

// DecodeAnimalRaceCatalog validates a race catalog read under identity. An
// absent scalar is an unknown fact; a negative or non-finite one is a
// contract breach.
func DecodeAnimalRaceCatalog(v *o.AnimalRaceCatalog, identity *c.Identity) (*AnimalRaces, error) {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return nil, contract("invalid animal race catalog context")
	}
	out := &AnimalRaces{LoadToken: identity.GetLoadToken(), AnimalRaceCatalog: policy.AnimalRaceCatalog{Races: make(map[policy.Resource]policy.AnimalRace, len(v.Races))}}
	for _, row := range v.Races {
		def := row.GetDefName()
		if validID(def) != nil {
			return nil, contract("invalid animal race")
		}
		if _, exists := out.Races[policy.Resource(def)]; exists {
			return nil, contract("duplicate animal race %s", def)
		}
		race := policy.AnimalRace{Def: policy.Resource(def), Trainability: optionalFact(row.Trainability), Trainables: append([]string{}, row.Trainables...), MinimumHandlingSkill: optionalFact(intPtr(row.MinimumHandlingSkill))}
		for _, trainable := range row.Trainables {
			if validID(trainable) != nil {
				return nil, contract("invalid animal race trainable")
			}
		}
		sort.Strings(race.Trainables)
		if row.Trainability != nil && validID(row.GetTrainability()) != nil || row.MinimumHandlingSkill != nil && row.GetMinimumHandlingSkill() < 0 {
			return nil, contract("invalid animal race %s", def)
		}
		var err error
		for _, scalar := range []struct {
			in  *float64
			out *domain.Fact[float64]
		}{{row.CarryingCapacity, &race.CarryingCapacity}, {row.Wildness, &race.Wildness}, {row.BodySize, &race.BodySize}, {row.CombatPower, &race.CombatPower}, {row.MarketValue, &race.MarketValue}} {
			if *scalar.out, err = nonNegative(scalar.in); err != nil {
				return nil, contract("invalid animal race %s scalar", def)
			}
		}
		for _, product := range row.Products {
			if validID(product.GetKind()) != nil || validID(product.GetDefName()) != nil {
				return nil, contract("invalid animal race %s product", def)
			}
			amount, aerr := nonNegative(product.Amount)
			interval, ierr := nonNegative(product.IntervalDays)
			if aerr != nil || ierr != nil {
				return nil, contract("invalid animal race %s product", def)
			}
			race.Products = append(race.Products, policy.RaceProduct{Kind: product.GetKind(), Def: policy.Resource(product.GetDefName()), Amount: amount, IntervalDays: interval})
		}
		out.Races[race.Def] = race
	}
	return out, nil
}

func nonNegative(p *float64) (domain.Fact[float64], error) {
	if p == nil {
		return domain.Unknown[float64](), nil
	}
	if *p < 0 || math.IsNaN(*p) || math.IsInf(*p, 0) {
		return domain.Unknown[float64](), contract("negative or non-finite scalar")
	}
	return domain.Known(*p), nil
}

package policy

import "strings"

// WorldEvaluationPolicy is the bounded subset of Python's ExpeditionPolicy
// evaluate_world reads: the travel food margin a caravan must keep in
// reserve past its home route's estimated travel time before it counts as
// stranded and needing player recovery attention. Richer expedition-risk
// scoring (temperature, goodwill, hostile settlements, concurrent caravan
// limits) belongs to evaluate_expedition, a separate advisory not ported
// here -- it needs native route hostility/temperature/goodwill fields Go
// does not read yet.
type WorldEvaluationPolicy struct {
	TravelFoodMarginDays float64
}

// WorldEvaluationRouteFact is one candidate player-home route for a
// caravan, or (as ReachableHome on the report) the shortest one that
// qualifies: native marked it reachable and reported a known, non-negative
// estimated travel time. It mirrors bridge.WorldRouteFact without this
// package depending on the bridge package, the same "policy stays a pure
// function of plain facts" discipline every other admission/evaluation
// function here follows.
type WorldEvaluationRouteFact struct {
	DestinationMapID    int32
	Reachable           bool
	EstimatedTicks      int64
	EstimatedTicksKnown bool
}

// WorldEvaluationPawnFact is one caravan crew member's dead/downed status.
// A caller passes DeadKnown/DownedKnown false only when native's own
// optional fields were absent; EvaluateWorld treats that the same as
// Python's `p.get('dead') is False` -- not proven alive, not healthy.
type WorldEvaluationPawnFact struct {
	Dead, DeadKnown     bool
	Downed, DownedKnown bool
}

// WorldEvaluationCaravanFact is one in-flight caravan's read-only census
// row: its crew's health, its native home routes, its remaining travel
// food, and its full carried cargo (bridge.CaravanJourney.Inventory,
// native's caravan-level aggregate across every pawn aboard). Python's
// original summed each pawn's own inventory list instead; both read the
// same native aggregate, so this is a narrower read of equivalent data, not
// a behavioral change -- see bridge.CaravanJourney's Inventory doc comment.
type WorldEvaluationCaravanFact struct {
	ID            string
	FoodDays      float64
	FoodDaysKnown bool
	HomeRoutes    []WorldEvaluationRouteFact
	Pawns         []WorldEvaluationPawnFact
	Inventory     map[string]int64
}

// WorldEvaluationTradeRequestFact is one quest's native settlement trade
// objective row: the required resource def name and count, and the
// settlement's destination tile. It mirrors bridge.QuestTradeRequestFact.
type WorldEvaluationTradeRequestFact struct {
	Resource        string
	Count           int64
	DestinationTile int32
}

// WorldEvaluationQuestFact is one quest's read-only census row: its native
// settled state, native's own CanAcceptQuest verdict, and its full trade
// objective list.
type WorldEvaluationQuestFact struct {
	ID             string
	State          string
	NativeEligible bool
	TradeRequests  []WorldEvaluationTradeRequestFact
}

// WorldEvaluationFacts is everything EvaluateWorld reads: a same-tick world
// census (Readable false whenever that census was incomplete -- native
// success/complete/ResultWasTruncated evidence a caller checks before
// calling this, mirroring Python's own guard), the observed caravans and
// quests, and the home colony's current resource stock
// (bridge.ReadColonyFacts's Resources census, reused rather than a second
// native resource read).
type WorldEvaluationFacts struct {
	Readable      bool
	Caravans      []WorldEvaluationCaravanFact
	Quests        []WorldEvaluationQuestFact
	HomeResources map[string]int64
}

// WorldEvaluationCaravanReport is one caravan's read-only recovery
// advisory.
type WorldEvaluationCaravanReport struct {
	ID               string
	RecoveryRequired bool
	Healthy          bool
	FoodDays         float64
	FoodDaysKnown    bool
	// ReachableHome is nil when no home route qualifies (unreachable,
	// unknown or negative travel time); otherwise the shortest qualifying
	// route, the same selection Python's evaluate_world makes.
	ReachableHome  *WorldEvaluationRouteFact
	Recommendation string
}

// WorldEvaluationQuestReport is one quest's read-only resource-deficit and
// carried-cargo advisory.
type WorldEvaluationQuestReport struct {
	ID                string
	State             string
	NativeEligible    bool
	ResourceDeficits  map[string]int64
	ResourceScope     string
	CarriedCandidates []string
	Objectives        []WorldEvaluationTradeRequestFact
	Recommendation    string
}

// WorldEvaluationReport is evaluate_world's full read-only advisory: no
// admission, no CAS tokens, no dispatch -- it only summarizes already
// observed facts for a player or dashboard to act on. Readable false means
// the underlying world census was incomplete; Caravans/Quests/Scope are
// then empty/zero and Reason explains why.
type WorldEvaluationReport struct {
	Readable bool
	Reason   string
	Caravans []WorldEvaluationCaravanReport
	Quests   []WorldEvaluationQuestReport
	Policy   WorldEvaluationPolicy
	Scope    string
}

// EvaluateWorld ports Python's expedition_policy.evaluate_world: a pure,
// read-only report over an already-observed world census plus home
// resource stock. It performs no writes and makes no decisions -- every
// Recommendation is advisory text for a human, never an instruction this
// codebase's own goal/action system would execute.
func EvaluateWorld(p WorldEvaluationPolicy, facts WorldEvaluationFacts) WorldEvaluationReport {
	if !facts.Readable {
		return WorldEvaluationReport{Reason: "Complete native world evidence is required"}
	}
	caravans := make([]WorldEvaluationCaravanReport, 0, len(facts.Caravans))
	for _, caravan := range facts.Caravans {
		var home *WorldEvaluationRouteFact
		for i := range caravan.HomeRoutes {
			route := caravan.HomeRoutes[i]
			if !route.Reachable || !route.EstimatedTicksKnown || route.EstimatedTicks < 0 {
				continue
			}
			if home == nil || route.EstimatedTicks < home.EstimatedTicks {
				chosen := route
				home = &chosen
			}
		}
		healthy := len(caravan.Pawns) > 0
		for _, pawn := range caravan.Pawns {
			if !pawn.DeadKnown || pawn.Dead || !pawn.DownedKnown || pawn.Downed {
				healthy = false
				break
			}
		}
		stranded := home == nil || !caravan.FoodDaysKnown || caravan.FoodDays < float64(home.EstimatedTicks)/60000+p.TravelFoodMarginDays
		recommendation := "Observed return route available"
		if stranded || !healthy {
			recommendation = "Player review: hold, resupply or explicit return"
		}
		caravans = append(caravans, WorldEvaluationCaravanReport{
			ID: caravan.ID, RecoveryRequired: stranded || !healthy, Healthy: healthy,
			FoodDays: caravan.FoodDays, FoodDaysKnown: caravan.FoodDaysKnown,
			ReachableHome: home, Recommendation: recommendation,
		})
	}
	quests := make([]WorldEvaluationQuestReport, 0, len(facts.Quests))
	for _, quest := range facts.Quests {
		needed := map[string]int64{}
		validObjectives := len(quest.TradeRequests) > 0
		for _, row := range quest.TradeRequests {
			if row.Resource == "" || row.Count <= 0 {
				validObjectives = false
				continue
			}
			needed[row.Resource] += row.Count
		}
		deficits := make(map[string]int64, len(needed))
		anyDeficit := false
		for resource, count := range needed {
			deficit := count - facts.HomeResources[resource]
			if deficit < 0 {
				deficit = 0
			}
			deficits[resource] = deficit
			anyDeficit = anyDeficit || deficit > 0
		}
		var carried []string
		if len(needed) > 0 && validObjectives {
			for _, caravan := range facts.Caravans {
				fulfilled := true
				for resource, count := range needed {
					if caravan.Inventory[resource] < count {
						fulfilled = false
						break
					}
				}
				if fulfilled {
					carried = append(carried, caravan.ID)
				}
			}
		}
		terminal := strings.HasPrefix(quest.State, "Ended")
		recommendation := "Explicit player choice required; acceptance is separate from completion"
		switch {
		case terminal:
			recommendation = "Terminal objective; do not replay"
		case len(carried) > 0:
			recommendation = "Cargo observed in listed parties; validate native quality, freshness and settlement eligibility"
		case anyDeficit:
			recommendation = "Production or acquisition required; no future output credited"
		}
		quests = append(quests, WorldEvaluationQuestReport{
			ID: quest.ID, State: quest.State, NativeEligible: quest.NativeEligible,
			ResourceDeficits: deficits, ResourceScope: "Current home stock; carried cargo is listed separately",
			CarriedCandidates: carried, Objectives: quest.TradeRequests, Recommendation: recommendation,
		})
	}
	return WorldEvaluationReport{
		Readable: true, Caravans: caravans, Quests: quests, Policy: p,
		Scope: "Read-only evaluation; no automatic quest acceptance, diplomatic escalation or expedition orders",
	}
}

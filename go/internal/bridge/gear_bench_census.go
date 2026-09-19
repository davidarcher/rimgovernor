package bridge

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

const gearBenchLimit = 256
const gearRecipeLimit = 256

// GearBenchRead is one workbench's fresh CAS token alongside the
// policy.GearBench census SelectGearMethod's produce path (GearProduce)
// evaluates. Unlike the specialized Cooking/Butchering rows ColonyFactsSnapshot
// carries, no ColonyFactsSnapshot field lists arbitrary gear-crafting benches
// (tailoring, smithing, ...), so this reads every bench's bill stack via the
// generic ReadBills RPC and, per bench, its recipe catalog (with ingredients)
// via ReadRecipes. Benches irrelevant to gear (a colony's stove, for example)
// are harmless here: SelectGearMethod only matches recipes whose Products
// include the needed definition.
type GearBenchRead struct {
	Token string
	Bench policy.GearBench
}

// ReadGearBenches is a fresh, uncached census; callers construct a
// domain.ProductionBill from its Token immediately, the same "read then
// dispatch in one step" shape ReadGearReplacement uses for wear orders.
func (client *Client) ReadGearBenches(ctx context.Context, identity *c.Identity) ([]GearBenchRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.BillsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Page: &c.PageRequest{Limit: proto.Uint32(gearBenchLimit)}}
	reply := &o.BillsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_bills", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	var snapshot *o.BillsSnapshot
	switch v := reply.Outcome.(type) {
	case *o.BillsReply_Observed:
		snapshot = v.Observed
	case *o.BillsReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.BillsReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	default:
		return nil, raw, contract("missing bills outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, raw, contract("invalid bills context")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Benches) > gearBenchLimit {
		return nil, raw, contract("incomplete bills census")
	}
	seen := map[string]bool{}
	out := make([]GearBenchRead, 0, len(snapshot.Benches))
	for _, stack := range snapshot.Benches {
		if stack == nil || stack.Bench == nil || validID(stack.Bench.GetId()) != nil || seen[stack.Bench.GetId()] {
			return nil, raw, contract("invalid gear bench identity")
		}
		if stack.Snapshot == nil || validID(stack.Snapshot.GetToken()) != nil || stack.Snapshot.GetEntityId() != stack.Bench.GetId() {
			return nil, raw, contract("invalid gear bench token")
		}
		seen[stack.Bench.GetId()] = true
		recipes, err := client.readGearRecipes(ctx, identity, stack.Bench.GetId())
		if err != nil {
			return nil, raw, err
		}
		bills, err := gearBillsFromStack(stack, recipes)
		if err != nil {
			return nil, raw, err
		}
		bench := policy.GearBench{ID: stack.Bench.GetId(), Bills: domain.Known(bills), Recipes: domain.Known(recipes)}
		out = append(out, GearBenchRead{Token: stack.Snapshot.GetToken(), Bench: bench})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Bench.ID < out[j].Bench.ID })
	return out, raw, nil
}

func (client *Client) readGearRecipes(ctx context.Context, identity *c.Identity, bench string) ([]policy.GearRecipe, error) {
	request := &o.RecipesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, BenchId: proto.String(bench), Page: &c.PageRequest{Limit: proto.Uint32(gearRecipeLimit)}}
	reply := &o.RecipesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_recipes", request, reply)
	if err != nil {
		return nil, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, err
	}
	var snapshot *o.RecipesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.RecipesReply_Observed:
		snapshot = v.Observed
	case *o.RecipesReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	case *o.RecipesReply_Failure:
		return nil, failure(v.Failure, raw)
	default:
		return nil, contract("missing recipes outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, contract("invalid recipes context")
	}
	if snapshot.Snapshot == nil || snapshot.Snapshot.GetEntityId() != bench {
		return nil, contract("recipe snapshot bench mismatch")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Recipes) > gearRecipeLimit {
		return nil, contract("incomplete recipe census")
	}
	names := map[string]bool{}
	out := make([]policy.GearRecipe, 0, len(snapshot.Recipes))
	for _, row := range snapshot.Recipes {
		if row == nil || row.Recipe == nil || validID(row.Recipe.GetDefName()) != nil || names[row.Recipe.GetDefName()] {
			return nil, contract("invalid gear recipe identity")
		}
		names[row.Recipe.GetDefName()] = true
		recipe := policy.GearRecipe{Definition: row.Recipe.GetDefName()}
		if row.AvailableNow != nil {
			recipe.Available = domain.Known(row.GetAvailableNow())
		}
		if row.AvailableOnBench != nil {
			recipe.AvailableOn = domain.Known(row.GetAvailableOnBench())
		}
		products, err := gearRecipeProducts(row.Products)
		if err != nil {
			return nil, err
		}
		recipe.Products = products
		recipe.Ingredients = GearRecipeIngredients(row.Ingredients)
		recipe.RequiredWork = gearRecipeWork(row)
		out = append(out, recipe)
	}
	return out, nil
}

// ReadRecipeCatalog reads the native recipe definition catalog narrowed to
// recipes producing product: which player-buildable bench definitions host
// each recipe and whether its research is complete. It is the discovery step
// a workshop planner takes before any bench exists, so no bench snapshot or
// AvailableOn is involved; the reply is bounded and complete or an error.
func (client *Client) ReadRecipeCatalog(ctx context.Context, identity *c.Identity, product string) ([]policy.RecipeHost, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if validID(product) != nil {
		return nil, Result{}, contract("invalid recipe product")
	}
	request := &o.RecipesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, ProductDef: proto.String(product), Page: &c.PageRequest{Limit: proto.Uint32(gearRecipeLimit)}}
	reply := &o.RecipesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_recipes", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	var snapshot *o.RecipesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.RecipesReply_Observed:
		snapshot = v.Observed
	case *o.RecipesReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.RecipesReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	default:
		return nil, raw, contract("missing recipes outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, raw, contract("invalid recipes context")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || len(snapshot.Recipes) > gearRecipeLimit {
		return nil, raw, contract("incomplete recipe catalog")
	}
	names := map[string]bool{}
	out := make([]policy.RecipeHost, 0, len(snapshot.Recipes))
	for _, row := range snapshot.Recipes {
		if row == nil || row.Recipe == nil || validID(row.Recipe.GetDefName()) != nil || names[row.Recipe.GetDefName()] || row.AvailableNow == nil {
			return nil, raw, contract("invalid recipe catalog row")
		}
		names[row.Recipe.GetDefName()] = true
		products, err := gearRecipeProducts(row.Products)
		if err != nil {
			return nil, raw, err
		}
		if len(row.BenchDefs) == 0 || len(row.BenchDefs) > 256 {
			return nil, raw, contract("recipe catalog row without benches")
		}
		host := policy.RecipeHost{Definition: row.Recipe.GetDefName(), Products: products, Available: row.GetAvailableNow(), Ingredients: GearRecipeIngredients(row.Ingredients), RequiredWork: gearRecipeWork(row)}
		if len(row.ResearchPrerequisites) > 256 {
			return nil, raw, contract("recipe research prerequisites exceed bound")
		}
		for _, project := range row.ResearchPrerequisites {
			if validID(project) != nil {
				return nil, raw, contract("invalid recipe research prerequisite")
			}
			host.Research = append(host.Research, project)
		}
		sort.Strings(host.Research)
		seen := map[string]bool{}
		for _, bench := range row.BenchDefs {
			if validID(bench) != nil || seen[bench] {
				return nil, raw, contract("invalid recipe catalog bench")
			}
			seen[bench] = true
			host.Benches = append(host.Benches, bench)
		}
		sort.Strings(host.Benches)
		out = append(out, host)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Definition < out[j].Definition })
	return out, raw, nil
}

func gearRecipeProducts(list []*o.Quantity) ([]policy.Resource, error) {
	if len(list) > 256 {
		return nil, contract("recipe products exceed bound")
	}
	seen := map[policy.Resource]bool{}
	out := make([]policy.Resource, 0, len(list))
	for _, q := range list {
		if q == nil || validID(q.GetDefName()) != nil {
			return nil, contract("invalid recipe product")
		}
		name := policy.Resource(q.GetDefName())
		if seen[name] {
			continue
		}
		seen[name] = true
		out = append(out, name)
	}
	return out, nil
}

// gearRecipeWork is the one work requirement a bill on this recipe needs
// covered: the native work type whose DoBill giver serves the bench
// (RecipeState.work_type — "Crafting" for a crafting spot, "Tailoring" for a
// tailor bench, "Cooking" for a stove) together with the recipe's own work
// skill and the highest native minimum level over its skill requirements.
// Unknown without a native work type, or when a skill row is malformed.
func gearRecipeWork(row *o.RecipeState) domain.Fact[[]policy.WorkRequirement] {
	if row.WorkType == nil || validID(row.GetWorkType()) != nil || len(row.Skills) > 256 {
		return domain.Unknown[[]policy.WorkRequirement]()
	}
	minimum := 0
	for _, skill := range row.Skills {
		if skill == nil || validID(skill.GetDefName()) != nil || skill.Minimum == nil || skill.GetMinimum() < 0 {
			return domain.Unknown[[]policy.WorkRequirement]()
		}
		minimum = max(minimum, int(skill.GetMinimum()))
	}
	skill := row.GetWorkSkill()
	if skill != "" && validID(skill) != nil {
		return domain.Unknown[[]policy.WorkRequirement]()
	}
	return domain.Known([]policy.WorkRequirement{{Work: policy.WorkType(row.GetWorkType()), Skill: skill, Minimum: minimum}})
}

func gearBillsFromStack(stack *o.BillStack, recipes []policy.GearRecipe) ([]policy.GearBill, error) {
	if len(stack.Bills) > 15 {
		return nil, contract("bill stack exceeds bound")
	}
	products := map[string][]policy.Resource{}
	for _, r := range recipes {
		products[r.Definition] = r.Products
	}
	out := make([]policy.GearBill, 0, len(stack.Bills))
	for _, bill := range stack.Bills {
		if bill == nil || bill.Recipe == nil || validID(bill.Recipe.GetDefName()) != nil {
			return nil, contract("invalid gear bill identity")
		}
		row := policy.GearBill{}
		if bill.Suspended != nil && bill.Finished != nil {
			row.Active = domain.Known(!bill.GetSuspended() && !bill.GetFinished())
		}
		if list, known := products[bill.Recipe.GetDefName()]; known {
			row.Products = list
		}
		out = append(out, row)
	}
	return out, nil
}

// ReadSupplyStock reads the currently available, unforbidden owned quantity
// for a bounded set of resource definitions, the same shape
// policy.GearPlanningRequest.Stock and the resource_production.go primitives
// need for funding checks. An unlisted definition (native reports zero
// matching stacks) is reported Unknown, not zero, since the reply omits rows
// it found nothing for and this cannot be told apart from an incomplete read.
func (client *Client) ReadSupplyStock(ctx context.Context, identity *c.Identity, defNames []string) ([]policy.Stock, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if len(defNames) == 0 {
		return nil, Result{}, nil
	}
	if len(defNames) > 256 {
		return nil, Result{}, contract("supply stock query exceeds bound")
	}
	names := map[string]bool{}
	for _, n := range defNames {
		if validID(n) != nil || names[n] {
			return nil, Result{}, contract("invalid supply stock def name")
		}
		names[n] = true
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{DefNames: append([]string(nil), defNames...), Ownership: proto.String("ours")}, Page: &c.PageRequest{Limit: proto.Uint32(uint32(len(defNames)))}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	var snapshot *o.SuppliesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ListSuppliesReply_Observed:
		snapshot = v.Observed
	case *o.ListSuppliesReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.ListSuppliesReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	default:
		return nil, raw, contract("missing supplies outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, raw, contract("invalid supplies context")
	}
	counts := snapshot.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || len(snapshot.Stocks) > len(defNames) {
		return nil, raw, contract("incomplete supplies census")
	}
	seenOut := map[string]bool{}
	out := make([]policy.Stock, 0, len(defNames))
	for _, row := range snapshot.Stocks {
		if row == nil || row.Definition == nil || validID(row.Definition.GetDefName()) != nil || !names[row.Definition.GetDefName()] || seenOut[row.Definition.GetDefName()] {
			return nil, raw, contract("invalid resource stock row")
		}
		seenOut[row.Definition.GetDefName()] = true
		stock := policy.Stock{Resource: policy.Resource(row.Definition.GetDefName())}
		if row.OursUnforbidden != nil && row.GetOursUnforbidden() >= 0 {
			stock.Available = domain.Known(row.GetOursUnforbidden())
		}
		out = append(out, stock)
	}
	// A complete page groups the things present: a requested definition
	// with no row is a known zero, not an unobserved stock.
	for _, n := range defNames {
		if !seenOut[n] {
			out = append(out, policy.Stock{Resource: policy.Resource(n), Available: domain.Known(int64(0))})
		}
	}
	return out, raw, nil
}

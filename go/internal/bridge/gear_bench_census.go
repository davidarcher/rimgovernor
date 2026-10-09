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
	request := &o.BillsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
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
	catalog, err := client.DefinitionCatalog(ctx, identity)
	if err != nil {
		return nil, raw, err
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
		recipes, def, err := client.readGearRecipes(ctx, identity, stack.Bench.GetId(), catalog)
		if err != nil {
			return nil, raw, err
		}
		bills, err := gearBillsFromStack(stack, def, recipes, catalog)
		if err != nil {
			return nil, raw, err
		}
		bench := policy.GearBench{ID: stack.Bench.GetId(), Def: def, WorkSpeed: benchWorkSpeed(stack), Bills: domain.Known(bills), Recipes: domain.Known(recipes)}
		if stack.Usable != nil {
			bench.Usable = domain.Known(stack.GetUsable())
		}
		out = append(out, GearBenchRead{Token: stack.Snapshot.GetToken(), Bench: bench})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Bench.ID < out[j].Bench.ID })
	return out, raw, nil
}

func (client *Client) readGearRecipes(ctx context.Context, identity *c.Identity, bench string, catalog *DefinitionCatalog) ([]policy.GearRecipe, string, error) {
	request := &o.RecipesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, BenchId: proto.String(bench)}
	reply := &o.RecipesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_recipes", request, reply)
	if err != nil {
		return nil, "", err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, "", err
	}
	var snapshot *o.RecipesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.RecipesReply_Observed:
		snapshot = v.Observed
	case *o.RecipesReply_Unavailable:
		return nil, "", unavailable(v.Unavailable, raw)
	case *o.RecipesReply_Failure:
		return nil, "", failure(v.Failure, raw)
	default:
		return nil, "", contract("missing recipes outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return nil, "", contract("invalid recipes context")
	}
	if snapshot.Snapshot == nil || snapshot.Snapshot.GetEntityId() != bench {
		return nil, "", contract("recipe snapshot bench mismatch")
	}
	if validID(snapshot.GetBenchDef()) != nil {
		return nil, "", contract("recipe snapshot has no bench definition")
	}
	names := map[string]bool{}
	out := make([]policy.GearRecipe, 0, len(snapshot.Recipes))
	for _, row := range snapshot.Recipes {
		if row == nil || row.Recipe == nil || validID(row.Recipe.GetDefName()) != nil || names[row.Recipe.GetDefName()] {
			return nil, "", contract("invalid gear recipe identity")
		}
		names[row.Recipe.GetDefName()] = true
		recipe := policy.GearRecipe{Definition: row.Recipe.GetDefName()}
		if recipe.Role, err = catalog.RecipeRole(recipe.Definition); err != nil {
			return nil, "", err
		}
		if row.AvailableNow != nil {
			recipe.Available = domain.Known(row.GetAvailableNow())
		}
		if row.AvailableOnBench != nil {
			recipe.AvailableOn = domain.Known(row.GetAvailableOnBench())
		}
		// What the recipe is and costs is the catalog's; the frame says only
		// whether this bench offers it now.
		if recipe.Products, err = catalog.RecipeProducts(recipe.Definition); err != nil {
			return nil, "", err
		}
		if recipe.Ingredients, err = catalog.RecipeIngredients(recipe.Definition); err != nil {
			return nil, "", err
		}
		if recipe.RequiredWork, err = catalog.RecipeWork(recipe.Definition, snapshot.GetBenchDef()); err != nil {
			return nil, "", err
		}
		if recipe.WorkAmount, err = catalog.RecipeWorkAmount(recipe.Definition); err != nil {
			return nil, "", err
		}
		if recipe.MechKind, err = catalog.RecipeMechKind(recipe.Definition); err != nil {
			return nil, "", err
		}
		out = append(out, recipe)
	}
	return out, snapshot.GetBenchDef(), nil
}

// benchWorkSpeed is the stack's work-table speed factor; unknown when native
// sent none or a negative one.
func benchWorkSpeed(stack *o.BillStack) domain.Fact[float64] {
	if stack.WorkSpeed == nil || stack.GetWorkSpeed() < 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(stack.GetWorkSpeed())
}

func gearBillsFromStack(stack *o.BillStack, benchDef string, recipes []policy.GearRecipe, catalog *DefinitionCatalog) ([]policy.GearBill, error) {
	if len(stack.Bills) > 15 {
		return nil, contract("bill stack exceeds bound")
	}
	products := map[string][]policy.Resource{}
	gestation := map[string]bool{}
	for _, r := range recipes {
		products[r.Definition] = r.Products
		gestation[r.Definition] = r.MechKind != ""
	}
	out := make([]policy.GearBill, 0, len(stack.Bills))
	for _, bill := range stack.Bills {
		if bill == nil || bill.Recipe == nil || validID(bill.Recipe.GetDefName()) != nil {
			return nil, contract("invalid gear bill identity")
		}
		row := policy.GearBill{ID: bill.GetId(), Recipe: bill.Recipe.GetDefName()}
		role, err := catalog.RecipeRole(row.Recipe)
		if err != nil {
			return nil, err
		}
		row.Role = role
		if bill.Suspended != nil && bill.Finished != nil {
			row.Active = domain.Known(!bill.GetSuspended() && !bill.GetFinished())
		}
		if bill.RepeatMode != nil {
			row.Finite = domain.Known(RepeatModeName(bill.GetRepeatMode()) == "RepeatCount")
		}
		// An unrestricted bill carries no worker reference.
		if optionalRef(bill.Worker) {
			row.Worker = domain.Known(bill.GetWorker().GetId())
		}
		if list, known := products[bill.Recipe.GetDefName()]; known {
			row.Products = list
		}
		row.Spent = bill.GetFinished()
		if gestation[row.Recipe] {
			row.Kind = policy.LedgerMechGestation
		}
		row.Spec = billOrderSpec(bill, benchDef)
		out = append(out, row)
	}
	return out, nil
}

// billOrderSpec is a bill read back as the work ledger's order spec, in the
// terms native identifies a bill by (recipe, ingredient filter, worker pin,
// repeat mode and count). It is unknown when the readback lacks the repeat
// mode, its count or whether the ingredient filter is the recipe's default.
func billOrderSpec(bill *o.BillState, benchDef string) domain.Fact[policy.OrderSpec] {
	spec := policy.OrderSpec{Recipe: bill.Recipe.GetDefName(), BenchKind: benchDef}
	if bill.RepeatMode == nil || bill.DefaultIngredients == nil {
		return domain.Unknown[policy.OrderSpec]()
	}
	switch RepeatModeName(bill.GetRepeatMode()) {
	case "Forever":
		spec.Mode = domain.ButcherForever
	case "RepeatCount":
		if bill.RepeatCount == nil {
			return domain.Unknown[policy.OrderSpec]()
		}
		spec.Mode, spec.Target = domain.GearBatch, bill.GetRepeatCount()
	case "TargetCount":
		if bill.TargetCount == nil {
			return domain.Unknown[policy.OrderSpec]()
		}
		spec.Mode, spec.Target = domain.StockTarget, bill.GetTargetCount()
	default:
		return domain.Unknown[policy.OrderSpec]()
	}
	if optionalRef(bill.Worker) {
		spec.Worker = bill.GetWorker().GetId()
	}
	if !bill.GetDefaultIngredients() {
		spec.Ingredients = append([]string(nil), bill.GetIngredientFilter().GetAllowedDefNames()...)
	}
	return domain.Known(spec)
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
	names := map[string]bool{}
	for _, n := range defNames {
		if validID(n) != nil || names[n] {
			return nil, Result{}, contract("invalid supply stock def name")
		}
		names[n] = true
	}
	seenOut := map[string]bool{}
	out := make([]policy.Stock, 0, len(defNames))
	var raw Result
	// Native refuses a filter of more than maxStockFilterDefNames names
	// (NativeSuppliesObservationTools), so a long recipe list reads in chunks.
	for start := 0; start < len(defNames); start += maxStockFilterDefNames {
		chunk := defNames[start:min(start+maxStockFilterDefNames, len(defNames))]
		var err error
		if raw, err = client.readSupplyStockChunk(ctx, identity, chunk, seenOut, &out); err != nil {
			return nil, raw, err
		}
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

// maxStockFilterDefNames is native ListSupplies' StockFilter.DefNames cap.
const maxStockFilterDefNames = 256

func (client *Client) readSupplyStockChunk(ctx context.Context, identity *c.Identity, defNames []string, seenOut map[string]bool, out *[]policy.Stock) (Result, error) {
	names := map[string]bool{}
	for _, n := range defNames {
		names[n] = true
	}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{DefNames: append([]string(nil), defNames...), Ownership: o.StockOwnership_STOCK_OWNERSHIP_OURS.Enum()}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return raw, err
	}
	var snapshot *o.SuppliesSnapshot
	switch v := reply.Outcome.(type) {
	case *o.ListSuppliesReply_Observed:
		snapshot = v.Observed
	case *o.ListSuppliesReply_Unavailable:
		return raw, unavailable(v.Unavailable, raw)
	case *o.ListSuppliesReply_Failure:
		return raw, failure(v.Failure, raw)
	default:
		return raw, contract("missing supplies outcome")
	}
	if snapshot == nil || ValidateContext(snapshot.Context) != nil || !sameIdentity(snapshot.Context.Identity, identity) {
		return raw, contract("invalid supplies context")
	}
	if len(snapshot.Stocks) > len(defNames) {
		return raw, contract("incomplete supplies census")
	}
	for _, row := range snapshot.Stocks {
		if row == nil || row.Definition == nil || validID(row.Definition.GetDefName()) != nil || !names[row.Definition.GetDefName()] || seenOut[row.Definition.GetDefName()] {
			return raw, contract("invalid resource stock row")
		}
		seenOut[row.Definition.GetDefName()] = true
		stock := policy.Stock{Resource: policy.Resource(row.Definition.GetDefName())}
		if row.OursUnforbidden != nil && row.GetOursUnforbidden() >= 0 {
			stock.Available = domain.Known(row.GetOursUnforbidden())
		}
		*out = append(*out, stock)
	}
	return raw, nil
}

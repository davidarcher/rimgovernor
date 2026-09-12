package domain

import "errors"

const ProductionBillAction ActionKind = "production_bill"

type BillMode string

const (
	FoodTarget     BillMode = "food_target"
	ButcherForever BillMode = "butcher_forever"
)

// ProductionBill adds one native bill without editing or replacing existing bills.
type ProductionBill struct {
	bench, recipe, token string
	mode                 BillMode
	target               int32
}

func NewProductionBill(bench, recipe, token string, mode BillMode, target int32) (ProductionBill, error) {
	if !validID(bench) || !validID(recipe) || !validID(token) || (mode != FoodTarget && mode != ButcherForever) || mode == FoodTarget && (target < 1 || target > 10000 || recipe == "ButcherCorpseFlesh") || mode == ButcherForever && (recipe != "ButcherCorpseFlesh" || target != 0) {
		return ProductionBill{}, errors.New("invalid production bill")
	}
	return ProductionBill{bench, recipe, token, mode, target}, nil
}
func (b ProductionBill) Bench() string       { return b.bench }
func (b ProductionBill) Recipe() string      { return b.recipe }
func (b ProductionBill) BeforeToken() string { return b.token }
func (b ProductionBill) Mode() BillMode      { return b.mode }
func (b ProductionBill) Target() int32       { return b.target }
func NewProductionBillAction(id ActionID, b ProductionBill) (Action, error) {
	canonical, err := NewProductionBill(b.bench, b.recipe, b.token, b.mode, b.target)
	if !validID(string(id)) || err != nil || canonical != b {
		return Action{}, errors.New("invalid production bill action")
	}
	return Action{id: id, kind: ProductionBillAction, bill: b}, nil
}
func (a Action) ProductionBill() (ProductionBill, bool) {
	return a.bill, a.kind == ProductionBillAction
}

package domain

import (
	"encoding/json"
	"errors"
	"sort"
)

const ProductionBillAction ActionKind = "production_bill"

type BillMode string

const (
	FoodTarget     BillMode = "food_target"
	ButcherForever BillMode = "butcher_forever"
	// StockTarget is the generic "keep at least Target units of this recipe's
	// output in stock" bill, the same pause-when-satisfied/unpause-below-half
	// shape FoodTarget uses, reused as-is by GearProduce, MaintainResource-*
	// and MaintainMedicalReserves rather than inventing a mode per consumer.
	// Unlike FoodTarget it carries no food-specific recipe exclusion.
	StockTarget BillMode = "stock_target"
)

// ProductionBill adds one native bill without editing or replacing existing bills.
type ProductionBill struct {
	bench, recipe, token string
	mode                 BillMode
	target               int32
	ingredients          string
}

func NewProductionBill(bench, recipe, token string, mode BillMode, target int32, ingredients ...string) (ProductionBill, error) {
	if !validID(bench) || !validID(recipe) || !validID(token) || (mode != FoodTarget && mode != ButcherForever && mode != StockTarget) || mode == FoodTarget && (target < 1 || target > 10000 || recipe == "ButcherCorpseFlesh") || mode == ButcherForever && (recipe != "ButcherCorpseFlesh" || target != 0) || mode == StockTarget && (target < 1 || target > 10000) {
		return ProductionBill{}, errors.New("invalid production bill")
	}
	if len(ingredients) > 256 || mode == ButcherForever && len(ingredients) > 0 {
		return ProductionBill{}, errors.New("invalid bill ingredient override")
	}
	filter := ""
	if len(ingredients) > 0 {
		rows := append([]string(nil), ingredients...)
		sort.Strings(rows)
		for i, name := range rows {
			if !validID(name) || i > 0 && name == rows[i-1] {
				return ProductionBill{}, errors.New("invalid bill ingredient")
			}
		}
		data, _ := json.Marshal(rows)
		filter = string(data)
	}
	return ProductionBill{bench, recipe, token, mode, target, filter}, nil
}

// Ingredients is the exact allowed definition set; empty preserves recipe defaults.
func (b ProductionBill) Ingredients() []string {
	var rows []string
	if b.ingredients != "" {
		_ = json.Unmarshal([]byte(b.ingredients), &rows)
	}
	return rows
}
func (b ProductionBill) Bench() string       { return b.bench }
func (b ProductionBill) Recipe() string      { return b.recipe }
func (b ProductionBill) BeforeToken() string { return b.token }
func (b ProductionBill) Mode() BillMode      { return b.mode }
func (b ProductionBill) Target() int32       { return b.target }
func NewProductionBillAction(id ActionID, b ProductionBill) (Action, error) {
	canonical, err := NewProductionBill(b.bench, b.recipe, b.token, b.mode, b.target, b.Ingredients()...)
	if !validID(string(id)) || err != nil || canonical != b {
		return Action{}, errors.New("invalid production bill action")
	}
	return Action{id: id, kind: ProductionBillAction, bill: b}, nil
}
func (a Action) ProductionBill() (ProductionBill, bool) {
	return a.bill, a.kind == ProductionBillAction
}

package domain

import (
	"errors"
	"math"
	"strings"
)

// TradeTarget is one priority-ordered economic target: the definition the
// player named, the stock level it should be kept at, and the per-deal
// quantity and price limits either side of that level. It is the Go form of
// colony_plan.py's TradeTarget, with its field bounds carried on Validate.
type TradeTarget struct {
	Item         string
	Stock        int64
	MaxBuy       int64
	MaxSell      int64
	MaxBuyPrice  float64
	MinSellPrice float64
}

// TradeEconomicPolicy is colony_plan.py's TradePolicy: a bounded,
// priority-ordered list of case-insensitively unique targets plus the silver
// the colony always keeps. Order is meaning, not presentation: it is the
// purchase priority SelectTrade spends its budget in.
type TradeEconomicPolicy struct {
	Targets       []TradeTarget
	SilverReserve int64
}

const (
	tradePolicyMaximumTargets = 30
	tradeTargetMaximumCount   = 100000
	tradeTargetMaximumName    = 160
)

func (t TradeTarget) validate() error {
	if t.Item == "" || len(t.Item) > tradeTargetMaximumName || strings.HasPrefix(t.Item, "#") {
		return errors.New("economic target needs an observed definition, not a session row index")
	}
	if t.Stock < 0 || t.Stock > tradeTargetMaximumCount || t.MaxBuy < 0 || t.MaxBuy > tradeTargetMaximumCount || t.MaxSell < 0 || t.MaxSell > tradeTargetMaximumCount {
		return errors.New("economic target quantities out of range")
	}
	if !finite(t.MaxBuyPrice) || t.MaxBuyPrice < 0 || !finite(t.MinSellPrice) || t.MinSellPrice < 0 {
		return errors.New("economic target prices out of range")
	}
	return nil
}

// Validate mirrors TradePolicy's own pydantic bounds and its unique_targets
// validator exactly.
func (p TradeEconomicPolicy) Validate() error {
	if len(p.Targets) == 0 || len(p.Targets) > tradePolicyMaximumTargets {
		return errors.New("economic policy needs one to thirty targets")
	}
	if p.SilverReserve < 0 || p.SilverReserve > tradeTargetMaximumCount {
		return errors.New("economic silver reserve out of range")
	}
	seen := make(map[string]bool, len(p.Targets))
	for _, target := range p.Targets {
		if err := target.validate(); err != nil {
			return err
		}
		folded := strings.ToLower(target.Item)
		if seen[folded] {
			return errors.New("use unique native definitions for economic targets")
		}
		seen[folded] = true
	}
	return nil
}

func finite(v float64) bool { return !math.IsNaN(v) && !math.IsInf(v, 0) }

package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

type PowerBuilding struct {
	BaseW, OutputW                            domain.Fact[float64]
	Powered, Connected, Forbidden, SwitchedOn domain.Fact[bool]
	Network                                   domain.Fact[string]
}

// PowerCoverage evaluates each consumer's own network. Spare generation on a
// different network cannot cover it; disconnected or unpowered consumers fail.
func PowerCoverage(rows domain.Fact[[]PowerBuilding]) (required domain.Fact[bool], headroom domain.Fact[float64], disabled domain.Fact[bool]) {
	buildings, known := rows.Value()
	if !known {
		return
	}
	consumers := []PowerBuilding{}
	for _, b := range buildings {
		base, known := b.BaseW.Value()
		if !known {
			return
		}
		if base < 0 {
			consumers = append(consumers, b)
		}
	}
	required = domain.Known(len(consumers) > 0)
	if len(consumers) == 0 {
		return required, domain.Known(float64(0)), domain.Known(false)
	}
	minimum, haveMinimum, powerKnown := float64(0), false, true
	disabledValue, disabledKnown := false, true
	for _, consumer := range consumers {
		on, onKnown := consumer.Powered.Value()
		forbidden, fk := consumer.Forbidden.Value()
		switched, sk := consumer.SwitchedOn.Value()
		if onKnown && on || fk && forbidden || sk && !switched {
			// This consumer is not an enabled, unpowered recovery target.
		} else if onKnown && fk && sk {
			disabledValue = true
		} else {
			disabledKnown = false
		}
		connected, ck := consumer.Connected.Value()
		value := float64(-1)
		if ck && !connected || onKnown && !on {
			// Known failure dominates unknown output or network identity.
		} else if !ck || !onKnown {
			powerKnown = false
			continue
		} else {
			network, nk := consumer.Network.Value()
			if !nk {
				powerKnown = false
				continue
			}
			value = 0
			for _, b := range buildings {
				connected, known := b.Connected.Value()
				if known && !connected {
					continue
				}
				net, nk := b.Network.Value()
				if !known || !nk {
					powerKnown = false
					continue
				}
				if net == network {
					output, ok := b.OutputW.Value()
					if !ok {
						powerKnown = false
					} else {
						value += output
					}
				}
			}
		}
		if !haveMinimum || value < minimum {
			minimum = value
			haveMinimum = true
		}
	}
	if powerKnown && haveMinimum {
		headroom = domain.Known(minimum)
	}
	if disabledValue || disabledKnown {
		disabled = domain.Known(disabledValue)
	}
	return
}

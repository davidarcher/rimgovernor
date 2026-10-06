package supplysim

// ShockKind is what a shock does to a source or a stock.
type ShockKind string

const (
	// ScaleCapacity multiplies a source's capacity by Factor.
	ScaleCapacity ShockKind = "ScaleCapacity"
	// DestroyStock removes the Factor fraction (0..1) of a source's stock (a
	// crop's planted cells, a herd's animals), or of a world good when Source
	// is empty.
	DestroyStock ShockKind = "DestroyStock"
	// RemoveSource ends a source for good.
	RemoveSource ShockKind = "RemoveSource"
	// PauseGrowth stops regeneration and crop growth for Days days.
	PauseGrowth ShockKind = "PauseGrowth"
)

// Shock applies at the start of Day, before that day's deliveries.
type Shock struct {
	Day    int
	Kind   ShockKind
	Source string
	Good   Good // DestroyStock on world stock
	Factor float64
	Days   int
}

func (s Shock) apply(r *runState) {
	if s.Kind == DestroyStock && s.Source == "" {
		r.stock[s.Good] *= 1 - clamp01(s.Factor)
		return
	}
	src := r.source(s.Source)
	if src == nil {
		return
	}
	switch s.Kind {
	case ScaleCapacity:
		src.capScale *= s.Factor
	case DestroyStock:
		f := 1 - clamp01(s.Factor)
		src.Stock *= f
		if src.Crop != nil {
			src.Crop.Planted *= f
		}
	case RemoveSource:
		src.Open, src.removed = false, true
	case PauseGrowth:
		if until := s.Day + s.Days; until > src.pausedUntil {
			src.pausedUntil = until
		}
	}
}

func clamp01(v float64) float64 {
	switch {
	case v < 0:
		return 0
	case v > 1:
		return 1
	}
	return v
}

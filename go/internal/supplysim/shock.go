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
	// StopWork stops a source delivering for Days days (caravan absent).
	StopWork ShockKind = "StopWork"
	// Threat blocks Safe sources (loot, salvage) for Days days.
	Threat ShockKind = "Threat"
	// PowerLoss cuts World.Power to the Factor fraction for Days days.
	PowerLoss ShockKind = "PowerLoss"
	// StorageFull caps Good's stock at Factor units for Days days; deliveries
	// beyond the headroom are not made.
	StorageFull ShockKind = "StorageFull"
	// SurgeDemand multiplies the Good's demand by Factor for Days days.
	SurgeDemand ShockKind = "SurgeDemand"
	// Requisition opens a one-off Build of Factor units of Good (steel needed
	// for weapons).
	Requisition ShockKind = "Requisition"
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
	until := s.Day + s.Days
	switch s.Kind {
	case DestroyStock:
		if s.Source == "" {
			r.stock[s.Good] *= 1 - clamp01(s.Factor)
			return
		}
	case Threat:
		r.threatUntil = max(r.threatUntil, until)
		return
	case PowerLoss:
		r.powerFactor, r.powerUntil = clamp01(s.Factor), until
		return
	case StorageFull:
		r.capUntil[s.Good], r.capLimit[s.Good] = until, s.Factor
		return
	case SurgeDemand:
		r.surge[s.Good] = surge{s.Factor, until}
		return
	case Requisition:
		r.builds = append(r.builds, &buildState{Build: Build{Name: "requisition " + string(s.Good), Day: s.Day,
			Costs: []Yield{{s.Good, s.Factor}}}, left: map[Good]float64{s.Good: s.Factor}})
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
		src.pausedUntil = max(src.pausedUntil, until)
	case StopWork:
		src.offUntil = max(src.offUntil, until)
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

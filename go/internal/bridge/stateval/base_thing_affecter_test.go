package stateval

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Every ConditionalStatAffecter class, hosted by a precept (the precept loop
// is not Biotech-gated, so the class's own Biotech check is what the cases
// without Biotech show).
func TestBaseConditionalAffecterClasses(t *testing.T) {
	cases := []struct {
		name    string
		class   string
		biotech bool
		edit    func(*StatContext)
		want    float32
		err     string
	}{
		{name: "in space", class: "InSpace", edit: func(c *StatContext) {
			c.Base.SpawnedOrParentSpawned, c.Base.InSpaceLayer = Some(true), Some(true)
		}, want: 11},
		{name: "not in space layer", class: "InSpace", edit: func(c *StatContext) {
			c.Base.SpawnedOrParentSpawned, c.Base.InSpaceLayer = Some(true), Some(false)
		}, want: 10},
		{name: "not spawned is not in space", class: "InSpace", edit: func(c *StatContext) {
			c.Base.SpawnedOrParentSpawned = Some(false)
		}, want: 10},
		{name: "not in space", class: "NotInSpace", edit: func(c *StatContext) {
			c.Base.SpawnedOrParentSpawned = Some(false)
		}, want: 11},
		{name: "space layer unobserved", class: "InSpace", edit: func(c *StatContext) {
			c.Base.SpawnedOrParentSpawned = Some(true)
		}, err: "space layer"},
		{name: "spawned unobserved", class: "InSpace", edit: func(*StatContext) {}, err: "parent is spawned"},
		{name: "sunlight", class: "InSunlight", biotech: true, edit: func(c *StatContext) {
			c.Spawned, c.Base.InSunlight = Some(true), Some(true)
		}, want: 11},
		{name: "shade", class: "InSunlight", biotech: true, edit: func(c *StatContext) {
			c.Spawned, c.Base.InSunlight = Some(true), Some(false)
		}, want: 10},
		{name: "unspawned is not in sunlight", class: "InSunlight", biotech: true, edit: func(c *StatContext) {
			c.Spawned = Some(false)
		}, want: 10},
		{name: "sunlight unobserved", class: "InSunlight", biotech: true, edit: func(c *StatContext) {
			c.Spawned = Some(true)
		}, err: "sunlight"},
		{name: "spawned unobserved for sunlight", class: "InSunlight", biotech: true, edit: func(*StatContext) {}, err: "spawned"},
		{name: "sunlight without Biotech", class: "InSunlight", edit: func(c *StatContext) {
			c.Spawned, c.Base.InSunlight = Some(true), Some(true)
		}, want: 10},
		{name: "unclothed pawn", class: "Unclothed", biotech: true, edit: func(*StatContext) {}, want: 11},
		{name: "unclothed in clothes", class: "Unclothed", biotech: true, edit: func(c *StatContext) { wearing("Apparel_Parka")(c.Pawn) }, want: 10},
		{name: "clothed in clothes", class: "Clothed", biotech: true, edit: func(c *StatContext) { wearing("Apparel_Parka")(c.Pawn) }, want: 11},
		{name: "clothed naked", class: "Clothed", biotech: true, edit: func(*StatContext) {}, want: 10},
		{name: "unclothed without Biotech", class: "Unclothed", edit: func(*StatContext) {}, want: 10},
		{name: "child", class: "Child", biotech: true, edit: func(c *StatContext) { c.Pawn.Base.Developmental = Some[int32](4) }, want: 11},
		{name: "adult", class: "Child", biotech: true, edit: func(c *StatContext) { c.Pawn.Base.Developmental = Some[int32](8) }, want: 10},
		{name: "child stage unobserved", class: "Child", biotech: true, edit: func(*StatContext) {}, err: "developmental stage"},
		{name: "child without Biotech", class: "Child", edit: func(c *StatContext) { c.Pawn.Base.Developmental = Some[int32](4) }, want: 10},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			p := &d.PreceptDef{DefName: "P", ConditionalStatAffecters: []*d.Opt_ConditionalStatAffecterAny{
				conditional(c.class, []*d.Opt_StatModifier{mod(testStat, 1)}, nil),
			}}
			r := newBaseRig(t, baseOpts{
				biotech: c.biotech,
				parka:   clothedParka,
				defs:    withDefs(lifeStages(nil, nil), func(s *d.DefSets) { s.PreceptDefs = append(s.PreceptDefs, p) }),
			})
			ctx := statedPawn(func(ps *PawnState) { ps.Body.Ideo = Some(IdeoState{Present: true, Precepts: []string{"P"}}) })
			c.edit(ctx)
			if c.err != "" {
				r.fails(ctx, c.err)
				return
			}
			checkValue(t, r.at(ctx), c.want)
		})
	}
}

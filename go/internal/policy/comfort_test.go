package policy

import (
	"testing"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func comfortFacilities() ComfortObservation {
	return ComfortObservation{People:[]PawnID{"a","b"},
		Surfaces:[]DiningSurface{{ID:"table",Adjacent:[]domain.Cell{{X:1,Z:2}}}},
		Dining:[]ComfortFacility{{ID:"chair",AccessibleTo:[]PawnID{"a","b"}}},
		Recreation:[]ComfortFacility{{ID:"hoop",AccessibleTo:[]PawnID{"a","b"}}}}
}

func TestComfortCapacityAndObservedUseAreIndependent(t *testing.T) {
	v:=ComfortObservation{People:[]PawnID{"a","b"}}
	h:=ComfortHistory{}
	check:=func(want ComfortMethod, fraction float64) ComfortReview {
		t.Helper();r,err:=ReviewComfort(domain.Known(v),h,10);if err!=nil {t.Fatal(err)}
		method,err:=SelectComfortMethod(v,r)
		if err!=nil || method!=want || r.Deficit()!=domain.Known(fraction) {t.Fatal(r,method,err)}
		h=r.History;return r
	}
	check(ComfortBuildTable,1)
	v.Surfaces=[]DiningSurface{{ID:"table",Adjacent:[]domain.Cell{{X:1,Z:2}}}}
	check(ComfortBuildChair,1)
	v.Dining=[]ComfortFacility{{ID:"chair",AccessibleTo:[]PawnID{"a","b"}}}
	check(ComfortBuildRecreation,1)
	v.Recreation=[]ComfortFacility{{ID:"hoop",AccessibleTo:[]PawnID{"a","b"}}}
	r:=check(ComfortWait,1)
	if r.Recovered()!=domain.Known(false) {t.Fatal("construction certified use",r)}
	v.Dining[0].Users=[]PawnID{"a"}
	check(ComfortWait,.5)
	v.Dining[0].Users=nil;v.Recreation[0].Users=[]PawnID{"b"}
	r=check(ComfortNoMethod,0)
	if r.Recovered()!=domain.Known(true) || r.History.Dining.Facility!="chair" || r.History.Recreation.Facility!="hoop" {t.Fatal(r)}
	v.Recreation[0].Users=nil
	check(ComfortNoMethod,0)
}

func TestComfortPreservesAccessAndUnknownProof(t *testing.T) {
	v:=comfortFacilities()
	h:=ComfortHistory{Dining:ComfortUse{Facility:"chair",Tick:7},Recreation:ComfortUse{Facility:"hoop",Tick:8}}
	r,err:=ReviewComfort(domain.Unknown[ComfortObservation](),h,9)
	if err!=nil || r.History!=h || r.Recovered()!=domain.Unknown[bool]() {t.Fatal(r,err)}
	v.Dining[0].AccessibleTo=[]PawnID{"a"}
	r,err=ReviewComfort(domain.Known(v),h,9);if err!=nil {t.Fatal(err)}
	m,err:=SelectComfortMethod(v,r)
	if err!=nil || m!=ComfortAccessBlocked || r.MissingDining!=1 || r.Recovered()!=domain.Known(false) {t.Fatal(r,m,err)}
	v=comfortFacilities();v.Dining[0].ID="replacement"
	r,err=ReviewComfort(domain.Known(v),h,9)
	if err!=nil || r.Dining!=ComfortUseNeeded {t.Fatal("old furniture use transferred",r,err)}
	v.Dining[0].Users=[]PawnID{"a"};v.Dining[0].AccessibleTo=[]PawnID{"b"}
	r,err=ReviewComfort(domain.Known(v),h,9)
	if err!=nil || r.History.Dining.Facility!="chair" {t.Fatal("inaccessible use accepted",r,err)}
	if _,err=ReviewComfort(domain.Known(v),h,6);err==nil {t.Fatal("future use accepted")}
	if _,err=ReviewComfort(domain.Known(v),ComfortHistory{Dining:ComfortUse{Tick:1}},9);err==nil {t.Fatal("unbound use accepted")}
}

func TestComfortRejectsIncompleteIdentitySets(t *testing.T) {
	for _,mutate:=range []func(*ComfortObservation){
		func(v *ComfortObservation){v.People=append(v.People,"a")},
		func(v *ComfortObservation){v.Dining[0].Users=[]PawnID{"outsider"}},
		func(v *ComfortObservation){v.Dining[0].AccessibleTo=[]PawnID{"a","a"}},
		func(v *ComfortObservation){v.Dining=append(v.Dining,v.Dining[0])},
		func(v *ComfortObservation){v.Surfaces[0].Adjacent=append(v.Surfaces[0].Adjacent,v.Surfaces[0].Adjacent[0])},
	}{v:=comfortFacilities();mutate(&v);if _,err:=ReviewComfort(domain.Known(v),ComfortHistory{},1);err==nil {t.Fatal("invalid comfort census accepted",v)}}
}

package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestIdeoligionCreationRefusesMalformedDesign(t *testing.T) {
	for _, design := range []*c.IdeoligionDesign{{}, {Memes: []string{"Meme"}}, {Memes: []string{"Meme", "Meme"}, Fluid: proto.Bool(true)}, {Memes: []string{"Meme"}, Precepts: []string{"Precept", "Precept"}, Fluid: proto.Bool(true)}} {
		request := pbNewColonyRequest()
		request.Spec.Ideoligion = design
		if err := ValidateNewColonyRequest(request); err == nil {
			t.Fatal("malformed design accepted")
		}
	}
}

func TestIdeoligionReformWireCarriesExpectedDesignAndCount(t *testing.T) {
	old := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Old"}, Fluid: true}
	next := domain.IdeoligionDesign{Memes: old.Memes, Precepts: []string{"New"}, Fluid: true}
	v, _ := domain.NewIdeoligionReform("Ideo_1", old, next, 3)
	a, _ := domain.NewIdeoligionReformAction("reform-0", v)
	wire, err := IntentAction("plan/1", a)
	if err != nil {
		t.Fatal(err)
	}
	i := wire.GetIdeoligionReform()
	if i.GetIdeoId() != "Ideo_1" || i.GetExpectedReformCount() != 3 || i.Expected.Precepts[0] != "Old" || i.Design.Precepts[0] != "New" {
		t.Fatalf("lost expected state: %v", wire)
	}
}

func TestIdeoligionDevelopmentDoesNotGuessMissingState(t *testing.T) {
	v := ideologySnapshot()
	got, err := DecodeIdeology(v, pbIdentity(), ideologyCatalogRows())
	if err != nil {
		t.Fatal(err)
	}
	if _, known := got.Facts.Development.Value(); known {
		t.Fatal("missing reform facts became eligible")
	}
	v.Fluid, v.CanReform = proto.Bool(true), proto.Bool(true)
	v.DevelopmentPoints, v.ReformCount, v.NextReformPoints = proto.Int32(10), proto.Int32(0), proto.Int32(10)
	got, err = DecodeIdeology(v, pbIdentity(), ideologyCatalogRows())
	if err != nil {
		t.Fatal(err)
	}
	if d, ok := got.Facts.Development.Value(); !ok || !d.CanReform {
		t.Fatal("eligible reform not decoded")
	}
	v.DevelopmentPoints = proto.Int32(9)
	if _, err = DecodeIdeology(v, pbIdentity(), ideologyCatalogRows()); err == nil {
		t.Fatal("inconsistent eligibility accepted")
	}
}

package policy

import "testing"

func TestBlockCropOrderAvoidsNeighbourCrop(t *testing.T) {
	rice := FieldBlockOption{Crop: CropChoice{Name: "Plant_Rice"}, Needed: 4}
	corn := FieldBlockOption{Crop: CropChoice{Name: "Plant_Corn"}, Needed: 6}
	got := BlockCropOrder([]FieldBlockOption{rice, corn}, map[string]bool{"Plant_Rice": true})
	if got[0].Crop.Name != "Plant_Corn" || got[1].Crop.Name != "Plant_Rice" {
		t.Fatalf("order = %+v", got)
	}
	if got := BlockCropOrder([]FieldBlockOption{rice, corn}, nil); got[0].Crop.Name != "Plant_Rice" {
		t.Fatalf("no neighbours reordered: %+v", got)
	}
}

func TestBlockCropOrderAllowsSoleViableCrop(t *testing.T) {
	rice := FieldBlockOption{Crop: CropChoice{Name: "Plant_Rice"}, Needed: 4}
	if got := BlockCropOrder([]FieldBlockOption{rice}, map[string]bool{"Plant_Rice": true}); len(got) != 1 || got[0].Crop.Name != "Plant_Rice" {
		t.Fatalf("order = %+v", got)
	}
}

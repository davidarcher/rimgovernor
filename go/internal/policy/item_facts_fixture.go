package policy

// CoreItemFacts is a small slice of Core's item numbers shaped like the
// catalog's ItemFacts, for tests and fixtures that stand in for a loaded
// catalog; planners never read it.
func CoreItemFacts() ItemFacts {
	items := ItemFacts{
		Market: map[Resource]float64{
			"Silver": 1, "Steel": 1.9, "Plasteel": 9, "Uranium": 6, "Gold": 10, "Jade": 5, "WoodLog": 1.2,
			"BlocksSandstone": 0.9, "BlocksGranite": 0.9, "BlocksLimestone": 0.9, "BlocksSlate": 0.9, "BlocksMarble": 0.9,
			"ComponentIndustrial": 32, "MedicineHerbal": 18, "MedicineIndustrial": 18, "MedicineUltratech": 120, "RawRice": 1.1,
		},
		Nutrition:      map[Resource]float64{"RawRice": 0.05},
		MedicalPotency: map[Resource]float64{"MedicineHerbal": 0.6, "MedicineIndustrial": 1, "MedicineUltratech": 1.6},
		StuffBeauty: map[Resource]float64{
			"WoodLog": 1, "Steel": 1, "Plasteel": 1, "Uranium": 0.5, "Silver": 2, "Gold": 4, "Jade": 2.5,
			"BlocksSandstone": 1.1, "BlocksGranite": 1, "BlocksLimestone": 1, "BlocksSlate": 1.1, "BlocksMarble": 1.35,
		},
		StuffCategories: map[Resource][]string{
			"WoodLog": {"Woody"}, "Steel": {"Metallic"}, "Plasteel": {"Metallic"}, "Uranium": {"Metallic"}, "Silver": {"Metallic"}, "Gold": {"Metallic"}, "Jade": {"Metallic"},
			"BlocksSandstone": {"Stony"}, "BlocksGranite": {"Stony"}, "BlocksLimestone": {"Stony"}, "BlocksSlate": {"Stony"}, "BlocksMarble": {"Stony"},
			"Cloth": {"Fabric"}, "Synthread": {"Fabric"}, "Hyperweave": {"Fabric"}, "Leather_Plain": {"Leathery"}, "Leather_Heavy": {"Leathery"},
		},
		AcceptedStuff: map[Resource][]string{},
		Categories:    map[Resource][]string{"RawRice": {"PlantFoodRaw"}},
		Currency:      "Silver",
		// Drugs are Core's, in the catalog's preference order.
		Drugs: []Drug{
			{Def: "Beer", Chemical: "Alcohol", Social: true}, {Def: "SmokeleafJoint", Chemical: "Smokeleaf", Social: true}, {Def: "PsychiteTea", Chemical: "Psychite", Social: true},
			{Def: "Flake", Chemical: "Psychite"}, {Def: "GoJuice", Chemical: "GoJuice", Combat: true}, {Def: "Luciferium", Chemical: "Luciferium"},
			{Def: "WakeUp", Chemical: "WakeUp"}, {Def: "Yayo", Chemical: "Psychite", Combat: true},
		},
		Chemicals: map[string]Chemical{
			"Alcohol": {Weanable: true}, "Smokeleaf": {Weanable: true}, "Psychite": {Weanable: true},
			"GoJuice": {Weanable: true}, "WakeUp": {Weanable: true}, "Luciferium": {},
		},
		Prevention: &Prevention{Drug: "Penoxycyline", Days: 5, Diseases: []string{"Malaria", "Plague"}},
	}
	items.AcceptedStuff["Sandbags"] = []string{"Fabric", "Leathery"}
	for _, def := range []Resource{"Bed", "DoubleBed", "RoyalBed", "SculptureSmall", "SculptureLarge", "SculptureGrand"} {
		items.AcceptedStuff[def] = []string{"Metallic", "Woody", "Stony"}
	}
	return items
}

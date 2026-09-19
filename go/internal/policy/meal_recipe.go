package policy

// Every slot is required; alternatives within a slot are interchangeable.
// A fine meal needs (meat OR animalProduct) AND vegetable, not all three.
type FoodIngredientClass string

const (
	IngredientMeat          FoodIngredientClass = "meat"
	IngredientVegetable     FoodIngredientClass = "vegetable"
	IngredientAnimalProduct FoodIngredientClass = "animalProduct"
	IngredientAny           FoodIngredientClass = "any"
)

type FoodIngredientSlot struct {
	Alternatives []FoodIngredientClass
}

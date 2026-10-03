#nullable enable
using System.Linq;
using RimWorld;
using Verse;
namespace HomeBridge.BridgeTools {
 // The roles a recipe plays, read from the recipe def itself (#1721): the
 // game's own worker class and ingredient/product shape, never a defName.
 internal static class NativeRecipeRoles {
  // Butchering a corpse for its flesh: the recipe the game gives the
  // butcher-animals worker counter (the one that counts the butchered pawn).
  internal static bool ButcherFlesh(RecipeDef? recipe)=>recipe?.WorkerCounter is RecipeWorkerCounter_ButcherAnimals;
  // Destroying corpses: a recipe that consumes corpses and yields nothing,
  // special products included, and is neither mech resurrection nor a
  // gestation recipe.
  internal static bool Cremation(RecipeDef? recipe)=>recipe!=null
   &&recipe.products.Count==0&&recipe.specialProducts==null&&!recipe.mechResurrection&&recipe.gestationCycles==0
   &&recipe.ingredients.Any(i=>i.filter.AllowedThingDefs.Any(d=>d.IsCorpse));
  internal static bool Corpse(RecipeDef? recipe)=>ButcherFlesh(recipe)||Cremation(recipe);
  // The same roles for the recipe a wire command names; an unknown recipe has none.
  internal static RecipeDef? Named(string name)=>DefDatabase<RecipeDef>.GetNamedSilentFail(name);
  // The prepared survival meal and the brewing wort the game names by constant.
  internal static bool Produces(RecipeDef recipe,ThingDef product)=>recipe.products.Count>0&&recipe.products.All(p=>p.thingDef==product);
 }
}

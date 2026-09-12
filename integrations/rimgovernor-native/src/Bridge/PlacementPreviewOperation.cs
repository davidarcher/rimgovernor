#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    internal sealed class PlacementQuery
    {
        internal PlacementQuery(string defName, int x, int z, string rotation, string? stuff)
        { DefName = defName; X = x; Z = z; Rotation = rotation; Stuff = stuff; }
        internal string DefName { get; }
        internal int X { get; }
        internal int Z { get; }
        internal string Rotation { get; }
        internal string? Stuff { get; }
    }

    internal abstract class PlacementPreviewResult
    {
        public abstract bool success { get; }
    }

    internal enum PlacementFailureKind { InvalidRequest, NotFound, Unsupported, Unavailable, CapacityExceeded }

    internal sealed class PlacementPreviewFailure : PlacementPreviewResult
    {
        internal PlacementPreviewFailure(string message, PlacementFailureKind kind = PlacementFailureKind.Unavailable) { error = PlacementPreviewOperation.Diagnostic(message); Kind = kind; }
        internal PlacementFailureKind Kind { get; }
        public override bool success => false;
        public string error { get; }
    }

    internal sealed class PlacementPreviewEvaluation : PlacementPreviewResult
    {
        public override bool success => true;
        public bool canPlace => rotations.Any(row => row.accepted);
        public bool madeFromStuff { get; internal set; }
        public string passability { get; internal set; } = "";
        public bool isDoor { get; internal set; }
        public bool researchFinished { get; internal set; }
        public bool buildableByPlayer { get; internal set; }
        public List<PlacementCost> costList { get; internal set; } = new List<PlacementCost>();
        public PlacementMaterials materials { get; internal set; } = new PlacementMaterials();
        public List<PlacementRotation> rotations { get; internal set; } = new List<PlacementRotation>();
    }

    internal sealed class PlacementCell
    {
        internal PlacementCell(IntVec3 cell) { x = cell.x; z = cell.z; }
        public int x { get; }
        public int z { get; }
    }

    internal sealed class PlacementCost
    {
        internal PlacementCost(ThingDef definition, int count)
        { Definition = definition; this.count = count; }
        internal ThingDef Definition { get; }
        public string defName => Definition.defName;
        public int count { get; }
    }

    internal sealed class PlacementMaterial
    {
        internal PlacementMaterial(string definition, int? count, int onMap = 0, int forbidden = 0, int claimed = 0)
        { defName = definition; available = count; OnMap = onMap; Forbidden = forbidden; Claimed = claimed; }
        internal int OnMap { get; }
        internal int Forbidden { get; }
        internal int Claimed { get; }
        public string defName { get; }
        public int? available { get; }
    }

    internal sealed class PlacementMaterials
    {
        public bool unreadable { get; internal set; }
        public List<PlacementMaterial> rows { get; } = new List<PlacementMaterial>();
    }

    internal sealed class PlacementBlocker
    {
        internal PlacementBlocker(Thing thing, bool wipes, bool haul, bool cancels)
        {
            Thing = thing; category = thing.def.category.ToString(); isBlueprint = thing is Blueprint;
            isFrame = thing is Frame; wouldBeWiped = wipes; MustBeHauledFirst = haul;
            frameWouldBeCancelled = cancels;
        }
        internal Thing Thing { get; }
        internal bool MustBeHauledFirst { get; }
        public string category { get; }
        public bool isBlueprint { get; }
        public bool isFrame { get; }
        public bool wouldBeWiped { get; }
        public bool frameWouldBeCancelled { get; }
    }

    internal sealed class PlacementRotation
    {
        public string rotation { get; internal set; } = "";
        public bool accepted { get; internal set; }
        public string reason { get; internal set; } = "";
        public bool? watchCellsAccessible { get; internal set; }
        public List<PlacementCell> occupiedCells { get; } = new List<PlacementCell>();
        public List<PlacementBlocker> blockingThings { get; } = new List<PlacementBlocker>();
        internal CellRect Rect { get; set; }
        internal bool IdenticalBlueprintExists { get; set; }
    }

    /// <summary>Main-thread native reads shared by the single and batch preview adapters.</summary>
    internal static class PlacementPreviewOperation
    {
        internal static string Diagnostic(string value)
        {
            var text = new StringBuilder();
            for (var index = 0; index < value.Length && text.Length < 4096; index++)
            {
                var current = value[index];
                if (char.IsHighSurrogate(current) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    if (text.Length > 4094) break;
                    text.Append(current).Append(value[++index]);
                }
                else text.Append(char.IsSurrogate(current) ? '\uFFFD' : current);
            }
            return text.ToString();
        }
        private static string DefinitionName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0
                || new UTF8Encoding(false, true).GetByteCount(value) > 256)
                throw new PlacementLimitException("Native identifier exceeds the preview contract");
            return value;
        }
        internal static readonly string[] RotationNames = { "north", "east", "south", "west" };

        internal static PlacementPreviewResult Evaluate(Map map, PlacementQuery candidate, bool godMode = false)
        {
            try
            {
                if (map == null) return new PlacementPreviewFailure("Load a colony first");
                if (string.IsNullOrWhiteSpace(candidate.DefName))
                    return new PlacementPreviewFailure("defName is required.", PlacementFailureKind.InvalidRequest);
                if (!TryResolveBuildable(candidate.DefName, out var definition, out _))
                    return new PlacementPreviewFailure("No ThingDef or TerrainDef matches \"" + candidate.DefName + "\" by exact defName.", PlacementFailureKind.NotFound);
                var center = new IntVec3(candidate.X, 0, candidate.Z);
                if (!center.InBounds(map)) return new PlacementPreviewFailure("Requested cell is not on this map.", PlacementFailureKind.InvalidRequest);
                if (!TryParseRotations(candidate.Rotation, out var rotations, out var error))
                    return new PlacementPreviewFailure(error!, PlacementFailureKind.InvalidRequest);
                DefinitionName(definition!.defName);
                if (!definition.BuildableByPlayer || definition.blueprintDef == null)
                    return new PlacementPreviewFailure("The requested definition is not available for ordinary blueprint construction.", PlacementFailureKind.Unsupported);
                var madeFromStuff = definition.MadeFromStuff;
                ThingDef? material = null;
                if (!string.IsNullOrEmpty(candidate.Stuff))
                {
                    if (!madeFromStuff)
                        return new PlacementPreviewFailure("The requested definition does not accept a stuff material.", PlacementFailureKind.InvalidRequest);
                    material = ResolveThingDef(candidate.Stuff!);
                    if (material == null) return new PlacementPreviewFailure("No ThingDef matches the requested stuff.", PlacementFailureKind.NotFound);
                }
                else if (madeFromStuff) material = GenStuff.DefaultStuffFor(definition);
                // CanPlaceBlueprintAt checks the site, assuming the architect already
                // restricted definitions and materials to its ordinary menu choices.
                if (madeFromStuff && (material == null || !material.IsStuff || material.stuffProps == null
                    || !material.stuffProps.CanMake(definition)))
                    return new PlacementPreviewFailure("The requested or default stuff cannot make this definition.", PlacementFailureKind.InvalidRequest);
                var thing = definition as ThingDef;
                var result = new PlacementPreviewEvaluation {
                    madeFromStuff = madeFromStuff,
                    passability = thing != null ? thing.passability.ToString() : ((TerrainDef)definition).passability.ToString(),
                    isDoor = thing != null && typeof(Building_Door).IsAssignableFrom(thing.thingClass),
                    researchFinished = definition.IsResearchFinished,
                    buildableByPlayer = definition.BuildableByPlayer
                };
                if (result.passability != "Standable" && result.passability != "PassThroughOnly" && result.passability != "Impassable")
                    return new PlacementPreviewFailure("Native passability is unavailable.");
                foreach (var rotation in rotations)
                    result.rotations.Add(EvaluateRotation(map, definition, definition.blueprintDef, center, new Rot4(rotation), material, godMode));
                if (!result.researchFinished)
                    foreach (var rotation in result.rotations)
                    {
                        rotation.accepted = false;
                        rotation.reason = "Required research for this definition is not finished.";
                    }
                var costs = ReadCosts(definition, material);
                if (costs == null) return new PlacementPreviewFailure("Native construction costs could not be read completely.");
                else
                {
                    result.costList = costs;
                    result.materials = ReadMaterials(map, costs);
                }
                return result;
            }
            catch (PlacementLimitException error) { return new PlacementPreviewFailure(error.Message, PlacementFailureKind.CapacityExceeded); }
            catch (Exception error) { return new PlacementPreviewFailure("Placement preview could not be read: " + error.Message); }
        }

        internal static PlacementRotation EvaluateRotation(Map map, BuildableDef definition, ThingDef? blueprint,
            IntVec3 center, Rot4 rotation, ThingDef? material, bool godMode, bool bounded = true)
        {
            var report = GenConstruct.CanPlaceBlueprintAt(definition, center, rotation, map, godMode, null, null, material);
            var result = new PlacementRotation {
                rotation = RotationNames[rotation.AsInt & 3], accepted = report.Accepted,
                reason = Diagnostic(report.Accepted ? "" : report.Reason ?? ""), Rect = GenAdj.OccupiedRect(center, rotation, definition.Size)
            };
            if (bounded && (long)result.Rect.Width * result.Rect.Height > 4096)
                throw new PlacementLimitException("Native footprint exceeds 4096 cells");
            var seen = new HashSet<int>();
            foreach (var cell in result.Rect)
            {
                result.occupiedCells.Add(new PlacementCell(cell));
                if (!cell.InBounds(map)) continue;
                foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
                {
                    if (thing == null || thing.def == null || !seen.Add(thing.thingIDNumber)) continue;
                    if (bounded && seen.Count > 4096) throw new PlacementLimitException("Native footprint exceeds 4096 things");
                    if (bounded) DefinitionName(thing.def.category.ToString());
                    // Placement wipes with the blueprint definition; loose items displaced
                    // by the finished building are hauled, rather than destroyed.
                    var wipes = blueprint != null && GenSpawn.SpawningWipes(blueprint, thing.def);
                    var haul = !wipes && thing.def.category == ThingCategory.Item && GenSpawn.SpawningWipes(definition, thing.def);
                    var cancels = thing is Frame && cell == center && TagsIntersect(blueprint, thing.def);
                    if (thing.Position == center && thing.Rotation == rotation && thing.Stuff == material
                        && (thing.def == definition || thing.def.entityDefToBuild == definition))
                        result.IdenticalBlueprintExists = true;
                    result.blockingThings.Add(new PlacementBlocker(thing, wipes, haul, cancels));
                }
            }
            if (result.occupiedCells.Count == 0) throw new InvalidOperationException("Empty native footprint");
            try { result.watchCellsAccessible = ComfortFacts.WatchCellsAccessible(map, definition, center, rotation); }
            catch (Exception) { result.watchCellsAccessible = null; }
            return result;
        }

        internal static List<PlacementCost>? ReadCosts(BuildableDef definition, ThingDef? material, bool bounded = true)
        {
            try
            {
                // Avoid the native logging branch for stuff on an unstuffed definition.
                var costs = definition.CostListAdjusted(definition.MadeFromStuff ? material : null, false);
                if (costs == null) return null;
                if (bounded && costs.Count > 256) throw new PlacementLimitException("Native costs exceed 256 rows");
                return costs.Select(row => {
                    if (row == null || row.thingDef == null || row.count < 0)
                        throw new InvalidOperationException("Invalid native cost");
                    if (bounded) DefinitionName(row.thingDef.defName);
                    return new PlacementCost(row.thingDef, row.count);
                }).ToList();
            }
            catch (PlacementLimitException) { throw; }
            catch { return null; }
        }

        internal static PlacementMaterials ReadMaterials(Map map, List<PlacementCost> costs)
        {
            var result = new PlacementMaterials();
            Faction? player;
            Dictionary<ThingDef, int> reserved;
            try {
                player = Faction.OfPlayerSilentFail;
                if (player == null) throw new InvalidOperationException("Player faction unavailable");
                reserved = ReadConstructionDeficit(map);
            }
            catch {
                result.unreadable = true;
                foreach (var cost in costs) result.rows.Add(new PlacementMaterial(cost.defName, null));
                return result;
            }
            foreach (var cost in costs.GroupBy(row => row.Definition).Select(group => group.First()))
            {
                int? available = null;
                var total = 0; var forbiddenTotal = 0; var claimed = 0;
                try
                {
                    if (cost.Definition == ThingDefOf.MinifiedThing) throw new InvalidOperationException("Unscannable resource");
                    foreach (var thing in map.listerThings.ThingsOfDef(cost.Definition))
                    {
                        if (thing == null || !thing.Spawned || thing.Faction != null && thing.Faction != player) continue;
                        var forbidden = (thing as ThingWithComps)?.GetComp<CompForbiddable>()?.Forbidden ?? false;
                        var count = thing.stackCount;
                        if (count < 0) throw new InvalidOperationException("Invalid native stack count");
                        total = checked(total + count);
                        if (forbidden) forbiddenTotal = checked(forbiddenTotal + count);
                    }
                    reserved.TryGetValue(cost.Definition, out claimed);
                    available = (int)Math.Max(0L, (long)total - forbiddenTotal - claimed);
                }
                catch { result.unreadable = true; }
                result.rows.Add(new PlacementMaterial(cost.defName, available, total, forbiddenTotal, claimed));
            }
            return result;
        }

        private sealed class PlacementLimitException : Exception
        {
            internal PlacementLimitException(string message) : base(message) { }
        }

        internal static bool TryParseRotations(string spec, out List<int> rotations, out string? error)
        {
            rotations = new List<int>();
            error = null;
            var text = (spec ?? "all").Trim().ToLowerInvariant();

            if (text.Length == 0 || text == "all")
            {
                rotations.AddRange(new[] { 0, 1, 2, 3 });
                return true;
            }

            // NOT Rot4.FromString: it Log.Errors on anything it does not recognise,
            // and Log.Error calls TickManager.Pause().
            for (var i = 0; i < RotationNames.Length; i++)
            {
                if (text == RotationNames[i] || text == i.ToString())
                {
                    rotations.Add(i);
                    return true;
                }
            }

            error = "rotation must be north, east, south, west, 0-3, or 'all'. Got: " + spec;
            return false;
        }

        internal static bool TryResolveBuildable(string spec, out BuildableDef? def, out string? kind)
        {
            // Exact native IDs only. Labels, trimming and case fallback are not identity.
            def = ResolveThingDef(spec);
            kind = def == null ? null : "ThingDef";
            if (def != null) return true;
            def = DefDatabase<TerrainDef>.GetNamedSilentFail(spec);
            if (def != null && !string.Equals(def.defName, spec, StringComparison.Ordinal)) def = null;
            if (def != null) kind = "TerrainDef";
            return def != null;
        }

        internal static ThingDef? ResolveThingDef(string spec)
        {
            var definition = DefDatabase<ThingDef>.GetNamedSilentFail(spec);
            return definition != null && string.Equals(definition.defName, spec, StringComparison.Ordinal) ? definition : null;
        }

        private static Dictionary<ThingDef, int> ReadConstructionDeficit(Map map)
        {
            var result = new Dictionary<ThingDef, int>();
            var seen = new HashSet<int>();
            foreach (var group in new[] { ThingRequestGroup.Blueprint, ThingRequestGroup.BuildingFrame })
                foreach (var thing in map.listerThings.ThingsInGroup(group))
                {
                    if (thing == null) throw new InvalidOperationException("Construction entry unavailable");
                    if (!seen.Add(thing.thingIDNumber) || thing is Blueprint_Install) continue;
                    if (!(thing is IConstructible construction))
                        throw new InvalidOperationException("Construction interface unavailable");
                    var costs = construction.TotalMaterialCost();
                    if (costs == null) throw new InvalidOperationException("Construction costs unavailable");
                    foreach (var cost in costs)
                    {
                        if (cost?.thingDef == null || cost.count < 0)
                            throw new InvalidOperationException("Invalid construction cost");
                        var remaining = construction.ThingCountNeeded(cost.thingDef);
                        if (remaining < 0) throw new InvalidOperationException("Invalid construction deficit");
                        result.TryGetValue(cost.thingDef, out var total);
                        result[cost.thingDef] = checked(total + remaining);
                    }
                }
            return result;
        }

        internal static bool TagsIntersect(ThingDef? blueprintDef, ThingDef? other)
        {
            try
            {
                if (blueprintDef == null || other == null)
                    return false;
                var a = blueprintDef.replaceTags;
                var b = other.replaceTags;
                if (a == null || b == null || a.Count == 0 || b.Count == 0)
                    return false;
                for (var i = 0; i < a.Count; i++)
                    for (var j = 0; j < b.Count; j++)
                        if (string.Equals(a[i], b[j], StringComparison.Ordinal))
                            return true;
                return false;
            }
            catch { throw; }
        }
    }
}

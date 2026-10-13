using System.Reflection;
using RimWorld;
using RimWorld.QuestGen;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_quests.go and catalog_quest_families.go. The Go version walked the string fields of a proto message; the
// game's node tree is walked by reflection over the fields that hold a QuestNode, a list of them, a plain
// PlanetLayerDef, or a SlateRef (whose text is the def name, a `<li>` list of them, or an unresolved expression).

public sealed partial class DefinitionCatalog
{
    // The package id of the mod that defines Odyssey's defs, compared without case as the game compares package ids.
    private const string OdysseyPackageId = "ludeon.rimworld.odyssey";

    // The quest tree fields that name a PlanetLayerDef: one required layer, or a whitelist of layers the quest may use. A
    // layerBlacklist only narrows and needs no ship, so it is not read.
    private const string QuestLayerField = "layer";
    private const string QuestLayerDefField = "layerDef";
    private const string QuestWhitelistField = "layerWhitelist";

    private static bool IsOdyssey(Def def) => string.Equals(def.modContentPack?.PackageId, OdysseyPackageId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies the quest script scriptDef for a colony that never builds or launches a gravship: a script the Odyssey
    /// mod does not define is Other; an Odyssey script whose tree (following QuestNode_SubScript) names one required
    /// space layer, or a whitelist of space layers only, is ShipOnly; every other Odyssey script is Ground. A script with
    /// no row, or a layer reference that is no PlanetLayerDef (an unresolved slate expression), is an error.
    /// </summary>
    public P.QuestClass QuestClass(string scriptDef)
    {
        var root = DefRow<QuestScriptDef>(scriptDef) ?? throw new CatalogException($"catalog has no QuestScriptDef row {scriptDef}");
        if (!IsOdyssey(root))
        {
            return new P.QuestClass(P.QuestScope.Other, string.Empty);
        }

        var walk = new QuestWalk(this);
        walk.Script(root);
        return walk.SpaceLayer.Length > 0
            ? new P.QuestClass(P.QuestScope.ShipOnly, walk.SpaceLayer)
            : new P.QuestClass(P.QuestScope.Ground, string.Empty);
    }

    /// <summary>Classifies one catalog root without inventing a default family. Odyssey keeps its layer-derived classification; auto-accepted roots are never proactively acted on.</summary>
    public P.QuestProfile QuestProfile(string scriptDef)
    {
        var row = DefRow<QuestScriptDef>(scriptDef) ?? throw new CatalogException($"catalog has no QuestScriptDef row {scriptDef}");
        var profile = P.PolicyFunctions.QuestFamilyForRoot(scriptDef);
        if (profile.Family == P.QuestFamily.None)
        {
            throw new CatalogException($"quest script {scriptDef} has no known family");
        }

        if (IsOdyssey(row) && profile.Family != P.QuestFamily.Utility && QuestClass(scriptDef).Scope == P.QuestScope.ShipOnly)
        {
            profile = profile with
            {
                Family = P.QuestFamily.OdysseyShipOnly,
                NeverAct = true,
                Disposition = P.QuestDisposition.Refuse,
                SkipReason = "ship_only",
            };
        }

        profile = profile with { NeverAct = profile.NeverAct || row.autoAccept };
        if (row.autoAccept && profile.Disposition == P.QuestDisposition.Decide)
        {
            profile = profile with { Disposition = P.QuestDisposition.Observe };
        }

        return profile;
    }

    // Splits a slate list value (`<li>Orbit</li><li>Surface</li>`) or a single def name into def names; an expression
    // stays one token and so fails to resolve.
    private static List<string> LayerTokens(string? value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return [];
        }

        if (!value.Contains("<li>", StringComparison.Ordinal))
        {
            return [value];
        }

        var names = new List<string>();
        foreach (var part in value.Split("</li>"))
        {
            var at = part.IndexOf("<li>", StringComparison.Ordinal);
            var name = (at >= 0 ? part[(at + 4)..] : part).Trim();
            if (at >= 0 || name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The space layers one quest script requires.
    private sealed class QuestWalk(DefinitionCatalog catalog)
    {
        private readonly HashSet<string> _seenScripts = new(StringComparer.Ordinal);
        private readonly HashSet<object> _seenNodes = new(ReferenceEqualityComparer.Instance);

        public string SpaceLayer { get; private set; } = string.Empty;

        public void Script(QuestScriptDef row)
        {
            if (!_seenScripts.Add(row.defName))
            {
                return;
            }

            if (row.root is { } root)
            {
                Node(row.defName, root);
            }
        }

        private void Node(string script, object node)
        {
            if (!_seenNodes.Add(node))
            {
                return;
            }

            for (var type = node.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(node);
                    switch (value)
                    {
                        case null:
                            continue;
                        case QuestNode child:
                            Node(script, child);
                            break;
                        case IEnumerable<QuestNode> children:
                            foreach (var item in children.Where(item => item is not null))
                            {
                                Node(script, item);
                            }

                            break;
                        case PlanetLayerDef layer when field.Name is QuestLayerField or QuestLayerDefField:
                            Required(script, layer.defName);
                            break;
                        case ISlateRef slate:
                            Field(script, node, field.Name, slate.SlateRef);
                            break;
                    }
                }
            }
        }

        private void Field(string script, object node, string field, string? value)
        {
            switch (field)
            {
                case QuestLayerField or QuestLayerDefField:
                    Required(script, value);
                    break;
                case QuestWhitelistField:
                    Whitelist(script, LayerTokens(value));
                    break;
                case "def" when node is QuestNode_SubScript:
                    var name = value?.Trim() ?? string.Empty;
                    var sub = catalog.DefRow<QuestScriptDef>(name) ?? throw new CatalogException($"quest script {script} runs sub-script \"{name}\", which has no QuestScriptDef row");
                    Script(sub);
                    break;
            }
        }

        private PlanetLayerDef LayerRow(string script, string name) =>
            catalog.DefRow<PlanetLayerDef>(name) ?? throw new CatalogException($"quest script {script} names planet layer \"{name}\", which has no PlanetLayerDef row");

        private void Required(string script, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (LayerRow(script, name.Trim()).isSpace && SpaceLayer.Length == 0)
            {
                SpaceLayer = name.Trim();
            }
        }

        // A whitelist needs a ship when it holds space layers only.
        private void Whitelist(string script, List<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            foreach (var name in names)
            {
                if (!LayerRow(script, name).isSpace)
                {
                    return;
                }
            }

            if (SpaceLayer.Length == 0)
            {
                SpaceLayer = names[0];
            }
        }
    }
}

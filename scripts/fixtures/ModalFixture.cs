using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Compiled only for disposable native UI acceptance.
    public sealed class ModalFixture
    {
        private static Func<object> read;
        private static Window opened;

        [Tool("test/modal_fixture", Description = "Disposable native dialog setup and game-value readback.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string action = "read", string kind = "zone")
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (action == "read") return (object)new { success = true, value = read?.Invoke(),
                    windowId = opened?.ID, windowOpen = opened != null && Find.WindowStack.Windows.Contains(opened),
                    paused = Find.TickManager.Paused };
                if (action == "replace")
                {
                    if (opened != null) Find.WindowStack.TryRemove(opened);
                    opened = new Dialog_MessageBox("External replacement fixture", "OK");
                    Find.WindowStack.Add(opened);
                    return (object)new { success = true, windowId = opened.ID };
                }
                var map = Find.CurrentMap;
                var pawn = map.mapPawns.FreeColonistsSpawned.First();
                var settlement = (Settlement)map.Parent;
                if (kind == "quest")
                {
                    var quest = new Quest { id = Find.UniqueIDsManager.GetNextQuestID(),
                        name = "Modal fixture reward choice", description = "Choose eleven or twenty-two silver." };
                    var choice = new QuestPart_Choice();
                    quest.AddPart(choice);
                    var markers = new List<QuestPart>();
                    foreach (int count in new[] { 11, 22 })
                    {
                        var item = ThingMaker.MakeThing(ThingDefOf.Silver);
                        item.stackCount = count;
                        var marker = new QuestPart_Pass();
                        quest.AddPart(marker);
                        markers.Add(marker);
                        choice.choices.Add(new QuestPart_Choice.Choice {
                            rewards = new List<Reward> { new Reward_Items { items = new List<Thing> { item } } },
                            questParts = new List<QuestPart> { marker } });
                    }
                    Find.QuestManager.Add(quest);
                    Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Quests);
                    ((MainTabWindow_Quests)MainButtonDefOf.Quests.TabWindow).Select(quest);
                    opened = null;
                    read = () => new { questId = quest.id, state = quest.State.ToString(),
                        choiceCount = choice.choices.Count,
                        rewards = choice.choices.Select(c => ((Reward_Items)c.rewards[0]).items[0].stackCount).ToArray(),
                        branchesPresent = markers.Select(m => quest.PartsListForReading.Contains(m)).ToArray() };
                    return (object)new { success = true, value = read() };
                }
                switch (kind)
                {
                    case "faction":
                        opened = new Dialog_NamePlayerFaction();
                        read = () => Faction.OfPlayer.Name;
                        break;
                    case "settlement":
                        opened = new Dialog_NamePlayerSettlement(settlement);
                        read = () => settlement.Name;
                        break;
                    case "combined":
                        opened = new Dialog_NamePlayerFactionAndSettlement(settlement);
                        read = () => new { first = Faction.OfPlayer.Name, second = settlement.Name };
                        break;
                    case "zone":
                        var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                        map.zoneManager.RegisterZone(zone);
                        opened = new Dialog_RenameZone(zone);
                        read = () => zone.RenamableLabel;
                        break;
                    case "area":
                        map.areaManager.TryMakeNewAllowed(out var area);
                        opened = new Dialog_RenameArea(area);
                        read = () => area.RenamableLabel;
                        break;
                    case "policy":
                        var policy = Current.Game.foodRestrictionDatabase.AllFoodRestrictions.First();
                        opened = new Dialog_RenamePolicy(policy);
                        read = () => policy.RenamableLabel;
                        break;
                    case "bill":
                        var bench = (IBillGiver)Spawn(map, pawn.Position, "TableButcher");
                        var bill = (Bill_Production)((Thing)bench).def.AllRecipes.First().MakeNewBill();
                        bench.BillStack.AddBill(bill);
                        opened = new Dialog_RenameBill(bill);
                        read = () => bill.RenamableLabel;
                        break;
                    case "storage":
                    case "storage_new":
                        var storage = (Building_Storage)Spawn(map, pawn.Position, "Shelf");
                        var member = (IStorageGroupMember)storage;
                        if (kind == "storage_new") opened = new Dialog_RenameBuildingStorage_CreateNew(storage);
                        else
                        {
                            var group = map.storageGroups.NewGroup();
                            group.InitFrom(member);
                            member.SetStorageGroup(group);
                            opened = new Dialog_RenameBuildingStorage(group);
                        }
                        read = () => member.Group?.RenamableLabel;
                        break;
                    case "pen":
                        var penDef = DefDatabase<ThingDef>.AllDefs.First(d => d.comps != null
                            && d.comps.Any(c => c.compClass == typeof(CompAnimalPenMarker)));
                        var marker = Spawn(map, pawn.Position, penDef.defName).TryGetComp<CompAnimalPenMarker>();
                        opened = new Dialog_RenameAnimalPen(map, marker);
                        read = () => marker.RenamableLabel;
                        break;
                    case "gravship":
                    case "gravship_given":
                        var engineDef = DefDatabase<ThingDef>.AllDefs.First(d => d.thingClass == typeof(Building_GravEngine));
                        var engine = (Building_GravEngine)ThingMaker.MakeThing(engineDef);
                        opened = kind == "gravship" ? (Window)new Dialog_RenameGravship(engine) : new Dialog_NamePlayerGravship(engine);
                        read = () => engine.RenamableLabel;
                        break;
                    case "pawn":
                    case "animal":
                        if (kind == "animal")
                        {
                            pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Muffalo, Faction.OfPlayer);
                            pawn.Name = new NameSingle("Fixture animal");
                            GenSpawn.Spawn(pawn, map.mapPawns.FreeColonistsSpawned.First().Position, map);
                            pawn.Drawer.renderer.EnsureGraphicsInitialized();
                        }
                        opened = new Dialog_NamePawn(pawn, kind == "pawn" ? NameFilter.First | NameFilter.Nick | NameFilter.Last | NameFilter.Title : NameFilter.Nick,
                            NameFilter.Nick, null);
                        read = () => pawn.Name.ToStringShort;
                        break;
                    default: throw new ArgumentException(kind);
                }
                Find.WindowStack.Add(opened);
                return (object)new { success = true, windowId = opened.ID, window = opened.GetType().FullName,
                    value = read() };
            }, cancellationToken);
        }

        private static Thing Spawn(Map map, IntVec3 center, string name)
        {
            var def = DefDatabase<ThingDef>.GetNamed(name);
            var cell = GenRadial.RadialCellsAround(center, 35, true).First(c =>
                GenAdj.OccupiedRect(c, Rot4.North, def.Size).All(p => p.InBounds(map)
                    && !p.Fogged(map) && p.Standable(map) && p.GetEdifice(map) == null));
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, cell, map);
        }
    }
}

using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
namespace RimBot.Colony
{
    public static class ColonyNotifications
    {
        // Only these two UI-owned lists need reflection. Never execute arbitrary reflected methods.
        private static readonly FieldInfo Alerts=typeof(AlertsReadout).GetField("activeAlerts",BindingFlags.Instance|BindingFlags.NonPublic);
        private static readonly FieldInfo Live=typeof(Messages).GetField("liveMessages",BindingFlags.Static|BindingFlags.NonPublic);
        public static JObject Read(bool details=false)
        {
            var ui=Find.UIRoot as UIRoot_Play;
            var alerts=ui==null?null:Alerts?.GetValue(ui.alerts) as List<Alert>;
            var live=Live?.GetValue(null) as List<Message>;
            var messages=(Find.Archive?.ArchivablesListForReading.OfType<Message>()??Enumerable.Empty<Message>())
                .Concat(live??Enumerable.Empty<Message>()).Distinct().OrderByDescending(m=>m.startingTick).Take(8);
            return new JObject {
                ["scope"]="Player notifications, potentially across maps. Reading does not dismiss them.",
                ["alertsAvailable"]=alerts!=null,["liveMessagesAvailable"]=live!=null,
                ["alerts"]=new JArray((alerts??Enumerable.Empty<Alert>()).Take(16).Select(a=>new JObject {
                    ["label"]=a.Label,["priority"]=a.Priority.ToString(),["detail"]=details?ActivitySummary.Short(a.GetExplanation().ToString(),450):null
                })),
                ["letters"]=new JArray((Find.LetterStack?.LettersListForReading??Enumerable.Empty<Letter>()).OrderByDescending(l=>l.arrivalTick).Take(8).Select(l=>new JObject {
                    ["id"]=l.ID,["label"]=l.Label.ToString(),["type"]=l.def.defName,["tick"]=l.arrivalTick,
                    ["detail"]=details?ActivitySummary.Short(((IArchivable)l).ArchivedTooltip,450):null
                })),
                ["messages"]=new JArray(messages.Select(m=>new JObject{["text"]=ActivitySummary.Short(m.text,details?400:160),["type"]=m.def?.defName,["tick"]=m.startingTick}))
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace RimBot.Colony
{
    // Observing is local; a batch of changed game notifications wakes one decision.
    public sealed class NotificationFeed
    {
        private readonly HashSet<string> seen=new HashSet<string>();
        private readonly Queue<string> seenOrder=new Queue<string>();
        private HashSet<string> alerts=new HashSet<string>();
        private bool initialized;
        public int Revision { get; private set; }
        public string LatestBatch { get; private set; }="";
        public bool Observe(JObject state)
        {
            var added=new JArray();
            var active=new HashSet<string>((state["alerts"] as JArray??new JArray()).Select(a=>a["label"]?.Value<string>()??""));
            foreach(string label in active.Except(alerts)) added.Add(new JObject{["alert"]=label});
            foreach(string label in alerts.Except(active)) added.Add(new JObject{["clearedAlert"]=label});
            alerts=active;
            foreach(string category in new[]{"letters","messages"}) foreach(var item in state[category] as JArray??new JArray()) {
                string id=category+":"+item.ToString(Formatting.None);
                if(!seen.Add(id)) continue;
                seenOrder.Enqueue(id); added.Add(new JObject{[category]=item.DeepClone()});
            }
            while(seenOrder.Count>256) seen.Remove(seenOrder.Dequeue());
            if(!initialized) { initialized=true; return false; }
            if(added.Count==0) return false;
            Revision++; LatestBatch=new JArray(added.Take(12)).ToString(Formatting.None); return true;
        }
    }
}

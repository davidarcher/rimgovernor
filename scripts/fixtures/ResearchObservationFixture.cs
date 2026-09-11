using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class ResearchObservationFixture
    {
        [Tool("test/research_observation_fingerprint", Description = "Private read-only fixture: exact saved research backing-state evidence without progress getters, slot initialization, saves or game orders.")]
        public async Task<object> Fingerprint(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var manager = Find.ResearchManager;
                if (manager == null) throw new InvalidOperationException("No research manager.");
                object Field(string name) => typeof(ResearchManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager);
                object Floats(string name)
                {
                    var values = Field(name) as Dictionary<ResearchProjectDef, float>;
                    if (values == null) return null;
                    return values.OrderBy(p => p.Key.defName, StringComparer.Ordinal).Select(p => new {
                        defName = p.Key.defName, bits = Convert.ToBase64String(BitConverter.GetBytes(p.Value)) }).ToArray();
                }
                var slots = Field("currentAnomalyKnowledgeProjects") as List<ResearchManager.KnowledgeCategoryProject>;
                var techprints = Field("techprints") as Dictionary<ResearchProjectDef, int>;
                return new { success = true, current = (Field("currentProj") as ResearchProjectDef)?.defName,
                    progress = Floats("progress"), knowledge = Floats("anomalyKnowledge"),
                    slots = slots?.Select(s => new { category = s.category?.defName, project = s.project?.defName }).ToArray(),
                    techprints = techprints?.OrderBy(p => p.Key.defName, StringComparer.Ordinal).Select(p => new { defName = p.Key.defName, value = p.Value }).ToArray(),
                    tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}

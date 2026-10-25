using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Common;
using System.Text.Json.Serialization;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Observations;

public sealed record ActivityTaskRequest(RimGovernor.Brain.GameFacts.Common.Identity Identity, PawnActivities Batch);

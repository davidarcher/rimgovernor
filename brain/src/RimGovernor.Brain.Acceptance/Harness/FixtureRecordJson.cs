using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The request edge of a bespoke <c>test/*</c> op called outside a typed case (the speed matrix, profile-capture): its
/// arguments from a Fixtures.Contracts request record. The reply reads back through <see cref="Harness.FixtureReply{T}"/>.
/// </summary>
public static class FixtureRecordJson
{
    /// <summary>The request record as the op's JSON arguments object.</summary>
    public static JsonObject Args<T>(T request) =>
        JsonNode.Parse(Fx.FixtureJson.Serialize(request)) as JsonObject ?? throw new JsonException($"{typeof(T).Name} is not a JSON object");
}

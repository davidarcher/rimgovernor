using System.Text.Json;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The request of a fixture op that takes no arguments.</summary>
internal sealed record NoArgs
{
    public static readonly NoArgs Value = new();
}

/// <summary>
/// The typed bespoke ops of the served root cases (#3398): a recorder's read, a preparation or an injection whose reply
/// is a <c>RimGovernor.Fixtures.Contracts</c> record.
/// </summary>
internal static class ServedOps
{
    /// <summary>An op without arguments whose reply is <typeparamref name="T"/>.</summary>
    public static FixtureOp<NoArgs, T> Read<T>(string tool) => new(tool, _ => [], Reply<T>(tool));

    /// <summary>An op without arguments that only prepares or injects.</summary>
    public static FixtureOp<NoArgs, Fx.FixtureDone> Inject(string tool) => new(tool, _ => [], Done(tool));

    /// <summary>The decoder of a <typeparamref name="T"/> reply; a missing or foreign reply fails the call's label.</summary>
    public static Func<string, JsonObject?, T> Reply<T>(string tool) => (label, reply) =>
    {
        try
        {
            return reply is null
                ? throw new AcceptanceException($"{label}: {tool} replied nothing")
                : Fx.FixtureJson.Deserialize<T>(reply.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{label}: {tool} replied {reply?.ToJsonString()}: {e.Message}", e);
        }
    };

    /// <summary>The decoder of a preparation's or an injection's reply; anything but success fails the call's label.</summary>
    public static Func<string, JsonObject?, Fx.FixtureDone> Done(string tool)
    {
        var decode = Reply<Fx.FixtureDone>(tool);
        return (label, reply) => decode(label, reply) is { Success: true } done
            ? done
            : throw new AcceptanceException($"{label}: {tool} refused: {reply?.ToJsonString()}");
    }

    /// <summary>The decoder of a <typeparamref name="T"/> reply that must report <paramref name="success"/>; a refusal fails the call's label.</summary>
    public static Func<string, JsonObject?, T> Succeeded<T>(string tool, Func<T, bool> success)
    {
        var decode = Reply<T>(tool);
        return (label, reply) => decode(label, reply) is var read && success(read)
            ? read
            : throw new AcceptanceException($"{label}: {tool} refused: {reply?.ToJsonString()}");
    }

    /// <summary>A fixture op's JSON arguments.</summary>
    public static JsonObject Args(params (string Key, JsonNode? Value)[] args) =>
        new(args.Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value)));

    /// <summary>A fixture contract record as report evidence.</summary>
    public static JsonNode? Json<T>(T value) => JsonNode.Parse(Fx.FixtureJson.Serialize(value));
}

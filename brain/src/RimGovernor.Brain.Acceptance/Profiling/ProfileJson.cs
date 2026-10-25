using System.Text.Json.Serialization;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The manual profile-capture JSON summary and equality result.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProfileSummary))]
[JsonSerializable(typeof(Fx.ProfileEquality))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;

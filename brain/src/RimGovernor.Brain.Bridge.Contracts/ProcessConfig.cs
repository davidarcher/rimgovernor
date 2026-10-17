namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Names the game a client drives. <see cref="Timeout"/> bounds connecting, discovery
/// and each read, including queue time. <see cref="RecordRows"/>, when set, writes every native call, frame read
/// and rollup as a native_* telemetry row.
/// </summary>
public sealed record ProcessConfig(string GameId, TimeSpan Timeout, bool RecordRows = false);

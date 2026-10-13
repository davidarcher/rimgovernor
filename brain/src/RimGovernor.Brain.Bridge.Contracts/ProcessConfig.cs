namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ProcessConfig</c>: names the game a client drives. <see cref="Timeout"/> bounds connecting, discovery
/// and each read, including queue time. <see cref="RecordRows"/>, when set, writes every native call, frame read
/// and rollup as a native_* telemetry row. Go's <c>Launch</c> (a gamehost spec) and <c>Transcript</c> are not
/// ported: they await the gamehost and replay ports.
/// </summary>
public sealed record ProcessConfig(string GameId, TimeSpan Timeout, bool RecordRows = false);

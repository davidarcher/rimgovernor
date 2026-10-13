using RimGovernor.Brain.Acceptance;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ThoughtAudit;

/// <summary>Finds the RimWorld install the audit reads: the flag, <c>RIMGOVERNOR_RIMWORLD_DIR</c>, then the Steam libraries.</summary>
internal static class GameDirectory
{
    public static string Resolve(string flag)
    {
        var dir = flag;
        if (dir.Length == 0)
        {
            dir = Environment.GetEnvironmentVariable(SetupDiscovery.RimWorldDirEnv) ?? "";
        }
        if (dir.Length == 0)
        {
            IReadOnlyList<string> libraries;
            try
            {
                libraries = SetupDiscovery.SteamLibraries();
            }
            catch (SetupException e)
            {
                throw new ToolException($"no RimWorld install: {e.Message}; set {SetupDiscovery.RimWorldDirEnv} or pass -rimworld", e);
            }
            dir = libraries
                .Select(lib => Path.Combine(lib, "steamapps", "common", "RimWorld"))
                .FirstOrDefault(c => File.Exists(Path.Combine(c, SetupDiscovery.GameImage))) ?? "";
            if (dir.Length == 0)
            {
                throw new ToolException($"no RimWorld install in the Steam libraries; set {SetupDiscovery.RimWorldDirEnv} or pass -rimworld");
            }
        }
        dir = Path.GetFullPath(dir);
        if (!File.Exists(Path.Combine(dir, "Assembly-CSharp.dll")))
        {
            throw new ToolException($"{dir} holds no Assembly-CSharp.dll");
        }
        return dir;
    }
}

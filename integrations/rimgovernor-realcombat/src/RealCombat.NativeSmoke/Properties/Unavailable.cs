// Compiled only where RimWorldDir has no Assembly-CSharp.dll (see RealCombat.csproj).
namespace RimGovernor.RealCombat.NativeSmoke;

internal static class GameUnavailable
{
    private static int Main()
    {
        Console.Error.WriteLine("realcombat: built without a game install; rebuild with -p:RimWorldDir=<game copy>");
        return 2;
    }
}

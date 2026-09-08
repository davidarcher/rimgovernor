using UnityEngine;
using Verse;

namespace HeadlessRim
{
    [StaticConstructorOnStartup]
    public static class HeadlessStartup
    {
        static HeadlessStartup()
        {
            if (!System.Linq.Enumerable.Contains(System.Environment.GetCommandLineArgs(), "-batchmode")) return;
            Log.Message("[HeadlessRim] Test mode: rendering disabled, frame cap removed.");
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }
    }
}

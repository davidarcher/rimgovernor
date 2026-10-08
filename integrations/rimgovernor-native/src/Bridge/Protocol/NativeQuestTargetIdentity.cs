namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestTargetIdentity
    {
        internal static bool WorshippedTerminalSignal(int questId, string signal)
            => signal == "Quest" + questId + ".terminal.HackingStarted";
    }
}

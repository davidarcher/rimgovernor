#nullable enable
using System;
using System.Threading;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using Verse.Steam;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The in-game time controls with six buttons, Pause through Hyperfast, for every player (#3608). A Harmony prefix
    /// replaces <c>TimeControls.DoTimeControlsGUI</c> and draws vanilla's row (RimWorld 1.7, read with ilspycmd) two
    /// buttons wider, keeping its right edge, sounds, tooltips, forced-speed lines and keys. Space and 1-4 keep their
    /// bindings, Ultrafast's key works outside dev mode, and Hyperfast has its own (RimGovernor_TimeSpeed_Hyperfast, 5).
    /// With the bot connected (native control authority active) a speed button or key only asks the brain
    /// (<see cref="Supervisor.RequestSpeed"/>) and never touches the game's speed; Pause goes to the game, where native's
    /// ExternalPause hold is the take-over. Without the bot a click sets the game's speed
    /// (<see cref="Supervisor.SetGameSpeed"/>). The decisions are <see cref="TimeControlsPolicy"/>'s. Installed from
    /// <see cref="NativeStartup"/> and skipped in the headless acceptance build; <see cref="Press"/> also serves the
    /// host's <c>rimgovernor/press_time_speed</c> tool, which reaches it by name.
    /// </summary>
    internal static class TimeControlsRow
    {
        private static int installed;
        private static KeyBindingDef? hyperfastKey;
        private static Texture2D? ultrafastIcon, hyperfastIcon;

        internal static void Install()
        {
            if (HeadlessRim.HeadlessMode.Active) return;
            if (Interlocked.CompareExchange(ref installed, 1, 0) != 0) return;
            try
            {
                new Harmony("rimgovernor.time-controls").Patch(
                    AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI))
                        ?? throw new MissingMethodException("TimeControls.DoTimeControlsGUI"),
                    prefix: new HarmonyMethod(typeof(TimeControlsRow), nameof(Replace)));
            }
            catch (Exception ex)
            {
                ModLog.Error("startup", "Time controls installation failed: " + ex);
            }
        }

        // Vanilla's row is never drawn.
        private static bool Replace(Rect timerRect)
        {
            DoRow(timerRect);
            return false;
        }

        // The [DefOf] rebinding does not reach this assembly (ExtraWorkTypeDefOf), so the def is looked up by its name,
        // spelled once here, after defs load.
        private static KeyBindingDef? HyperfastKey => hyperfastKey ??= DefDatabase<KeyBindingDef>.GetNamedSilentFail("RimGovernor_TimeSpeed_Hyperfast");

        internal static bool BotConnected()
        {
            var game = Current.Game;
            return game != null && NativeControlAuthority.TryGetForGame(game, out var authority) && authority != null && authority.Status().Active;
        }

        /// <summary>
        /// One button or key press, on the game thread: Pause toggles the game's pause; a speed asks the brain while the
        /// bot is connected, else sets the game's speed.
        /// </summary>
        internal static void Press(TimeButton button)
        {
            var tickManager = Find.TickManager;
            if (button == TimeButton.Pause)
            {
                tickManager.TogglePaused();
                PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.Pause, KnowledgeAmount.SpecificInteraction);
                PlaySound(GameButton());
                return;
            }
            var speed = TimeControlsPolicy.Speed(button);
            if (TimeControlsPolicy.Requests(BotConnected())) Supervisor.RequestSpeed(speed);
            else Supervisor.SetGameSpeed(speed);
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.TimeControls, KnowledgeAmount.SpecificInteraction);
            PlaySound(button);
        }

        private static void DoRow(Rect timerRect)
        {
            var tickManager = Find.TickManager;
            var connected = BotConnected();
            var (pending, run) = Supervisor.ShownSpeeds();
            var boost = Supervisor.BoostOn;
            var paused = tickManager.ForcePaused || tickManager.CurTimeSpeed == TimeSpeed.Paused;
            var lit = TimeControlsPolicy.Highlight(connected, paused, pending, run, tickManager.CurTimeSpeed, boost);
            var size = TimeControls.TimeButSize;
            // Vanilla's four buttons start at timerRect.x; six end where its four did.
            var row = new Rect(timerRect.x - 2f * size.x, timerRect.y, TimeControlsPolicy.Buttons.Length * size.x, size.y);
            Widgets.BeginGroup(row);
            var rect = new Rect(0f, 0f, size.x, size.y);
            foreach (var button in TimeControlsPolicy.Buttons)
            {
                if (TimeControlsPolicy.Visible(button, connected, Supervisor.BoostAvailable))
                {
                    var key = KeyFor(button);
                    var tooltip = key == null ? null : $"{"HotKeyTip".Translate()}: {KeyPrefs.KeyPrefsData.GetBoundKeyCode(key, KeyPrefs.BindingSlot.A).ToStringReadable()}";
                    if (Widgets.ButtonImage(rect, Icon(button), true, tooltip) && !tickManager.ForcePaused) Press(button);
                    if (lit == button) GUI.DrawTexture(rect, TexUI.HighlightTex);
                }
                rect.x += rect.width;
            }
            // Vanilla's lines: forced normal speed strikes the buttons above Normal, a forced pause every speed.
            if (tickManager.slower.ForcedNormalSpeed) Widgets.DrawLineHorizontal(rect.width * 2f, rect.height / 2f, rect.width * 4f);
            if (tickManager.ForcePaused) Widgets.DrawLineHorizontal(rect.width, rect.height / 2f, rect.width * 5f);
            Widgets.EndGroup();
            GenUI.AbsorbClicksInRect(row);
            UIHighlighter.HighlightOpportunity(row, "TimeControls");
            if (Event.current.type == EventType.KeyDown) DoKeys(connected, TimeControlsPolicy.Chosen(connected, pending, run, tickManager.CurTimeSpeed, boost));
        }

        private static void DoKeys(bool connected, Clock.Speed? chosen)
        {
            var tickManager = Find.TickManager;
            if (KeyBindingDefOf.TogglePause.KeyDownEvent)
            {
                Press(TimeButton.Pause);
                Event.current.Use();
            }
            if (!Find.WindowStack.WindowsForcePause)
            {
                foreach (var button in TimeControlsPolicy.Buttons)
                {
                    if (button == TimeButton.Pause || KeyFor(button) is not { } key || !key.KeyDownEvent) continue;
                    if (!TimeControlsPolicy.Visible(button, connected, Supervisor.BoostAvailable)) continue;
                    Press(button);
                    Event.current.Use();
                }
                if (KeyBindingDefOf.TimeSpeed_Slower.KeyDownEvent) Step(connected, chosen, faster: false);
                if (KeyBindingDefOf.TimeSpeed_Faster.KeyDownEvent) Step(connected, chosen, faster: true);
            }
            if (Prefs.DevMode && KeyBindingDefOf.Dev_TickOnce.KeyDownEvent && tickManager.CurTimeSpeed == TimeSpeed.Paused)
            {
                tickManager.DoSingleTick();
                SoundDefOf.Clock_Stop.PlayOneShotOnCamera();
            }
        }

        // The slower/faster keys. Without the bot they are vanilla's: one TimeSpeed down (Normal's is a pause) or up to
        // Superfast. With it they ask the brain for the chosen speed's neighbour.
        private static void Step(bool connected, Clock.Speed? chosen, bool faster)
        {
            var tickManager = Find.TickManager;
            if (TimeControlsPolicy.Requests(connected))
            {
                if (TimeControlsPolicy.Step(chosen, faster) is { } next) Press((TimeButton)(int)next);
            }
            else if (faster ? (int)tickManager.CurTimeSpeed < (int)TimeSpeed.Superfast : tickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                tickManager.CurTimeSpeed = (TimeSpeed)((int)tickManager.CurTimeSpeed + (faster ? 1 : -1));
                PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.TimeControls, KnowledgeAmount.SpecificInteraction);
                PlaySound(GameButton());
            }
            Event.current.Use();
        }

        private static KeyBindingDef? KeyFor(TimeButton button) => button switch
        {
            TimeButton.Pause => KeyBindingDefOf.TogglePause,
            TimeButton.Normal => KeyBindingDefOf.TimeSpeed_Normal,
            TimeButton.Fast => KeyBindingDefOf.TimeSpeed_Fast,
            TimeButton.Superfast => KeyBindingDefOf.TimeSpeed_Superfast,
            TimeButton.Ultrafast => KeyBindingDefOf.TimeSpeed_Ultrafast,
            _ => HyperfastKey,
        };

        // Vanilla's textures for Pause through Superfast; Ultrafast and Hyperfast are this mod's (Textures/UI/TimeControls).
        private static Texture2D Icon(TimeButton button) => button switch
        {
            TimeButton.Ultrafast => ultrafastIcon ??= ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Ultrafast"),
            TimeButton.Hyperfast => hyperfastIcon ??= ContentFinder<Texture2D>.Get("UI/TimeControls/TimeSpeedButton_Hyperfast"),
            _ => TexButton.SpeedButtonTextures[(int)button],
        };

        // The button of the game's own speed now.
        private static TimeButton GameButton() =>
            TimeControlsPolicy.GameSpeed(Find.TickManager.CurTimeSpeed, Supervisor.BoostOn) is { } speed ? (TimeButton)(int)speed : TimeButton.Pause;

        // Vanilla's clock sounds (its Ultrafast reuses Superfast's), skipped headless.
        private static void PlaySound(TimeButton button)
        {
            if (HeadlessRim.HeadlessMode.Active) return;
            var sound = button switch
            {
                TimeButton.Pause => SoundDefOf.Clock_Stop,
                TimeButton.Fast => SoundDefOf.Clock_Fast,
                TimeButton.Superfast or TimeButton.Ultrafast or TimeButton.Hyperfast => SoundDefOf.Clock_Superfast,
                _ => SoundDefOf.Clock_Normal,
            };
            sound?.PlayOneShotOnCamera();
            SteamDeck.Vibrate();
        }
    }
}

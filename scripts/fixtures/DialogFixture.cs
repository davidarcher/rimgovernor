using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #156). Opens the same kind of
    // force-pausing Verse.Dialog_NodeTree the game opens by itself (a finished
    // research project's completion dialog, a caravan demand, a quest dialog),
    // built the way ResearchManager.FinishProject builds its own: a text node
    // with resolveTree options, delayInteractivity on. The chosen option is
    // recorded so the harness can prove which native action ran.
    public sealed class DialogFixture
    {
        private static Dialog_NodeTree opened;
        private static string chosen;

        [Tool("test/open_choice_dialog", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: open a force-pausing Dialog_NodeTree with the given '|'-separated option labels now or delayTicks (1..600) ticks ahead. Every option closes the dialog and records its label; a label prefixed '!' is disabled. action=read reports the dialog's state.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "open or read.")] string action = "open",
            [ToolParameter(Description = "Dialog title.")] string title = "Research finished",
            [ToolParameter(Description = "Dialog body text.")] string text = "Disposable choice dialog acceptance case.",
            [ToolParameter(Description = "Option labels separated by '|'.")] string options = "OK|Research screen",
            [ToolParameter(Description = "0 opens now; 1..600 schedules that many ticks ahead.")] int delayTicks = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                if (action == "read") return Read();
                if (delayTicks < 0 || delayTicks > 600) throw new ArgumentException("delayTicks must be within 0..600.");
                var labels = options.Split('|').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (labels.Length == 0) throw new ArgumentException("At least one option is required.");
                chosen = null;
                Action open = () => {
                    var node = new DiaNode(text);
                    foreach (var raw in labels)
                    {
                        var label = raw.TrimStart('!');
                        var option = new DiaOption(label) { resolveTree = true, action = () => chosen = label };
                        if (raw.StartsWith("!")) option.Disable("fixture");
                        node.options.Add(option);
                    }
                    opened = new Dialog_NodeTree(node, delayInteractivity: true, radioMode: false, title: title);
                    Find.WindowStack.Add(opened);
                };
                if (delayTicks == 0) { open(); return Read(); }
                var scheduler = Current.Game.GetComponent<ScheduledDialog>();
                if (scheduler == null) { scheduler = new ScheduledDialog(Current.Game); Current.Game.components.Add(scheduler); }
                var due = checked(Find.TickManager.TicksGame + delayTicks);
                scheduler.Schedule(open, due);
                return new { success = true, scheduled = true, dueTick = due, tick = Find.TickManager.TicksGame, options = labels };
            }, cancellationToken).ConfigureAwait(false);
        }

        private static object Read()
        {
            var open = opened != null && Find.WindowStack.Windows.Contains(opened);
            return new { success = true, windowId = opened?.ID, windowOpen = open, window = opened?.GetType().FullName,
                chosen, forcePaused = Find.TickManager.ForcePaused, paused = Find.TickManager.Paused,
                forcePausingWindows = Find.WindowStack.Windows.Where(w => w.forcePause).Select(w => w.GetType().FullName).ToArray(),
                tick = Find.TickManager.TicksGame };
        }
    }

    // Opens the scheduled dialog on the game tick it falls due, from the
    // ordinary GameComponent tick, exactly as an incident worker would.
    public sealed class ScheduledDialog : GameComponent
    {
        private Action pending; private int dueTick;
        public ScheduledDialog(Game game) { }
        public void Schedule(Action open, int due) { pending = open; dueTick = due; }
        public override void GameComponentTick()
        {
            if (pending == null || Find.TickManager.TicksGame < dueTick) return;
            var open = pending; pending = null;
            open();
        }
    }
}

#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// Type into whatever dialog is on screen. RimWorld's text boxes are plain
    /// string fields redrawn every frame by `Widgets.TextField`, so setting the
    /// field IS typing: no keyboard event reaches the game from outside it.
    ///
    /// The target is the TOP-MOST window on `Find.WindowStack.Windows` - the
    /// last entry, which is the one drawn over everything else. A
    /// `MainTabWindow` or an `ImmediateWindow` is refused: those are the Work
    /// tab and the message/letter overlays, not a dialog anyone typed into.
    ///
    /// Two field shapes are handled, and both are found by reflection rather
    /// than by a per-dialog table:
    ///
    ///   * a plain `string` instance field declared on the window's own class or
    ///     any base up to but EXCLUDING `Verse.Window` - `Dialog_Rename.curName`,
    ///     `Dialog_GiveName.curName` / `.curSecondName`;
    ///   * `Verse.Dialog_NamePawn`, whose boxes live in a `List&lt;NameContext&gt;`
    ///     of a private nested class, one entry per name part; each holds the
    ///     text in `current` and the untranslated box label in `textboxName`
    ///     ("FirstName", "NickName", "LastName", "BackstoryTitle"). Those rows
    ///     are named `&lt;listField&gt;[&lt;index&gt;].&lt;sub&gt;` so `field` can address one.
    ///
    /// `Window`'s own strings (`optionalTitle`, the layer bookkeeping) are never
    /// offered: they are chrome, not input.
    ///
    /// Naming confirmation lives inside native DoWindowContents, not the
    /// Window.OnAcceptKeyPressed shortcut. Confirm through a freshly captured
    /// OK/Accept UI control so native name validation and final game writes run.
    ///
    /// Every reflection write is wrapped: an exception is reported as a refusal
    /// naming the type, never thrown out of the tool.
    /// </summary>
    public sealed class HomeDialogTextTools
    {
        private const string ToolName = "home/dialog_text";

        /// <summary>Field names tried, in order, when the caller names none and
        /// the window has more than one string field.</summary>
        private static readonly string[] PreferredFieldNames = { "curName", "name", "text", "newName" };

        /// <summary>The string field inside a name-context list element that
        /// holds the editable text.</summary>
        private static readonly string[] ContextTextFieldNames = { "current", "name" };

        /// <summary>The string field inside a name-context list element that
        /// labels which box it is.</summary>
        private static readonly string[] ContextLabelFieldNames = { "textboxName", "label", "name" };

        [Tool(
            ToolName,
            Title = "Type into the dialog that is on screen",
            Description =
                "Lists dialog string fields; writes reviewed naming inputs using exact windowId and field. Confirm using a fresh get_ui_layout OK/Accept control and click_ui_target; accept=true is refused. dryRun "
                + "defaulting to TRUE. Real keyboard input does not reach this game, so this is the only way to answer a "
                + "dialog that asks for typed text - naming a colony, a settlement, an animal, a storage zone. Call it with no "
                + "arguments (or list:true) to see what the open dialog holds: every string field it declares, with its current "
                + "value. Then call it again with text and, when there is more than one box, field. Refused when no window is "
                + "open and when the top window is a main tab or an immediate overlay rather than a dialog.",
            ResultDescription =
                "success, dryRun, applied, window (the window's type name), fields[] (name, before) for every string field the "
                + "dialog offers, set{field, before, after} for the one that was written, accepted:false, error.")]
        [ToolResponse("dryRun", "boolean", "True = nothing was written. Defaults to TRUE; a caller must pass dryRun:false deliberately.", Always = true)]
        [ToolResponse("applied", "boolean", "True only when a field was actually written. False on every dry run and every refusal.", Always = true)]
        [ToolResponse("window", "string", "The full type name of the top-most window on the stack, or null when there was none.", Always = true)]
        [ToolResponse("fields", "array", "Every string field the dialog offers: name, before, maxLength (null when no reviewed input limit is known). A Dialog_NamePawn's editable name boxes appear as listField[index].current; non-editable names are excluded. Empty means no typable field.", Always = true)]
        [ToolResponse("set", "object", "The field that was written: field, before, after. Null when nothing was set - a listing call, a dry run's refusal, or a refusal.", Nullable = true)]
        [ToolResponse("accepted", "boolean", "Always false: text input does not confirm a naming outcome. Use the native confirmation UI control and fresh game readback.", Always = true)]
        [ToolResponse("unknownArguments", "array", "Every argument key the caller sent that this tool does not declare, sorted, case-sensitively. Empty array = every key was recognised. The host's own _rimBridgeTimeoutMs is never listed.", Always = true)]
        [ToolResponse("unknownArgumentsWarning", "string", "Present only when unknownArguments is non-empty, or when the caller's raw keys could not be read at all - in which case the empty unknownArguments means 'not known', not 'nothing unknown'.", Nullable = true)]
        [ToolResponse("error", "string", "Why the call was refused. Null when it was not.", Nullable = true)]
        public async Task<object?> DialogText(
            IRimBridgeContext ctx,
            CancellationToken cancellationToken,
            [ToolParameter(Description = "The text to put in the box. Required unless list is true. An empty string is accepted and clears the box.")] string? text = null,
            [ToolParameter(Description = "Exact field name returned by the current listing. Required for a write; partial names and default selection are not supported.")] string? field = null,
            [ToolParameter(Description = "List the dialog's string fields and their current values without writing anything. The same listing rides on every reply, so this is only a way to ask for it without passing text.", DefaultValue = false)] bool list = false,
            [ToolParameter(Description = "Unsupported confirmation shortcut: true is refused before writing. Confirm with a freshly captured OK/Accept UI control instead.", DefaultValue = false)] bool accept = false,
            [ToolParameter(Description = "TRUE by default. Read the dialog's fields and report what WOULD be written without touching it. Pass false to actually type.", DefaultValue = true)] bool dryRun = true,
            [ToolParameter(Description = "Exact window ID returned by the listing; required for a write.")] int windowId = -1)
        {
            return BridgeCommon.WithUnknownArguments(
                await DialogTextCore(ctx, cancellationToken, text, field, list, accept, dryRun, windowId).ConfigureAwait(false),
                ctx, typeof(HomeDialogTextTools), ToolName);
        }

        private async Task<object> DialogTextCore(
            IRimBridgeContext ctx,
            CancellationToken cancellationToken,
            string? text,
            string? field,
            bool list,
            bool accept,
            bool dryRun, int windowId)
        {
            if (ctx?.MainThread == null)
                return Failure("No RimBridge main-thread dispatcher is available for this invocation.");

            // Companion tools dispatch with MarshalToMainThread = false, so the
            // window stack, the reflection write and the read-back all go inside
            // ONE hop: a frame in between would let the dialog redraw from a
            // field this call had not finished setting.
            return await ctx.MainThread
                .InvokeAsync(() => Run(text, field, list, accept, dryRun, windowId), cancellationToken)
                .ConfigureAwait(false);
        }

        // ================================================================= run

        private static object Run(string? text, string? field, bool list, bool accept, bool dryRun, int windowId)
        {
            var stack = BridgeCommon.Try(() => Find.WindowStack, (WindowStack?)null);
            if (stack == null)
                return Failure("Find.WindowStack is null, so there is no window to type into.");

            List<Window> windows;
            try
            {
                var all = stack.Windows;
                windows = all == null ? new List<Window>() : all.Where(w => w != null).ToList();
            }
            catch (Exception ex)
            {
                return Failure("WindowStack.Windows threw " + ex.GetType().Name + "; nothing was read or written.");
            }

            if (windows.Count == 0)
                return Failure("No window is open. Open the dialog first; this tool types into what is already on screen.");

            // Immediate overlays (messages/letters) can sit above a real modal.
            // Walk downward to the first actual dialog instead of letting an
            // overlay make that modal unreachable.
            var window = windows.LastOrDefault(w => !(w is ImmediateWindow) && !(w is MainTabWindow));
            if (window == null)
            {
                var topType = BridgeCommon.SafeString(() => windows[windows.Count - 1].GetType().FullName) ?? "unknown";
                return Refused(topType, "No dialog is open beneath the immediate/main-tab overlays.");
            }
            var typeName = BridgeCommon.SafeString(() => window.GetType().FullName) ?? "unknown";

            if (!dryRun && !list && text != null && window.ID != windowId)
                return Refused(typeName, "Dialog identity changed or was omitted; list fields again before writing.");
            // Only reviewed naming dialogs expose writable input slots. Other
            // string fields may be labels or internal state, not text boxes.
            var naming = window is Dialog_GiveName || window is Dialog_NamePawn
                || Chain(window.GetType()).Any(t => t.IsGenericType
                    && t.GetGenericTypeDefinition() == typeof(Dialog_Rename<>));
            if (!dryRun && !list && text != null && !naming)
                return Refused(typeName, "Text writes are reviewed only for naming dialogs; other dialogs are inspection-only.");
            if (!dryRun && accept)
                return Refused(typeName, "Naming confirmation requires the exact OK/Accept control from get_ui_layout, then click_ui_target. The accept-key shortcut does not execute native naming validation; no text was written.");
            Dictionary<string, object?> Reply(List<object> fields, bool preview, bool applied,
                Dictionary<string, object?>? set, bool accepted, string? error)
            {
                var reply = Payload(typeName, fields, preview, applied, set, accepted, error);
                reply["windowId"] = window.ID;
                reply["writable"] = naming;
                return reply;
            }

            List<Slot> slots;
            try
            {
                slots = Slots(window);
            }
            catch (Exception ex)
            {
                return Refused(typeName, "Reading " + typeName + "'s fields threw " + ex.GetType().Name
                    + "; nothing was written.");
            }

            var rows = slots.Select(s => (object)new Dictionary<string, object?>
            {
                { "name", s.Name },
                { "before", s.Read() },
                { "maxLength", s.MaxLength }
            }).ToList();

            if (list || text == null)
            {
                var reason = list
                    ? null
                    : "No text was given, so this is a listing only. Pass text (and field when fields[] has more than one row).";
                return Reply(rows, dryRun, false, null, false, reason);
            }

            if (slots.Count == 0)
                return Reply(rows, dryRun, false, null, false,
                    typeName + " declares no string field of its own, so there is nothing to type into.");

            Slot? chosen;
            string? why;
            if (!Choose(slots, field, out chosen, out why))
                return Reply(rows, dryRun, false, null, false, why);

            var before = chosen.Read();
            if (!dryRun && !(chosen.Name == "curName" || chosen.Name == "curSecondName"
                || (Chain(window.GetType()).Any(t => t.Name == "Dialog_NamePawn") && chosen.Name.Contains("["))))
                return Reply(rows, dryRun, false, null, false, "This field has not been reviewed as a naming input.");
            if (!chosen.MaxLength.HasValue)
                return Reply(rows, dryRun, false, null, false, "Native input limit is unavailable; nothing was written.");
            if (text.Length > chosen.MaxLength.Value)
                return Reply(rows, dryRun, false, null, false, "Text exceeds the native input limit of " + chosen.MaxLength.Value + " characters; nothing was written.");
            object? after = text;
            var applied = false;
            var accepted = false;

            if (!dryRun)
            {
                try
                {
                    chosen.Write(text);
                    applied = true;
                }
                catch (Exception ex)
                {
                    return Reply(rows, dryRun, false, null, false,
                        "Writing " + chosen.Name + " threw " + ex.GetType().Name + ". Nothing changed.");
                }

                after = chosen.Read();

            }

            return Reply(rows, dryRun, applied, Set(chosen.Name, before, after), accepted, null);
        }

        /// <summary>Which box to write. Exactly one candidate wins outright; a
        /// named field must match a row; otherwise the four conventional names
        /// are tried in order and, failing all of that, the candidates are
        /// listed rather than one of them guessed at.</summary>
        private static bool Choose(List<Slot> slots, string? field, [NotNullWhen(true)] out Slot? chosen, [NotNullWhen(false)] out string? why)
        {
            chosen = null;
            why = null;

            var matches = slots.Where(s => string.Equals(s.Name, field, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1)
            {
                why = "Use one exact field name from the current listing: " + Names(slots);
                return false;
            }
            chosen = matches[0];
            return true;
        }

        private static string Names(List<Slot> slots)
        {
            return string.Join(", ", slots.Select(s => s.Label == null ? s.Name : s.Name + " (" + s.Label + ")").ToArray());
        }

        // =============================================================== slots

        /// <summary>One writable box: a plain string field on the window, or one
        /// element's text field inside a name-context list.</summary>
        private sealed class Slot
        {
            internal Slot(string name, int? maxLength, Func<string?> read, Action<string> write)
            {
                Name = name;
                MaxLength = maxLength;
                Read = read;
                Write = write;
            }

            internal readonly string Name;
            internal string? Label;
            internal readonly int? MaxLength;
            internal readonly Func<string?> Read;
            internal readonly Action<string> Write;
        }

        /// <summary>Every box the top window offers, list rows included.</summary>
        private static List<Slot> Slots(Window window)
        {
            var slots = new List<Slot>();
            foreach (var type in Chain(window.GetType()))
            {
                foreach (var info in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                    | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (info.IsLiteral || info.IsInitOnly)
                        continue;
                    if (info.FieldType == typeof(string))
                    {
                        AddPlain(slots, window, info);
                        continue;
                    }
                    AddListRows(slots, window, info);
                }
            }
            return slots;
        }

        /// <summary>The window's own class and every base up to but EXCLUDING
        /// Verse.Window, most-derived first. Window's own strings are chrome.</summary>
        private static IEnumerable<Type> Chain(Type type)
        {
            for (var t = type; t != null && t != typeof(Window); t = t.BaseType)
                yield return t;
        }

        private static void AddPlain(List<Slot> slots, Window window, FieldInfo info)
        {
            if (slots.Any(s => string.Equals(s.Name, info.Name, StringComparison.Ordinal)))
                return;
            slots.Add(new Slot(
                info.Name,
                PlainLimit(window, info.Name),
                () => BridgeCommon.SafeString(() => (string?)info.GetValue(window)),
                value => info.SetValue(window, value)));
        }

        private static int? PlainLimit(Window window, string field)
        {
            string? property = window is Dialog_GiveName
                ? (field == "curName" ? "FirstCharLimit" : field == "curSecondName" ? "SecondCharLimit" : null)
                : field == "curName" ? "MaxNameLength" : null;
            if (property == null) return null;
            var info = Chain(window.GetType()).Select(t => t.GetProperty(property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .FirstOrDefault(p => p != null);
            if (info == null) return null;
            int? limit = BridgeCommon.TryN(() => (int)info.GetValue(window, null));
            // Dialog_Rename accepts text.Length strictly below MaxNameLength.
            return window is Dialog_GiveName ? limit : limit - 1;
        }

        /// <summary>Dialog_NamePawn keeps one box per name part in a List of a
        /// private nested class. An element whose `editable` field is false is a
        /// label the dialog draws, not a box, and is left out.</summary>
        private static void AddListRows(List<Slot> slots, Window window, FieldInfo info)
        {
            if (!typeof(IList).IsAssignableFrom(info.FieldType) || !info.FieldType.IsGenericType)
                return;

            var element = info.FieldType.GetGenericArguments().FirstOrDefault();
            if (element == null || element == typeof(string) || element.IsValueType)
                return;

            var textField = Named(element, ContextTextFieldNames);
            if (textField == null)
                return;
            var labelField = Named(element, ContextLabelFieldNames);
            var editableField = element.GetField("editable",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            IList? entries;
            try { entries = info.GetValue(window) as IList; }
            catch { return; }
            if (entries == null)
                return;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null)
                    continue;
                if (editableField != null && editableField.FieldType == typeof(bool)
                    && !BridgeCommon.Try(() => (bool)editableField.GetValue(entry), true))
                    continue;

                var target = entry;
                slots.Add(new Slot(
                    info.Name + "[" + i + "]." + textField.Name,
                    BridgeCommon.TryN(() => (int)element.GetField("maximumNameLength",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target)),
                    () => BridgeCommon.SafeString(() => (string?)textField.GetValue(target)),
                    value => textField.SetValue(target, value))
                {
                    Label = labelField == null
                        ? null
                        : BridgeCommon.SafeString(() => Text(labelField.GetValue(target)))
                });
            }
        }

        /// <summary>The first string field of `element` with one of these names.</summary>
        private static FieldInfo? Named(Type element, string[] names)
        {
            foreach (var name in names)
            {
                var info = element.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (info != null && info.FieldType == typeof(string) && !info.IsInitOnly && !info.IsLiteral)
                    return info;
            }
            return null;
        }

        /// <summary>A label field is a string on Dialog_NamePawn and a
        /// TaggedString elsewhere; both answer ToString().</summary>
        private static string? Text(object value)
        {
            return value == null ? null : value.ToString();
        }

        // ============================================================== replies

        private static Dictionary<string, object?> Set(string field, string? before, object? after)
        {
            return new Dictionary<string, object?>
            {
                { "field", field },
                { "before", before },
                { "after", after }
            };
        }

        private static Dictionary<string, object?> Payload(string window, List<object> fields, bool dryRun,
                                                          bool applied, Dictionary<string, object?>? set,
                                                          bool accepted, string? error)
        {
            return new Dictionary<string, object?>
            {
                { "success", true },
                { "tool", ToolName },
                { "dryRun", dryRun },
                { "applied", applied },
                { "window", window },
                { "fields", fields },
                { "fieldCount", fields.Count },
                { "set", set },
                { "accepted", accepted },
                { "error", error },
                { "note", dryRun
                    ? "NOTHING WAS WRITTEN. set{}.after is the value the write WOULD produce. Run again with dryRun:false to type it."
                    : "Applied. set{}.after was READ BACK from the field after the write." }
            };
        }

        /// <summary>A refusal that still names the window it looked at, so a
        /// caller can see what was on top.</summary>
        private static Dictionary<string, object?> Refused(string window, string error)
        {
            var payload = Failure(error);
            payload["window"] = window;
            payload["fields"] = new List<object>();
            payload["fieldCount"] = 0;
            payload["set"] = null;
            payload["accepted"] = false;
            return payload;
        }

        private static Dictionary<string, object?> Failure(string error)
        {
            var payload = BridgeCommon.Failure(ToolName, error);
            payload["dryRun"] = true;
            payload["applied"] = false;
            payload["window"] = null;
            payload["fields"] = new List<object>();
            payload["fieldCount"] = 0;
            payload["set"] = null;
            payload["accepted"] = false;
            return payload;
        }
    }
}

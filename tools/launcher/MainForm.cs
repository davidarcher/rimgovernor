using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RimGovernor.Launcher;

/// <summary>
/// The launcher window: a WebView2 showing the embedded page (<c>ui/index.html</c>, unchanged from the Go launcher),
/// with the app's actions bound as global functions that return promises, as the page expects. A call arrives as a
/// posted message {id, name, args}; the reply is {id, ok, result} or {id, ok: false, error}, and an error surfaces in
/// the page as an <c>Error</c> with that message.
/// </summary>
internal sealed class MainForm : Form
{
    private const int DefaultWidth = 860;
    private const int DefaultHeight = 760;

    private readonly LauncherApp _app;
    private readonly string _windowFile;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _remember = new() { Interval = 1000 };
    private readonly GameWindowWatcher _gameWindow;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _calls;
    private WindowRect _lastBounds;

    public MainForm(LauncherApp app)
    {
        _app = app;
        _windowFile = Path.Combine(app.PrivateDir, "launcher-window.json");
        _gameWindow = new GameWindowWatcher(Path.Combine(app.PrivateDir, "game-window.json"));
        Text = "RimGovernor";
        ClientSize = new Size(DefaultWidth, DefaultHeight);
        StartPosition = FormStartPosition.CenterScreen;
        if (WindowRect.Load(_windowFile) is { } saved && saved.OnScreen(SystemInformation.VirtualScreen))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(saved.X, saved.Y, saved.W, saved.H);
        }

        Controls.Add(_web);
        _calls = new Dictionary<string, Func<JsonElement, Task<object?>>>(StringComparer.Ordinal)
        {
            ["getState"] = _ => Result(app.View()),
            ["saveSettings"] = a => Run(() => app.SaveSettings(Arg<Settings>(a, 0))),
            ["focused"] = _ => Background(app.FocusAsync),
            ["play"] = _ => Background(app.PlayAsync),
            ["stopController"] = _ => Background(app.CloseGameAsync),
            ["restart"] = _ => Background(app.RestartAsync),
            ["closeGame"] = _ => Background(app.CloseGameAsync),
            ["getNewColony"] = _ => Result(app.Colony.View()),
            ["generateNewColony"] = a => Run(() => app.Colony.Generate(Arg<NewColonySpec>(a, 0))),
            ["cancelNewColony"] = _ => Background(app.Colony.CancelAsync),
            ["getAcceptCases"] = _ => Result(app.Accept.Cases()),
            ["getAcceptRun"] = _ => Result(app.Accept.View()),
            ["runAcceptCase"] = async a =>
            {
                await app.Accept.RunAsync(Arg<string>(a, 0), a.GetArrayLength() > 1 && a[1].ValueKind == JsonValueKind.True).ConfigureAwait(true);
                return null;
            },
            ["stopAcceptCase"] = _ => Run(app.Accept.Stop),
            ["openLink"] = a => Run(() => OpenLink(Arg<string>(a, 0))),
            ["getObserve"] = async a => await app.Watch.ViewAsync(Arg<long>(a, 0), _stop.Token).ConfigureAwait(true),
            ["setBot"] = async a =>
            {
                await app.Watch.SetBotAsync(Arg<bool>(a, 0), _stop.Token).ConfigureAwait(true);
                return null;
            },
        };
        Shown += async (_, _) => await InitializeWebViewAsync().ConfigureAwait(true);
        FormClosed += (_, _) => Shutdown();
    }

    private static Task<object?> Result(object value) => Task.FromResult<object?>(value);

    private static Task<object?> Run(Action action)
    {
        action();
        return Task.FromResult<object?>(null);
    }

    private static T Arg<T>(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index)
        {
            throw new LauncherException("missing argument " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        try
        {
            return args[index].Deserialize<T>(LauncherJson.Compact) ?? throw new LauncherException("missing argument " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (JsonException e)
        {
            throw new LauncherException("bad argument: " + e.Message, e);
        }
    }

    /// <summary>Starts <paramref name="work"/> and answers at once; a failure goes to the log panel (the page polls state, not a result).</summary>
    private Task<object?> Background(Func<Task> work)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A background action ends at the log panel; nothing above it can handle the failure.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _app.Log("error: " + e.Message);
                }
            },
            CancellationToken.None);
        return Task.FromResult<object?>(null);
    }

    private static void OpenLink(string url)
    {
        Links.RequireAllowed(url);
        _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_app.PrivateDir, "launcher-webview")).ConfigureAwait(true);
            await _web.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(
                this,
                "Could not open a WebView2 window. Install the Microsoft Edge WebView2 Runtime and try again.\n\n" + e.Message,
                "RimGovernor launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith("data:", StringComparison.Ordinal) && !e.Uri.StartsWith("about:", StringComparison.Ordinal))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        _ = await core.AddScriptToExecuteOnDocumentCreatedAsync(BindingScript()).ConfigureAwait(true);
        core.NavigateToString(PageHtml());

        _remember.Tick += (_, _) => RememberBounds();
        _remember.Start();
        _gameWindow.Start();
        _ = Task.Run(() => _app.FocusAsync(), CancellationToken.None);
        _ = Task.Run(() => _app.MonitorAsync(_stop.Token), CancellationToken.None);
    }

    private string BindingScript()
    {
        var names = JsonSerializer.Serialize(_calls.Keys);
        return $$"""
            (function () {
              const pending = new Map();
              let next = 0;
              window.chrome.webview.addEventListener('message', e => {
                const m = e.data;
                const p = pending.get(m.id);
                if (!p) { return; }
                pending.delete(m.id);
                if (m.ok) { p.resolve(m.result); } else { p.reject(new Error(m.error)); }
              });
              for (const name of {{names}}) {
                window[name] = (...args) => new Promise((resolve, reject) => {
                  const id = ++next;
                  pending.set(id, { resolve, reject });
                  window.chrome.webview.postMessage({ id, name, args });
                });
              }
            })();
            """;
    }

    private static string PageHtml()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.index.html")
            ?? throw new InvalidOperationException("the launcher page is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement id = default;
        object? reply;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement.Clone();
            id = message.GetProperty("id");
            var name = message.GetProperty("name").GetString() ?? "";
            if (!_calls.TryGetValue(name, out var call))
            {
                throw new LauncherException($"unknown call \"{name}\"");
            }

            var result = await call(message.GetProperty("args")).ConfigureAwait(true);
            reply = new Reply(id, true, result, null);
        }
        catch (Exception ex) when (ex is LauncherException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            reply = new Reply(id, false, null, ex.Message);
        }

        if (id.ValueKind != JsonValueKind.Undefined && _web.CoreWebView2 is { } core)
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(reply, LauncherJson.Compact));
        }
    }

    private void RememberBounds()
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }

        var now = new WindowRect(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
        if (now == _lastBounds || !now.OnScreen(SystemInformation.VirtualScreen))
        {
            return;
        }

        _lastBounds = now;
        now.Save(_windowFile);
    }

    private void Shutdown()
    {
        _stop.Cancel();
        _remember.Dispose();
        _gameWindow.Dispose();
    }

    private sealed record Reply(JsonElement Id, bool Ok, object? Result, string? Error);
}

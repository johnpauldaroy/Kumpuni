using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kumpuni;

internal sealed class WebWorkspace : UserControl
{
    private const string Origin = "https://app.kumpuni.invalid";
    private const string Home = Origin + "/index.html";
    private const string ContentPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self'; base-uri 'none'; frame-src 'none'; form-action 'none'";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly TechnicianPanel _technician;
    private readonly Func<Task> _openTools;
    private readonly Action<PrinterProfile> _saveProfile;
    private readonly Func<PrinterProfile, Action<QuickProgress>, Task<QuickOutcome>> _runProfile;
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PrinterProfile? _profile;
    private QuickOutcome? _lastReport;
    private string? _setupError;
    private bool _busy;

    public bool IsRepairing { get; private set; }

    public WebWorkspace(TechnicianPanel technician, Func<Task> openTools, Func<PrinterProfile?>? loadProfile = null,
        Action<PrinterProfile>? saveProfile = null, Func<PrinterProfile, Action<QuickProgress>, Task<QuickOutcome>>? runProfile = null)
    {
        Dock = DockStyle.Fill;
        BackColor = KumpuniTheme.Canvas;
        _technician = technician;
        _openTools = openTools;
        _saveProfile = saveProfile ?? PrinterProfileStore.Save;
        _runProfile = runProfile ?? QuickRepair.RunAsync;
        try { _profile = (loadProfile ?? PrinterProfileStore.Load)(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _setupError = "The saved printer could not be read. Select a printer to update the setup.";
        }
        _view.DefaultBackgroundColor = KumpuniTheme.Canvas;
        Controls.Add(_view);
    }

    public static bool CanHost()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<bool> InitializeAsync(string? dataDirectory = null)
    {
        try
        {
            string data = dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kumpuni", "WebView");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            if (IsDisposed)
                return false;
            await _view.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
                return false;
            CoreWebView2 core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.NavigationStarting += (_, args) => args.Cancel = !IsHome(args.Uri);
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) => ServeResource(environment, args);
            core.WebMessageReceived += ReceiveMessage;
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                    _loaded.TrySetResult(false);
            };
            core.Navigate(Home);
            return await _loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or IOException
            or UnauthorizedAccessException or TimeoutException or DllNotFoundException or WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    private static bool IsHome(string? uri) => Uri.TryCreate(uri, UriKind.Absolute, out Uri? value)
        && value.GetLeftPart(UriPartial.Path) == Home && value.Query.Length == 0;

    private static void ServeResource(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs args)
    {
        (string? Name, string? ContentType) asset = default;
        if (args.Request.Method == "GET" && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? uri)
            && uri.GetLeftPart(UriPartial.Authority) == Origin && uri.Query.Length == 0)
        {
            asset = uri.AbsolutePath switch
            {
                "/index.html" => ("index.html", "text/html; charset=utf-8"),
                "/styles.css" => ("styles.css", "text/css; charset=utf-8"),
                "/desktop.css" => ("desktop.css", "text/css; charset=utf-8"),
                "/desktop.js" => ("desktop.js", "text/javascript; charset=utf-8"),
                "/assets/logo.png" => ("logo.png", "image/png"),
                "/assets/office-printer.webp" => ("office-printer.webp", "image/webp"),
                "/assets/office-printer-640.webp" => ("office-printer-640.webp", "image/webp"),
                "/vendor/lenis/lenis.css" => ("lenis.css", "text/css; charset=utf-8"),
                "/vendor/lenis/lenis.min.js" => ("lenis.min.js", "text/javascript; charset=utf-8"),
                "/vendor/gsap/gsap.min.js" => ("gsap.min.js", "text/javascript; charset=utf-8"),
                "/vendor/gsap/ScrollTrigger.min.js" => ("ScrollTrigger.min.js", "text/javascript; charset=utf-8"),
                _ => default
            };
        }

        Stream? response = asset.Name is string name
            ? typeof(WebWorkspace).Assembly.GetManifestResourceStream("Kumpuni.Ui." + name) : null;
        args.Response = response is null
            ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
            : environment.CreateWebResourceResponse(response, 200, "OK",
                $"Content-Type: {asset.ContentType}\r\nContent-Security-Policy: {ContentPolicy}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store");
        // WebView2 reads the response stream after the event returns.
    }

    private async void ReceiveMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsHome(args.Source) || args.WebMessageAsJson.Length > 4096)
            return;

        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement data = document.RootElement;
            if (ReadString(data, "type") is not string type)
                return;

            // A native SaveFileDialog must open after the WebView event has returned.
            await Task.Yield();
            if (IsDisposed)
                return;

            if (type == "ready")
            {
                SendProfile();
                _loaded.TrySetResult(true);
                return;
            }
            if (_busy)
                return;
            _busy = true;
            try
            {
                switch (type)
                {
                    case "refresh": await RefreshAsync(); break;
                    case "save": SaveProfile(data); break;
                    case "repair": await RepairAsync(); break;
                    case "check-queue": await CheckQueueAsync(data); break;
                    case "export-repair":
                        if (_profile is not null && _lastReport is not null)
                            Export("Kumpuni-printer-check.json", QuickRepair.Serialize(_profile, _lastReport), "repair");
                        break;
                    case "export-inventory":
                        if (_technician.CreateReport() is string json)
                            Export("Kumpuni-technician-report.json", json, "inventory");
                        break;
                    case "advanced": await _openTools(); break;
                }
            }
            finally
            {
                _busy = false;
                Send(new { type = "idle" });
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or COMException
            or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Send(new { type = "error", message = "Windows could not complete the action. Try again." });
        }
    }

    private async Task RefreshAsync()
    {
        await _technician.RefreshInventoryAsync();
        PublishInventory();
    }
    public void PublishInventory()
    {
        if (_technician.Snapshot is not ConsoleSnapshot snapshot)
        {
            Send(new { type = "inventory", status = _technician.InventoryStatus, available = false });
            return;
        }
        Send(new
        {
            type = "inventory", status = _technician.InventoryStatus, available = true,
            queues = snapshot.Queues().Select(queue =>
            {
                QueueProbe? probe = _technician.ReadCheck(queue.Name)?.Probe;
                QueueDetails? details = probe?.Details ?? queue.Details;
                PortKind kind = ConsoleHealth.Kind(details, snapshot.Ports.Items);
                return new
                {
                    queue.Name, Shared = queue.Scope == QueueScope.SharedConnection, Scope = ConsoleHealth.ScopeText(queue.Scope),
                    Connection = kind == PortKind.Unknown ? ConsoleHealth.ScopeText(queue.Scope) : ConsoleHealth.PortKindText(kind),
                    Status = ConsoleHealth.StatusText(details, probe), Health = ConsoleHealth.Health(details, probe).ToString(),
                    Jobs = details?.Jobs
                };
            }),
            drivers = snapshot.Drivers.Items.Select(driver => new { driver.Name, driver.Environment, driver.Version }),
            ports = snapshot.Ports.Items.Select(port => new
            {
                port.Name, Kind = ConsoleHealth.PortKindText(ConsoleHealth.GetPortKind(port.Name, port.Monitor)), port.Monitor
            })
        });
    }

    private async Task CheckQueueAsync(JsonElement data)
    {
        if (ReadString(data, "queue") is not string name)
            return;
        await _technician.CheckQueueAsync(name);
        PublishInventory();
        Send(new { type = "queue-result", message = _technician.InventoryStatus });
    }

    private void SaveProfile(JsonElement data)
    {
        string input = (ReadString(data, "queue") ?? "").Trim();
        Target? target = Target.Parse(input);
        QueueEntry? installed = _technician.Snapshot?.Queues()
            .FirstOrDefault(queue => queue.Scope != QueueScope.SharedConnection && Diagnose.Same(queue.Name, input));
        bool savedLocal = _profile is { Shared: false } && Diagnose.Same(_profile.Queue, input);
        if (target is null && installed is null && !savedLocal)
        {
            Send(new { type = "setup-error", message = "Select an installed printer or enter a valid shared printer path." });
            return;
        }

        bool makeDefault = data.TryGetProperty("makeDefault", out JsonElement value) && value.ValueKind == JsonValueKind.True;
        var profile = new PrinterProfile(target?.Path ?? installed?.Name ?? _profile!.Queue, target is not null, makeDefault);
        try { _saveProfile(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Send(new { type = "setup-error", message = "Windows could not save the printer setup." });
            return;
        }
        _profile = profile;
        _lastReport = null;
        _setupError = null;
        SendProfile(saved: true);
    }

    private void SendProfile(bool saved = false)
    {
        Target? target = _profile is { Shared: true } ? Target.Parse(_profile.Queue) : null;
        Send(new
        {
            type = "profile", saved, error = _setupError,
            profile = _profile is null ? null : new
            {
                _profile.Queue, _profile.Shared, _profile.MakeDefault,
                Name = target?.Share ?? _profile.Queue,
                Location = target?.Host ?? "This PC"
            }
        });
    }

    private async Task RepairAsync()
    {
        if (_profile is not PrinterProfile profile)
            return;
        IsRepairing = true;
        _lastReport = null;
        try
        {
            QuickOutcome outcome = await _runProfile(profile,
                progress => Send(new { type = "progress", phase = (int)progress.Phase, progress.Message }));
            _lastReport = outcome;
            bool inspected = outcome.Ready || outcome.SharedReport is not null || outcome.LocalBefore is not null;
            bool checkedAfterRepair = outcome.SharedRun?.Final is not null || outcome.LocalAfter is not null;
            KnownIssue[] issues = await ReadKnownIssuesAsync(outcome);
            Send(new
            {
                type = "outcome", outcome.Ready, outcome.Title, outcome.Detail, outcome.Steps, outcome.RanAt,
                inspected, checkedAfterRepair,
                knownIssues = issues.Select(issue => new { issue.Title, issue.Detail, status = issue.StatusText, issue.Url })
            });
        }
        finally { IsRepairing = false; }
    }

    private static async Task<KnownIssue[]> ReadKnownIssuesAsync(QuickOutcome outcome)
    {
        try
        {
            Machine machine = outcome.SharedReport?.Machine ?? Machine.Read();
            return KnownIssues.Match(await KnownIssues.LoadAsync(), machine, KnownIssues.RelevantCode(outcome.SharedReport, null));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Security.SecurityException) { return []; }
    }

    private void Export(string fileName, string json, string scope)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = fileName, Filter = "JSON report (*.json)|*.json", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, json);
            Send(new { type = "exported", scope, message = "Redacted report saved." });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Send(new { type = "exported", scope, message = "Windows could not save the report." });
        }
    }

    private void Send<T>(T message)
    {
        if (!IsDisposed && _view.CoreWebView2 is { } core && IsHome(core.Source))
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    private static string? ReadString(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static int CheckBinding(string? captureFolder)
    {
        using var form = new Form
        {
            ClientSize = new Size(1120, 750), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-25000, -25000)
        };
        using var technician = new TechnicianPanel(_ => { }, () => null);
        PrinterProfile? saved = null;
        int repairRuns = 0;
        bool openedAdvanced = false;
        using var web = new WebWorkspace(technician, () =>
        {
            openedAdvanced = true;
            return Task.CompletedTask;
        }, () => null, profile => saved = profile, (profile, update) =>
        {
            repairRuns++;
            update(new(QuickPhase.Diagnose, "Fixture queue check"));
            return Task.FromResult(new QuickOutcome("Fixture ready", "Windows queue verification sample.", true, [], DateTimeOffset.UtcNow));
        });

        string stage = "load";
        string? error = null;
        var results = new Dictionary<string, bool>();
        form.Controls.Add(web);
        form.Shown += async (_, _) =>
        {
            try
            {
                string data = Path.Combine(Path.GetTempPath(), "Kumpuni-WebUiCheck", Guid.NewGuid().ToString("N"));
                if (!await web.InitializeAsync(data))
                    throw new InvalidOperationException("WebView2 did not load the packaged workspace.");
                CoreWebView2 core = web._view.CoreWebView2;
                await Expect("FirstRunDialog", "document.querySelector('#setup').open && document.querySelector('#my-printer h1').textContent === 'My printer'");
                await Expect("MockupStylesAndImage", "getComputedStyle(document.querySelector('.hero')).backgroundColor === 'rgb(18, 59, 46)' && document.querySelector('.hero-photo').naturalWidth > 0");
                await Expect("LiveInventory", "document.querySelector('#printerSelect').options.length > 1 && !document.querySelector('#inventoryReport').disabled");
                await Expect("SetupActionVisible", "document.querySelector('#setupForm [type=submit]').getBoundingClientRect().bottom < innerHeight");
                await Expect("AdvancedActionVisible", "document.querySelector('#nativeTools').getBoundingClientRect().height > 0");
                await Capture("Kumpuni-first-run.png");

                await core.ExecuteScriptAsync("document.querySelector('#printerPathInput').value = 'invalid path'; document.querySelector('#setupForm').requestSubmit();");
                await Expect("InvalidSetupRejected", "document.querySelector('#setup').open && document.querySelector('#setupStatus').textContent.includes('valid shared')");
                await core.ExecuteScriptAsync("document.querySelector('#printerPathInput').value = " +
                    JsonSerializer.Serialize(@"\\FIXTURE.invalid\Office printer") + "; document.querySelector('#setupForm').requestSubmit();");
                await Expect("SavedPrinterDashboard", "!document.querySelector('#setup').open && document.querySelector('#printerName').textContent === 'Office printer' && !document.querySelector('#run').disabled");
                results["NativeProfileSaved"] = saved is { Shared: true } && saved.Queue == @"\\FIXTURE.invalid\Office printer";
                await Capture("Kumpuni-saved-printer.png");

                await core.ExecuteScriptAsync("document.querySelector('#run').click()");
                await Expect("OneClickOutcome", "document.querySelector('#runStatus').textContent === 'Fixture ready' && !document.querySelector('#report').disabled");
                results["CalledNativeWorkflow"] = repairRuns == 1;
                await core.ExecuteScriptAsync("document.querySelector('#driversTab').click()");
                await Expect("DriverInventory", "document.querySelector('#inventory table caption').textContent === 'Windows printer drivers'");
                await core.ExecuteScriptAsync("document.querySelector('#portsTab').click()");
                await Expect("PortInventory", "document.querySelector('#inventory table caption').textContent === 'Windows printer ports'");
                await core.ExecuteScriptAsync("document.querySelector('#nativeTools').click()");
                await Expect("AdvancedToolsAction", "!document.querySelector('#nativeTools').disabled");
                results["AdvancedToolsCallback"] = openedAdvanced;

                async Task Expect(string name, string condition)
                {
                    stage = name;
                    for (int attempt = 0; attempt < 120; attempt++)
                    {
                        if (await core.ExecuteScriptAsync($"Boolean({condition})") == "true")
                        {
                            results[name] = true;
                            return;
                        }
                        await Task.Delay(100);
                    }
                    throw new InvalidOperationException("The expected WebView state did not appear.");
                }

                async Task Capture(string file)
                {
                    if (captureFolder is null)
                        return;
                    await using FileStream image = File.Create(Path.Combine(captureFolder, file));
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                }
            }
            catch (Exception failure)
            {
                error = $"{stage}: {failure.Message}";
            }
            finally { form.Close(); }
        };
        Application.Run(form);
        return Program.Write(new { Checks = results, Error = error }, error is null && results.Values.All(value => value) ? 0 : 1);
    }

}

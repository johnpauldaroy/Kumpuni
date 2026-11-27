using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

internal sealed class MainForm : Form
{
    private readonly ComboBox _operation = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _path = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, MaxLength = 512 };
    private readonly TextBox _code = new() { Dock = DockStyle.Fill, MaxLength = 24, PlaceholderText = "Optional, e.g. 0x00000709" };
    private readonly Button _diagnose = MakeButton("Diagnose only");
    private readonly Button _automatic = MakeButton("Check and repair");
    private readonly CheckBox _restorePrivacy = new()
    {
        AutoSize = true, Text = "Restore RPC privacy if explicitly disabled (this print host only)"
    };
    private readonly Button _repair = MakeButton("Apply repair");
    private readonly Button _settings = MakeButton("Windows printer settings");
    private readonly Button _stageDriver = MakeButton("Stage approved driver");
    private readonly Button _export = MakeButton("Export report");
    private readonly Button _printed = MakeButton("Confirm page printed");
    private readonly RichTextBox _results = new()
    {
        ReadOnly = true, Dock = DockStyle.Fill, DetectUrls = false, WordWrap = true,
        BackColor = Color.White, ForeColor = KumpuniTheme.Ink, ScrollBars = RichTextBoxScrollBars.Vertical,
        AccessibleName = "Printer diagnosis and repair results"
    };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 3, 0, 0) };
    private readonly System.Windows.Forms.Timer _waiting = new() { Interval = 12000 };
    private readonly TechnicianPanel _technician;
    private readonly TechnicianActionsPanel _technicianActions = new();
    private readonly TabControl _workspace = new() { Dock = DockStyle.Fill };
    private readonly QuickRepairPanel _home = new();
    private readonly Icon _windowIcon = KumpuniBrand.LoadIcon();
    private readonly PictureBox _brandMark = new()
    {
        Width = 40, Height = 40, SizeMode = PictureBoxSizeMode.Zoom, TabStop = false
    };
    private readonly Button _homeNav = NavigationButton("My printer");
    private readonly Button _toolsNav = NavigationButton("Technician console");
    private WebWorkspace? _webWorkspace;
    private bool _showingTools;
    private bool _toolsInitialized;
    private readonly TabPage _sharedRepair = new("Shared printer repair");
    private readonly TabPage _technicianTab = new("Technician console");
    private Report? _report;
    private Decision? _decision;
    private Target? _target;
    private ActionResult? _lastResult;
    private SharedRepairRun? _run;
    private KnownIssue[] _knownIssues = [];
    private readonly List<DriverDeploymentResult> _deployments = [];
    private uint? _reportedCode;
    private TaskKind _task;
    private bool _busy;
    private bool _mutating;
    private bool _pageConfirmed;

    public MainForm()
    {
        Text = "Kumpuni";
        Icon = _windowIcon;
        _brandMark.Image = KumpuniBrand.LoadLogo();
        Font = new Font("Segoe UI", 10);
        BackColor = KumpuniTheme.Canvas;
        ForeColor = KumpuniTheme.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(890, 640);
        ClientSize = new Size(1120, 750);

        _operation.Items.AddRange(["Connect to a shared printer", "Set the default printer", "Connected but jobs do not print"]);
        _operation.SelectedIndex = 0;
        _operation.AccessibleName = "What failed";
        _path.AccessibleName = "Shared printer path";
        _code.AccessibleName = "Reported Windows error";

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(22)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var help = new PrinterHelpPanel(OpenHelpQueues, OpenGuidedSharedRepair, OpenSettings);
        _technician = new TechnicianPanel(OpenSharedRepair, () => help.Evidence);
        _technicianTab.Controls.Add(_technician);
        var helpTab = new TabPage("All-Round Printer Help");
        helpTab.Controls.Add(help);
        _sharedRepair.Controls.Add(root);
        var actionsTab = new TabPage("Technician actions");
        actionsTab.Controls.Add(_technicianActions);
        _workspace.TabPages.AddRange([_technicianTab, helpTab, _sharedRepair, actionsTab]);
        BuildShell();

        var heading = new Label
        {
            Text = "Shared printer repair", AutoSize = true, Padding = new Padding(0, 0, 0, 12),
            Font = new Font("Segoe UI", 20, FontStyle.Bold)
        };
        root.Controls.Add(heading, 0, 0);

        var inputs = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 4 };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddInput(inputs, "What failed", _operation, 0);
        AddInput(inputs, @"Shared printer (\\HOST\SHARE)", _path, 1);
        AddInput(inputs, "Reported Windows error", _code, 2);
        inputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inputs.Controls.Add(_restorePrivacy, 1, 3);
        root.Controls.Add(inputs, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 10)
        };
        KumpuniTheme.StylePrimary(_automatic);
        actions.Controls.AddRange([_automatic, _diagnose, _repair, _stageDriver, _settings, _export, _printed]);
        root.Controls.Add(actions, 0, 2);
        root.Controls.Add(_results, 0, 3);
        root.Controls.Add(_status, 0, 4);
        _workspace.SelectedIndexChanged += (_, _) => SyncDefaultAction();
        _home.DefaultActionChanged += (_, _) => SyncDefaultAction();

        _diagnose.Click += async (_, _) => await DiagnoseAsync(applyRepairs: false);
        _automatic.Click += async (_, _) => await DiagnoseAsync(applyRepairs: true);
        _repair.Click += async (_, _) => await RepairAsync();
        _stageDriver.Click += async (_, _) => await StageDriverAsync();
        _settings.Click += (_, _) => OpenSettings(_status);
        _export.Click += (_, _) => ExportReport();
        _printed.Click += (_, _) => { _pageConfirmed = true; Render(); };
        _operation.SelectedIndexChanged += (_, _) => InvalidateDiagnosis();
        _path.TextChanged += (_, _) =>
        {
            _restorePrivacy.Checked = false;
            _deployments.Clear();
            InvalidateDiagnosis();
        };
        _code.TextChanged += (_, _) => InvalidateDiagnosis();
        Shown += async (_, _) => await InitializeWorkspaceAsync();
        _waiting.Tick += (_, _) =>
        {
            _waiting.Stop();
            _status.Text = "Windows is still processing this action. Wait before trying another repair.";
        };
        InvalidateDiagnosis();
        ShowTechnicianTools(false);
    }

    private void BuildShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        var brand = KumpuniTheme.Label("Kumpuni", 15, true);
        int brandWidth = _brandMark.Width + TextRenderer.MeasureText(brand.Text, brand.Font).Width + 56;
        int navWidth = Math.Max(brandWidth, Math.Max(205, TextRenderer.MeasureText(_toolsNav.Text, _toolsNav.Font).Width + 60));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, navWidth));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebarFrame = new Panel { Dock = DockStyle.Fill, BackColor = KumpuniTheme.Line, Padding = new Padding(0, 0, 1, 0), Margin = Padding.Empty };
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(15, 24, 15, 18) };
        var nav = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var lockup = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = new Padding(7, 0, 0, 22)
        };
        lockup.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lockup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lockup.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _brandMark.Anchor = AnchorStyles.Left;
        _brandMark.Margin = new Padding(0, 0, 8, 0);
        brand.ForeColor = KumpuniTheme.Primary;
        brand.Anchor = AnchorStyles.Left;
        brand.Margin = Padding.Empty;
        lockup.Controls.Add(_brandMark, 0, 0);
        lockup.Controls.Add(brand, 1, 0);
        nav.Controls.Add(lockup);
        nav.Controls.Add(_homeNav);
        nav.Controls.Add(_toolsNav);
        sidebar.Controls.Add(nav);
        sidebarFrame.Controls.Add(sidebar);
        shell.Controls.Add(sidebarFrame, 0, 0);

        var work = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = KumpuniTheme.Canvas };
        work.Controls.Add(_workspace);
        work.Controls.Add(_home);
        shell.Controls.Add(work, 1, 0);
        Controls.Add(shell);

        _homeNav.Click += (_, _) =>
        {
            if (_webWorkspace is null)
                ShowTechnicianTools(false);
            else if (!_busy && !_technician.IsPrinting && !_technicianActions.IsMutating)
            {
                _webWorkspace.Visible = true;
                _webWorkspace.BringToFront();
                _webWorkspace.PublishInventory();
                AcceptButton = null;
            }
        };
        _toolsNav.Click += async (_, _) => await OpenAdvancedToolsAsync();
    }

    private async Task InitializeWorkspaceAsync()
    {
        if (WebWorkspace.CanHost())
        {
            var web = new WebWorkspace(_technician, OpenAdvancedToolsAsync);
            _webWorkspace = web;
            Controls.Add(web);
            web.BringToFront();
            AcceptButton = null;
            if (await web.InitializeAsync() || IsDisposed)
                return;
            _webWorkspace = null;
            Controls.Remove(web);
            web.Dispose();
        }

        if (!IsDisposed)
        {
            ShowTechnicianTools(false);
            await _home.InitializeAsync();
        }
    }

    private async Task OpenAdvancedToolsAsync()
    {
        if (_webWorkspace is not null)
            _webWorkspace.Visible = false;
        ShowTechnicianTools(true);
        if (!_toolsInitialized)
        {
            _toolsInitialized = true;
            await Task.WhenAll(LoadConnectionsAsync(), _technician.RefreshInventoryAsync());
        }
    }

    private void ShowTechnicianTools(bool show)
    {
        _showingTools = show;
        _workspace.Visible = show;
        _home.Visible = !show;
        (show ? (Control)_workspace : _home).BringToFront();
        StyleNavigation(_homeNav, !show);
        StyleNavigation(_toolsNav, show);
        SyncDefaultAction();
    }

    private void SyncDefaultAction() => AcceptButton = _webWorkspace?.Visible == true ? null : _showingTools
        ? _workspace.SelectedTab == _sharedRepair ? _diagnose : null : _home.DefaultAction;

    private static void StyleNavigation(Button button, bool selected)
    {
        button.BackColor = selected ? KumpuniTheme.Tint : Color.White;
        button.ForeColor = selected ? KumpuniTheme.Primary : KumpuniTheme.Muted;
    }

    private static Button NavigationButton(string text)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Top, Height = 43, TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(9, 0, 0, 0), Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = KumpuniTheme.Tint;
        return button;
    }

    internal bool CheckBrandingAssets() =>
        _brandMark.Parent is not null && _brandMark.Image is Bitmap { Width: > 0, Height: > 0 } && Icon == _windowIcon;

    internal bool CheckHomeNavigation()
    {
        ShowTechnicianTools(false);
        return !_showingTools && AcceptButton == _home.DefaultAction;
    }

    internal bool CheckTechnicianActionsBinding() => _technicianActions.CheckBinding();

    internal bool CheckSharedRepairBinding()
    {
        ShowTechnicianTools(true);
        _ = _workspace.Handle;
        _workspace.SelectedTab = _sharedRepair;
        _path.Text = @"\\fixture.invalid\Printer";
        if (!_automatic.Enabled || !_diagnose.Enabled || _restorePrivacy.Enabled || _repair.Enabled)
            return false;
        _path.Text = @"\\localhost\Printer";
        if (!_restorePrivacy.Enabled)
            return false;
        _restorePrivacy.Checked = true;
        _path.Text = @"\\fixture.invalid\Another printer";
        return !_restorePrivacy.Enabled && !_restorePrivacy.Checked && !_stageDriver.Enabled && AcceptButton == _diagnose;
    }

    internal bool HasActiveWork => _busy || _mutating || _home.IsRunning || _webWorkspace?.IsRepairing == true
        || _technician.IsPrinting || _technicianActions.IsMutating;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if ((_mutating || _home.IsRunning || _webWorkspace?.IsRepairing == true || _technician.IsPrinting || _technicianActions.IsMutating) && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = MessageBox.Show(this,
                "Windows is still processing the repair. Closing Kumpuni leaves the repair running. Diagnose again before retrying.\n\nClose Kumpuni?",
                "Repair in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _waiting.Dispose();
            Image? logo = _brandMark.Image;
            _brandMark.Image = null;
            logo?.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
            _windowIcon.Dispose();
    }

    private void OpenHelpQueues(PrinterConnection connection)
    {
        _workspace.SelectedTab = _technicianTab;
        _technician.ShowQueues(connection);
    }

    private void OpenGuidedSharedRepair(string path)
    {
        if (!_busy && !Diagnose.Same(Target.Parse(_path.Text)?.Path, path))
        {
            _operation.SelectedIndex = (int)TaskKind.Connect;
            _code.Clear();
        }
        OpenSharedRepair(path);
    }

    private void OpenSharedRepair(string path)
    {
        _workspace.SelectedTab = _sharedRepair;
        if (!_busy)
        {
            _path.Text = path;
            _path.Focus();
        }
    }

    private async Task LoadConnectionsAsync()
    {
        try
        {
            Inventory? inventory = (await Worker.RunAsync("--inventory"))?.Inventory;
            if (inventory is null || IsDisposed)
                return;

            foreach (string connection in inventory.Connections.Where(value => Target.Parse(value) is not null))
            {
                if (!_path.Items.Contains(connection))
                    _path.Items.Add(connection);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or JsonException)
        {
            // An unavailable list does not prevent entering a share directly.
        }
    }

    private async Task DiagnoseAsync(bool applyRepairs)
    {
        if (_busy)
            return;

        Target? target = Target.Parse(_path.Text);
        if (target is null)
        {
            _status.Text = @"Enter the host and its exact printer share name as \\HOST\SHARE.";
            _path.Focus();
            return;
        }

        uint? code = Diagnose.ParseReported(_code.Text);
        if (_code.Text.Trim().Length != 0 && code is null)
        {
            _status.Text = "Enter a Windows error number, for example 1801 or 0x00000709.";
            _code.Focus();
            return;
        }

        TaskKind task = (TaskKind)_operation.SelectedIndex;
        bool localHost = SharingSettings.IsLocalHost(target);
        bool restorePrivacy = localHost && _restorePrivacy.Checked;
        if (applyRepairs && !ConfirmAutomaticRun(target, task, localHost, restorePrivacy))
            return;

        InvalidateDiagnosis();
        SetBusy(true, "Checking the selected queue and Windows settings.");
        try
        {
            Report? report = await ReadDiagnosisAsync(target);
            if (IsDisposed)
                return;
            if (report is null)
            {
                _results.Text = "The diagnostic worker did not return a result. Check that the host is available and try again.";
                return;
            }

            _report = report;
            _target = target;
            _reportedCode = code;
            _task = task;
            _decision = Diagnose.Choose(task, target, report, code);
            _knownIssues = await ReadKnownIssuesAsync(report, code);
            if (applyRepairs)
            {
                _mutating = true;
                _waiting.Start();
                _run = await SharedRepair.ExecuteAsync(task, target, report, code, restorePrivacy,
                    action => ExecuteActionAsync(target, action), () => ReadDiagnosisAsync(target));
                if (IsDisposed)
                    return;

                _report = _run.Final ?? report;
                _decision = Diagnose.Choose(task, target, _report, code);
                _lastResult = _run.Steps.LastOrDefault()?.Result;
            }
            Render();
        }
        finally
        {
            if (!IsDisposed)
            {
                _waiting.Stop();
                _mutating = false;
                SetBusy(false, _run?.Summary ?? (_report is null ? "Diagnosis incomplete." : "Diagnosis complete."));
            }
        }
    }

    private bool ConfirmAutomaticRun(Target target, TaskKind task, bool localHost, bool restorePrivacy)
    {
        string scope = localHost
            ? "Kumpuni can start an eligible stopped Print Spooler or Server service, turn on File and Printer Sharing for the active adapters, "
                + "and set a Public network to Private with File and Printer Sharing allowed and Windows Firewall on. Windows may ask for administrator approval."
            : "Kumpuni can start the local Print Spooler, turn on Client for Microsoft Networks, connect or reconnect this printer as the current user, and verify the result. "
                + "Windows may ask you to sign in to the print host. Continue if you trust this print host.";
        if (!localHost && task == TaskKind.Default)
            scope += " It may set this user's default printer.";
        if (!localHost && task == TaskKind.Print)
            scope += " It may send one test page.";
        if (restorePrivacy)
            scope += $"\n\nIf explicitly disabled, set {SharingSettings.PrivacyValue} from 0 to 1. Arrange a Spooler or host restart afterwards.";
        return MessageBox.Show(this, $"{target.Path}\n\n{scope}", "Check and repair",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    private static async Task<KnownIssue[]> ReadKnownIssuesAsync(Report report, uint? code)
    {
        try { return KnownIssues.Match(await KnownIssues.LoadAsync(), report.Machine, KnownIssues.RelevantCode(report, code)); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { return []; }
    }

    private static async Task<Report?> ReadDiagnosisAsync(Target target)
    {
        try { return (await Worker.RunAsync("--diagnose", target.Path))?.Report; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<ActionResult> ExecuteActionAsync(Target target, Repair action)
    {
        if (!IsDisposed)
            _status.Text = $"{ActionLabel(action)}. Waiting for Windows...";
        try
        {
            if (action == Repair.SignInToHost)
                return HostSignIn.SignIn(target, Handle);
            ActionResult? result = Diagnose.Elevated(action)
                ? await Worker.RunElevatedRepairAsync(target, action)
                : (await Worker.RunAsync("--repair", target.Path, action))?.Action;
            return result ?? new("The worker could not confirm the result. Check Windows before retrying.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or JsonException or IOException)
        {
            return new($"The action outcome could not be confirmed ({error.GetType().Name}).",
                Code: (error as Win32Exception)?.NativeErrorCode);
        }
    }

    private async Task RepairAsync()
    {
        if (_decision is null || _target is null || _decision.Action == Repair.None)
            return;

        Repair action = _decision.Action;
        string prompt = action switch
        {
            Repair.Connect => "Connect this shared printer for the current Windows user? Use only a print host approved by your organization or trusted by you.",
            Repair.Reconnect => "Remove and re-add this user's connection to the selected printer? Kumpuni first checks for queued jobs and attempts to restore the connection if adding it fails.",
            Repair.SetDefault => "Set this queue as the default printer for the current Windows user?",
            Repair.TestPage => "Send one test page to the selected printer?",
            Repair.StartSpooler => "Start the Print Spooler on this PC? Proceed only if printing is authorized on this device. Windows may ask for elevation.",
            Repair.EnableSmbClient => "Turn on Client for Microsoft Networks for the active network adapters? Windows asks for administrator approval.",
            Repair.EnableSmbServer => "Turn on File and Printer Sharing for Microsoft Networks for the active network adapters on this print host? Windows asks for administrator approval.",
            Repair.SecureHostSharing => "Allow File and Printer Sharing on Private networks, set the current Public network to Private and turn Windows Firewall on for Private networks? "
                + "Only continue on a network you trust, such as your office or home network. Windows asks for administrator approval.",
            Repair.SignInToHost => "Sign in to this print host through Windows? Windows asks for an account that has access to the host. "
                + "Select Remember my credentials to keep the sign-in. Kumpuni does not see or store the password.",
            _ => "Apply this repair?"
        };
        if (MessageBox.Show(this, $"{_target.Path}\n\n{prompt}", ActionLabel(action),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        _mutating = true;
        _waiting.Start();
        SetBusy(true, "Waiting for Windows to complete the selected action.");
        try
        {
            _lastResult = await ExecuteActionAsync(_target, action);
        }
        finally
        {
            if (!IsDisposed)
            {
                _waiting.Stop();
                _mutating = false;
                Render();
                SetBusy(false, "Action finished. Diagnose again before applying another repair.");
            }
        }
    }

    private async Task StageDriverAsync()
    {
        if (_busy || _target is not Target target || _report is null || _report.Policy?.ProtectedPrintRequired == true)
            return;

        using var picker = new OpenFileDialog
        {
            Title = "Select an approved printer driver INF from a local drive",
            Filter = "Printer driver INF (*.inf)|*.inf",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        string path = picker.FileName;
        if (MessageBox.Show(this, $"Stage this approved printer package on this PC?\n\n{path}\n\n"
            + "Windows may request administrator approval. This adds the package to the Driver Store. You can then retry the queue.",
            "Stage approved driver", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        _mutating = true;
        _waiting.Start();
        SetBusy(true, "Waiting for the Windows driver helper.");
        try
        {
            DriverDeploymentResult result = await DriverDeployment.RequestAsync(path);
            if (IsDisposed)
                return;
            _deployments.Add(result);
            if (result.Verified && await ReadDiagnosisAsync(target) is Report fresh && !IsDisposed)
            {
                _report = fresh;
                _decision = Diagnose.Choose(_task, target, fresh, _reportedCode);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _waiting.Stop();
                _mutating = false;
                Render();
                SetBusy(false, _deployments.LastOrDefault()?.Message ?? "The driver helper did not complete.");
            }
        }
    }

    private void Render()
    {
        if (_report is not Report report || _decision is not Decision decision || _target is not Target target)
            return;

        string selectedDefault = report.Inventory is not { DefaultError: 0 } inventory ? "Unknown"
            : Diagnose.Same(inventory.Default, target.Path) ? "Selected queue"
            : inventory.Default is null ? "None" : "Another printer";

        var output = new StringBuilder();
        output.AppendLine(decision.Finding).AppendLine($"Owner: {decision.Owner}").AppendLine(decision.Details);
        if (_run is SharedRepairRun run)
        {
            output.AppendLine().AppendLine(run.Summary);
            foreach (SharedRepairStep step in run.Steps)
            {
                output.AppendLine($"[{step.FinishedAt.LocalDateTime:t}] {ActionLabel(step.Action)}. {step.Result.Message}");
                if (step.Result.Code is int code)
                    output.AppendLine($"Windows error: {ErrorText(code)}");
                if (step.StateMatches == false)
                    output.AppendLine("The follow-up check did not see the expected state.");
            }
        }

        if (_knownIssues.Length > 0)
        {
            output.AppendLine().AppendLine("Known Windows issues for this build");
            foreach (KnownIssue issue in _knownIssues)
            {
                output.AppendLine($"- {issue.Title} ({issue.StatusText})").AppendLine($"  {issue.Detail}");
                if (issue.Url is string url)
                    output.AppendLine($"  {url}");
            }
        }

        foreach (DriverDeploymentResult deployment in _deployments)
            output.AppendLine().AppendLine($"Driver package [{deployment.AttemptedAt.LocalDateTime:g}]: {deployment.Message} Exit code {deployment.Code}.");

        string evidenceHeading = _run is not null ? _run.Final is null ? "Evidence before repair" : "Latest diagnostic evidence"
            : _lastResult is null ? "Diagnostic evidence" : "Evidence before the action";
        output.AppendLine().AppendLine(evidenceHeading)
            .AppendLine($"Queue: {target.Path}")
            .AppendLine($"Windows: build {report.Machine.Version} ({report.Machine.Edition}, {report.Machine.Architecture})")
            .AppendLine($"Local spooler: {ConsoleHealth.SpoolerText(report.Inventory?.SpoolerState)}")
            .AppendLine($"This user's connection: {ConnectionText(report.Inventory, target)}")
            .AppendLine($"Current default: {selectedDefault}")
            .AppendLine($"Host name resolution: {ProbeText(report.Network?.NameResolved, "Resolved", "Did not resolve")}")
            .AppendLine($"TCP 135 (RPC endpoint mapper): {ProbeText(report.Network?.RpcMapper, "Reached", "Did not respond")}")
            .AppendLine($"TCP 445 (SMB): {ProbeText(report.Network?.Smb, "Reached", "Did not respond")}")
            .AppendLine($"Live Windows print queue: {QueueText(report)}");
        AppendConnectivity(output, report.Connectivity);

        if (_reportedCode is uint reported)
            output.AppendLine($"Reported error: {_code.Text.Trim()} (normalized: 0x{reported:X8}, {reported})");

        if (report.Policy is PrintPolicy policy)
        {
            output.AppendLine($"Protected print required by policy: {PolicyText(policy.ProtectedPrintRequired)}")
                .AppendLine($"Administrator-only driver install policy: {PolicyText(policy.DriverInstallationAdminOnly)}")
                .AppendLine($"Configured outgoing print RPC transport: {policy.RpcTransport ?? "No setting found"}");
        }
        else
            output.AppendLine("Print policy settings: unavailable");

        AppendSharingEvidence(output, report.Sharing);
        AppendAdvancedEvidence(output, report.Advanced, report.Sharing?.LocalHost == true);

        if (report.Events.Length > 0)
            output.AppendLine($"Recent matching PrintService/Admin event IDs: {string.Join(", ", report.Events.Select(item => item.Id).Distinct())}");
        else
            output.AppendLine($"PrintService/Admin: {(report.EventsAvailable ? "No matching recent events" : "Unavailable")}");

        if (_lastResult is ActionResult result)
        {
            if (_run is null)
            {
                output.AppendLine().AppendLine(result.Message);
                if (result.Code is int error)
                    output.AppendLine($"Windows error: {ErrorText(error)}");
            }
            if (result.JobState is not null)
                output.AppendLine($"Test page: {(_pageConfirmed ? "Physical output confirmed by the user" : result.JobState)}");
        }

        _results.Text = output.ToString();
        _results.SelectionStart = 0;
        _results.ScrollToCaret();
        SyncButtons();
    }

    private static void AppendSharingEvidence(StringBuilder output, SharingEvidence? sharing)
    {
        output.AppendLine().AppendLine("Sharing settings on this PC");
        if (sharing is null)
        {
            output.AppendLine("Windows did not return sharing settings.");
            return;
        }

        output.AppendLine(sharing.LocalHost ? "The selected host is this PC." : "Run this check on the print host to inspect its settings.");
        if (sharing.LocalHost)
            output.AppendLine($"Local printer share: {ProbeText(sharing.SharedQueuePresent, "Published", "Not found")}");
        output.AppendLine($"LanmanServer: {ServiceText(sharing.Server)}")
            .AppendLine($"Windows Defender Firewall service: {ServiceText(sharing.FirewallService)}");
        if (sharing.Firewall is FirewallReading firewall)
            output.AppendLine($"Active firewall profiles: {Profiles(firewall.ActiveProfiles)}; enabled locally: {Profiles(firewall.EnabledLocally)}.");
        else
            output.AppendLine("Firewall profile status: unavailable");
        output.AppendLine($"{SharingSettings.PrivacyValue}: {SettingText(sharing.Privacy)}")
            .AppendLine($"Outgoing RpcAuthentication: {SettingText(sharing.RpcAuthentication)}")
            .AppendLine($"Incoming RpcProtocols: {SettingText(sharing.RpcProtocols)}")
            .AppendLine($"RegisterSpoolerRemoteRpcEndPoint: {SettingText(sharing.RemoteSpooler)}")
            .AppendLine($"RpcTcpPort: {SettingText(sharing.RpcTcpPort)}")
            .AppendLine($"ForceKerberosForRpc: {SettingText(sharing.RpcKerberos)}")
            .AppendLine($"SMB server overrides: SMB1 {SettingText(sharing.Smb1)}, SMB2 {SettingText(sharing.Smb2)}, require signing {SettingText(sharing.ServerSigning)}")
            .AppendLine($"SMB client overrides: require signing {SettingText(sharing.ClientSigning)}, AllowInsecureGuestAuth policy {SettingText(sharing.GuestPolicy)}");

        static string Profiles(int mask)
        {
            var names = new List<string>();
            if ((mask & 1) != 0) names.Add("Domain");
            if ((mask & 2) != 0) names.Add("Private");
            if ((mask & 4) != 0) names.Add("Public");
            return names.Count == 0 ? "None" : string.Join(", ", names);
        }

        static string ServiceText(ServiceReading reading) => reading.Error != 0 ? ErrorText(reading.Error)
            : $"{ConsoleHealth.SpoolerText(reading.State)} (startup {reading.Start switch
            {
                2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown"
            }})";
        static string SettingText(RegistrySetting setting) => setting.Error != 0 ? $"Unavailable ({setting.Error})"
            : setting.Value?.ToString() ?? "No explicit DWORD override";
    }

    private static void AppendAdvancedEvidence(StringBuilder output, AdvancedSharing? advanced, bool isHost)
    {
        output.AppendLine().AppendLine("Firewall, discovery, queue and storage");
        if (advanced is null)
        {
            output.AppendLine("Additional Windows evidence is unavailable.");
            return;
        }
        output.AppendLine($"Network Discovery provider (fdPHost): {ServiceState(advanced.DiscoveryProvider)}")
            .AppendLine($"Network Discovery publication (FDResPub): {ServiceState(advanced.DiscoveryPublication)}");
        if (advanced.FirewallRules is FirewallRules rules)
        {
            output.AppendLine(rules.Store == "ActiveStore" ? "Built-in rules on active profiles (effective policy store)."
                : $"Built-in rules on active profiles (local rules). Effective policy query: {ErrorText(rules.ActiveStoreError)}. Run on the host as administrator for ActiveStore.");
            output.AppendLine($"SMB inbound: {RuleText(rules.Smb)}")
                .AppendLine($"RPC endpoint mapper inbound: {RuleText(rules.RpcMapper)}")
                .AppendLine($"Print Spooler RPC inbound: {RuleText(rules.PrintRpc)}")
                .AppendLine($"Network Discovery inbound: {RuleText(rules.DiscoveryInbound)}; outbound: {RuleText(rules.DiscoveryOutbound)}");
            if (rules.CustomInbound is RuleCounts custom)
                output.AppendLine($"Custom inbound rules with explicit print ports: {RuleText(custom)}");
            else if (rules.CustomRulesError is int error)
                output.AppendLine($"Custom print-port rules: {ErrorText(error)}.");
            output.AppendLine("Rule counts describe candidates on active profiles. Verify remote scope, overrides and actual client access.");
        }
        else
            output.AppendLine($"Firewall rule audit: {ErrorText(advanced.FirewallReadError)}");

        SpoolStorage spool = advanced.Spool;
        output.AppendLine($"Spool folder source: {spool.Source}. Folder check: {(spool.FolderError == 0 ? "No error reported" : ErrorText(spool.FolderError))}.");
        output.AppendLine(spool.FreeBytes is ulong free && spool.TotalBytes is ulong capacity
            ? $"Spool volume: {free / 1073741824d:F2} GiB free of {capacity / 1073741824d:F2} GiB."
            : spool.VolumeError == 0 ? "Spool volume information: unavailable." : $"Spool volume information: {ErrorText(spool.VolumeError)}");

        if (advanced.Queue is QueueAdministration queue)
        {
            QueuePermissions security = queue.Permissions;
            output.AppendLine(security.Error == 0
                ? $"Queue discretionary ACL: {security.Dacl}, {security.AceCount?.ToString() ?? "unknown"} entries."
                : $"Queue discretionary ACL: {ErrorText(security.Error)}.");
            output.AppendLine($"Current account Manage Printers check: {(security.ManageError == 0 ? "Granted" : ErrorText(security.ManageError))}.");
            QueueJobs jobs = queue.Jobs;
            output.AppendLine(jobs.Error == 0
                ? $"Queue jobs sampled: {jobs.Sampled}, {jobs.Blocked} need attention, {jobs.Paused} paused, {jobs.Printing} spooling or printing."
                    + (jobs.SampleLimitReached ? " Sample limit reached." : "")
                : $"Queue jobs: {ErrorText(jobs.Error)}.");
        }
        else if (isHost)
            output.AppendLine("The named local shared queue was unavailable for ACL and job inspection.");

        if (advanced.Driver is DriverReadiness driver)
        {
            output.AppendLine(driver.InventoryError == 0
                ? $"Registered drivers matching the queue and this Windows architecture: {driver.MatchingDriverCount}. Model: {driver.DriverModel?.ToString() ?? "unknown"}; version: {driver.Version ?? "unknown"}."
                : $"Driver inventory: {ErrorText(driver.InventoryError)}.");
        }
        else
            output.AppendLine("The queue did not provide a driver model to compare with the local inventory.");

        static string ServiceState(ServiceReading service) => service.Error == 0
            ? ConsoleHealth.SpoolerText(service.State) : ErrorText(service.Error);
        static string RuleText(RuleCounts counts) => $"{counts.Allow} allow, {counts.Block} block, {counts.Disabled} disabled";
    }

    private void ExportReport()
    {
        if (_report is not Report report || _target is not Target target || _decision is not Decision decision)
            return;

        using var save = new SaveFileDialog
        {
            Title = "Export redacted printer report", FileName = "Kumpuni-printer-report.json",
            Filter = "JSON report (*.json)|*.json", AddExtension = true, OverwritePrompt = true
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllText(save.FileName,
                SharedRepair.SerializeReport(target, _task, _reportedCode, report, decision, _lastResult, _pageConfirmed, _run, _deployments));
            _status.Text = "Redacted report exported.";
        }
        catch (IOException exception) { _status.Text = $"Report could not be saved ({exception.GetType().Name})."; }
        catch (UnauthorizedAccessException) { _status.Text = "Windows denied writing to the selected location."; }
    }

    private static void OpenSettings(Label status)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:printers") { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { status.Text = "Open Printers & scanners from Windows Settings."; }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _status.Text = status;
        UseWaitCursor = busy;
        _path.Enabled = _operation.Enabled = _code.Enabled = !busy;
        SyncButtons();
    }

    private void SyncButtons()
    {
        _diagnose.Enabled = _automatic.Enabled = !_busy;
        _restorePrivacy.Enabled = !_busy && Target.Parse(_path.Text) is Target target && SharingSettings.IsLocalHost(target);
        _repair.Enabled = !_busy && _decision is { Action: not Repair.None } && _lastResult is null;
        _repair.Text = _decision is null ? "Apply repair" : ActionLabel(_decision.Action);
        _export.Enabled = !_busy && _report is not null;
        _stageDriver.Enabled = !_busy && _report is not null && _report.Policy?.ProtectedPrintRequired != true;
        _printed.Visible = !_busy && !_pageConfirmed && _lastResult is { Verified: true, JobState: not null };
    }

    private void InvalidateDiagnosis()
    {
        _report = null;
        _decision = null;
        _target = null;
        _lastResult = null;
        _run = null;
        _pageConfirmed = false;
        _results.Text = "Enter a shared printer and choose what failed. Check and repair applies eligible changes after confirmation. Diagnose only collects evidence.";
        _status.Text = "Ready";
        SyncButtons();
    }

    private static string ConnectionText(Inventory? inventory, Target target) => inventory is not { InventoryError: 0 }
        ? "Unknown" : inventory.Connections.Any(value => Diagnose.Same(value, target.Path)) ? "Present" : "Absent";

    private static string QueueText(Report report)
    {
        if (report.Queue is not Observation queue)
            return report.UnavailableReason ?? "Unknown";
        if (queue.Code != 0)
            return ErrorText(queue.Code);
        if (queue.DetailsError != 0)
            return $"Opened; properties failed with {ErrorText(queue.DetailsError)}";
        return queue.QueueStatus is uint status
            ? $"Opened; status 0x{status:X8}, queued jobs {queue.Jobs}"
            : "Opened; queue properties unavailable";
    }

    private static void AppendConnectivity(StringBuilder output, ConnectivityEvidence? link)
    {
        if (link is null)
        {
            output.AppendLine("Session checks: unavailable");
            return;
        }
        foreach (AddressProbe address in link.Addresses)
            output.AppendLine($"{address.Family}: TCP 445 {(address.Smb ? "reached" : "no response")}, TCP 135 {(address.RpcMapper ? "reached" : "no response")}");
        output.AppendLine("File-sharing session (net view): " + (link.Session switch
        {
            null => "Unknown",
            { Error: 0 } session => $"Opened, {session.PrinterShares} shared printer(s), selected share {(session.SharePublished == true ? "listed" : "not listed")}",
            { Error: int error } => ErrorText(error)
        }));
        output.AppendLine("Host print server (print RPC): " + (link.PrintServerError switch
        {
            null => "Unknown", 0 => "Opened", int error => ErrorText(error)
        }));
        if (link.Health is NetworkHealth health)
        {
            output.AppendLine($"Network type: {(health.Categories.Length == 0 ? "Unknown" : string.Join(", ", health.Categories))}");
            foreach (AdapterBinding adapter in health.Adapters)
                output.AppendLine($"Adapter {adapter.Adapter}: Client for Microsoft Networks {OnOff(adapter.Client)}, File and Printer Sharing {OnOff(adapter.Server)}");
            output.AppendLine($"Security products: {(health.FirewallProducts.Concat(health.AntivirusProducts).Distinct().ToArray() is { Length: > 0 } products ? string.Join(", ", products) : "None reported")}");
        }
        else
        {
            output.AppendLine($"Network adapters and security products: unavailable ({ErrorText(link.HealthError)})");
        }
        if (link.SmbClientEvents.Length > 0)
            output.AppendLine($"SMB client events in the last 30 minutes: {string.Join(", ", link.SmbClientEvents)}");

        static string OnOff(bool? value) => value switch { true => "on", false => "OFF", null => "not installed" };
    }

    private static string PolicyText(bool? value) => value switch { true => "Yes", false => "No", null => "No setting found" };
    private static string ErrorText(int code) => $"0x{unchecked((uint)code):X8} ({code}) {new Win32Exception(code).Message}";
    private static string ProbeText(bool? outcome, string yes, string no) => outcome switch { true => yes, false => no, null => "Unknown" };
    internal static string ActionLabel(Repair repair) => repair switch
    {
        Repair.Connect => "Connect printer",
        Repair.Reconnect => "Reconnect printer",
        Repair.SetDefault => "Set default printer",
        Repair.TestPage => "Send test page",
        Repair.StartSpooler => "Start local spooler",
        Repair.StartHostSpooler => "Start print host spooler",
        Repair.StartLanmanServer => "Start Server service (LanmanServer)",
        Repair.RestoreRpcPrivacy => "Restore RPC privacy",
        Repair.SignInToHost => "Sign in to print host",
        Repair.EnableSmbClient => "Turn on Client for Microsoft Networks",
        Repair.EnableSmbServer => "Turn on File and Printer Sharing",
        Repair.SecureHostSharing => "Set network to Private and turn on the firewall",
        _ => "Apply repair"
    };

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(7, 3, 7, 3), Margin = new Padding(0, 0, 7, 4)
    };

    private static void AddInput(TableLayoutPanel panel, string text, Control control, int row)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label
        {
            Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 0, 10, 0)
        }, 0, row);
        panel.Controls.Add(control, 1, row);
    }
}

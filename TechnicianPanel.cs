using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

internal sealed class TechnicianPanel : UserControl
{
    private readonly Label _system = new() { AutoSize = true };
    private readonly Label _message = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _filter = new() { Width = 240, PlaceholderText = "Filter this tab", AccessibleName = "Filter inventory" };
    private readonly ComboBox _connection = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Width = 155, AccessibleName = "Queue connection type",
        Margin = new Padding(0, 4, 6, 4)
    };
    private readonly Button _refresh = Button("Refresh inventory");
    private readonly Button _check = Button("Check queue");
    private readonly Button _test = Button("Send test page");
    private readonly Button _printed = Button("Confirm page printed");
    private readonly Button _repair = Button("Open shared repair");
    private readonly Button _export = Button("Export redacted report");
    private readonly TabControl _inventory = new() { Dock = DockStyle.Fill };
    private readonly TabPage _queuePage = new("Queues");
    private readonly TabPage _driverPage = new("Drivers");
    private readonly TabPage _portPage = new("Ports");
    private readonly DataGridView _queues = Grid("Printer queues",
        ("Queue", 27), ("Scope", 16), ("Windows status", 21), ("Jobs", 7), ("Driver", 17), ("Port", 12));
    private readonly DataGridView _drivers = Grid("Installed print drivers",
        ("Driver", 45), ("Environment", 20), ("Model", 15), ("Version", 20));
    private readonly DataGridView _ports = Grid("Configured print ports",
        ("Port", 30), ("Type", 15), ("Monitor", 25), ("Description", 30));
    private readonly RichTextBox _detail = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = true, BackColor = Color.White,
        AccessibleName = "Selected queue details", ScrollBars = RichTextBoxScrollBars.Vertical
    };

    private readonly Dictionary<string, ConsoleCheck> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConsoleTest> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _openSharedRepair;
    private ConsoleSnapshot? _snapshot;
    private bool _busy;
    private bool _rendering;

    private readonly Func<PrinterHelpEvidence?> _readHelpEvidence;

    public TechnicianPanel(Action<string> openSharedRepair, Func<PrinterHelpEvidence?> readHelpEvidence)
    {
        _openSharedRepair = openSharedRepair;
        _readHelpEvidence = readHelpEvidence;
        Dock = DockStyle.Fill;
        BackColor = KumpuniTheme.Canvas;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        layout.Controls.Add(new Label { Text = "Printer technician console", AutoSize = true,
            Font = new Font("Segoe UI", 19, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(_system, 0, 1);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 7, 0, 7)
        };
        KumpuniTheme.StylePrimary(_refresh);
        _filter.Margin = new Padding(0, 4, 6, 4);
        _connection.Items.AddRange(["All connections", "USB queues", "Network queues", "Shared queues"]);
        _connection.SelectedIndex = (int)PrinterConnection.All;
        toolbar.Controls.AddRange([_refresh, _export, _filter, _connection]);
        layout.Controls.Add(toolbar, 0, 2);

        var queueLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(5) };
        queueLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 63));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        queueLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 37));
        queueLayout.Controls.Add(_queues, 0, 0);

        var queueActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 5, 0, 5) };
        queueActions.Controls.AddRange([_check, _test, _printed, _repair]);
        queueLayout.Controls.Add(queueActions, 0, 1);
        queueLayout.Controls.Add(_detail, 0, 2);
        _queuePage.Controls.Add(queueLayout);
        _driverPage.Controls.Add(_drivers);
        _portPage.Controls.Add(_ports);
        _inventory.TabPages.AddRange([_queuePage, _driverPage, _portPage]);
        layout.Controls.Add(_inventory, 0, 3);
        layout.Controls.Add(_message, 0, 4);

        _refresh.Click += async (_, _) => await RefreshInventoryAsync();
        _check.Click += async (_, _) => await CheckSelectedAsync();
        _test.Click += async (_, _) => await TestSelectedAsync();
        _printed.Click += (_, _) => ConfirmOutput();
        _repair.Click += (_, _) =>
        {
            if (SelectedQueue()?.Name is string path)
                _openSharedRepair(path);
        };
        _export.Click += (_, _) => ExportReport();
        _filter.TextChanged += (_, _) => RenderInventory();
        _connection.SelectedIndexChanged += (_, _) => RenderInventory();
        _queues.SelectionChanged += (_, _) =>
        {
            if (!_rendering)
                ShowSelection();
        };
        _inventory.SelectedIndexChanged += (_, _) => ShowSelection();
        _system.Text = "This PC and the shared printer connections visible to the current Windows user.";
        _message.Text = "Waiting for an inventory scan.";
        ShowSelection();
    }

    public bool IsPrinting { get; private set; }
    internal ConsoleSnapshot? Snapshot => _snapshot;
    internal string InventoryStatus => _message.Text;
    internal ConsoleCheck? ReadCheck(string name) => _checks.GetValueOrDefault(name);

    internal string? CreateReport() => _snapshot is ConsoleSnapshot snapshot
        ? ConsoleReport.Serialize(snapshot, _checks, _tests, _readHelpEvidence()) : null;

    internal Task CheckQueueAsync(string name) =>
        CheckQueueAsync(_snapshot?.Queues().FirstOrDefault(queue => Diagnose.Same(queue.Name, name)));

    public void ShowQueues(PrinterConnection connection)
    {
        _inventory.SelectedTab = _queuePage;
        _filter.Clear();
        _connection.SelectedIndex = (int)connection;
    }

    public async Task RefreshInventoryAsync()
    {
        if (_busy || IsDisposed)
            return;

        SetBusy(true);
        _message.Text = "Reading Windows spooler inventory...";
        try
        {
            ConsoleSnapshot? snapshot = (await Worker.RunAsync("--console-scan"))?.Console;
            if (IsDisposed)
                return;
            if (snapshot is null)
            {
                _message.Text = "Windows did not return the inventory. Refresh to try again.";
                return;
            }

            ApplySnapshot(snapshot);
            _message.Text = DescribeInventory(snapshot);
        }
        catch (Exception error) when (IsWorkerError(error))
        {
            _message.Text = $"The inventory worker could not complete ({error.GetType().Name}).";
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false);
        }
    }

    private Task CheckSelectedAsync() => CheckQueueAsync(SelectedQueue());

    private async Task CheckQueueAsync(QueueEntry? queue)
    {
        if (_busy || queue is null)
            return;
        SetBusy(true);
        _message.Text = "Checking the selected Windows queue...";
        try
        {
            ConsoleCheck? check = (await Worker.RunAsync("--console-probe", queue.Name))?.Check;
            if (IsDisposed)
                return;
            if (check is null)
            {
                _message.Text = "Windows did not complete the queue check. Check availability and try again.";
                return;
            }

            _checks[queue.Name] = check;
            RenderInventory();
            _message.Text = check.Probe.OpenError == 0 && check.Probe.DetailsError == 0
                ? "Queue check complete." : "Queue check found a Windows error.";
        }
        catch (Exception error) when (IsWorkerError(error))
        {
            _message.Text = $"The queue check could not complete ({error.GetType().Name}).";
        }
        finally
        {
            if (!IsDisposed)
                SetBusy(false);
        }
    }

    private async Task TestSelectedAsync()
    {
        if (_busy || _snapshot is null || SelectedQueue() is not QueueEntry queue
            || !ConsoleHealth.MaySendTest(Details(queue), _snapshot.Ports.Items, _checks.GetValueOrDefault(queue.Name)?.Probe))
            return;
        if (MessageBox.Show(this, $"Send one test page to this queue?\n\n{queue.Name}", "Send test page",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        IsPrinting = true;
        SetBusy(true);
        _message.Text = "Waiting for Windows to submit the test page...";
        ActionResult? result = null;
        try
        {
            result = (await Worker.RunAsync("--console-test", queue.Name, printJob: true))?.Action;
        }
        catch (Exception error) when (IsWorkerError(error))
        {
            result = new($"The test worker could not confirm the outcome ({error.GetType().Name}). Check the queue before retrying.",
                JobState: "Submission uncertain");
        }
        finally
        {
            IsPrinting = false;
            if (!IsDisposed)
            {
                result ??= new("Windows did not confirm submission. Check the queue before retrying.", JobState: "Submission uncertain");
                _tests[queue.Name] = new(DateTimeOffset.Now, result);
                _message.Text = result.Message;
                SetBusy(false);
                ShowSelection();
            }
        }
    }

    private void ConfirmOutput()
    {
        if (SelectedQueue() is QueueEntry queue
            && _tests.TryGetValue(queue.Name, out ConsoleTest? test) && test.Result.Verified)
        {
            _tests[queue.Name] = test with { PageConfirmed = true };
            _message.Text = "Physical output confirmed.";
            ShowSelection();
        }
    }

    private void ApplySnapshot(ConsoleSnapshot snapshot)
    {
        _snapshot = snapshot;
        _checks.Clear();
        RenderInventory();
    }

    internal static bool CheckInventoryBinding()
    {
        using var panel = new TechnicianPanel(_ => { }, () => null);
        var queue = new QueueEntry("Test queue", QueueScope.Local, new("Test driver", "USB001", null, 0, 1, 0));
        var shared = new QueueEntry(@"\\TESTHOST\Another queue", QueueScope.SharedConnection);
        var network = new QueueEntry("Network queue", QueueScope.SharedHere, queue.Details! with { Port = "WSD-1" });
        var snapshot = new ConsoleSnapshot(DateTimeOffset.UtcNow, new("Professional", 26300, null, "X64", true),
            new(4, 0, null, 0), new([queue, network], 0), new([shared], 0),
            new([new("Test driver", "Windows x64", 4, "1.2.3.4")], 0), new([new("USB001", "USB Monitor", null, 3)], 0), null);

        panel.ApplySnapshot(snapshot);
        if (panel._queues.Rows.Count != 3 || panel._drivers.Rows.Count != 1 || panel._ports.Rows.Count != 1)
            return false;

        panel._filter.Text = "USB001";
        if (panel._queues.Rows.Count != 1 || panel._ports.Rows.Count != 1 || panel._drivers.Rows.Count != 0)
            return false;
        panel._queues.CurrentCell = panel._queues.Rows[0].Cells[0];
        panel.ShowSelection();
        if (!panel._test.Enabled || panel._repair.Enabled || !panel._detail.Text.Contains("Jobs: 1", StringComparison.Ordinal))
            return false;

        panel.ShowQueues(PrinterConnection.Network);
        if (panel._queues.Rows.Count != 1 || panel.SelectedQueue()?.Name != network.Name)
            return false;
        panel.ShowQueues(PrinterConnection.Shared);
        if (panel._queues.Rows.Count != 2)
            return false;
        panel.ShowQueues(PrinterConnection.Usb);
        return panel._queues.Rows.Count == 1 && panel.SelectedQueue()?.Name == queue.Name;
    }

    private void RenderInventory()
    {
        if (_snapshot is not ConsoleSnapshot snapshot)
            return;

        string search = _filter.Text.Trim();
        string? selected = SelectedQueue()?.Name;
        _rendering = true;
        try
        {
            _queues.Rows.Clear();
            var connection = (PrinterConnection)_connection.SelectedIndex;
            DataGridViewRow? selection = null;
            foreach (QueueEntry queue in snapshot.Queues())
            {
                QueueDetails? details = Details(queue);
                QueueProbe? probe = _checks.GetValueOrDefault(queue.Name)?.Probe;
                string status = ConsoleHealth.StatusText(details, probe);
                string scope = ConsoleHealth.ScopeText(queue.Scope);
                if (!ConsoleHealth.MatchesConnection(connection, queue.Scope, details, snapshot.Ports.Items)
                    || !Matches(search, queue.Name, scope, status, details?.Driver, details?.Port, details?.Share))
                    continue;

                object jobs = details is null ? DBNull.Value : details.Jobs;
                int index = _queues.Rows.Add(queue.Name, scope, status, jobs, details?.Driver ?? "", details?.Port ?? "");
                DataGridViewRow row = _queues.Rows[index];
                row.Tag = queue;
                if (Diagnose.Same(queue.Name, selected))
                    selection = row;
                if (ConsoleHealth.Health(details, probe) == QueueHealth.Attention)
                    row.Cells[2].Style.ForeColor = Color.FromArgb(153, 58, 25);
            }
            if (_queues.Rows.Count != 0)
                _queues.CurrentCell = (selection ?? _queues.Rows[0]).Cells[0];

            _drivers.Rows.Clear();
            foreach (DriverEntry driver in snapshot.Drivers.Items.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (Matches(search, driver.Name, driver.Environment, driver.Version))
                    _drivers.Rows.Add(driver.Name, driver.Environment, driver.Model, driver.Version ?? "");
            }

            _ports.Rows.Clear();
            foreach (PortEntry port in snapshot.Ports.Items.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                string kind = ConsoleHealth.PortKindText(ConsoleHealth.GetPortKind(port.Name, port.Monitor));
                if (Matches(search, port.Name, kind, port.Monitor, port.Description))
                    _ports.Rows.Add(port.Name, kind, port.Monitor ?? "", port.Description ?? "");
            }

            string hostType = snapshot.Machine.Workstation ? "Windows workstation" : "Windows Server";
            _system.Text = $"{hostType} {snapshot.Machine.Version}  |  Spooler {ConsoleHealth.SpoolerText(snapshot.Spooler?.State)}"
                + $"  |  {snapshot.Queues().Length} queues, {snapshot.Drivers.Items.Length} drivers, {snapshot.Ports.Items.Length} ports";
        }
        finally { _rendering = false; }
        ShowSelection();
    }

    private void ShowSelection()
    {
        QueueEntry? queue = SelectedQueue();
        if (_snapshot is not ConsoleSnapshot snapshot || queue is null)
        {
            _detail.Text = _snapshot is not null && _queues.Rows.Count == 0
                ? "No installed queues match the current filters. Refresh after adding a printer in Windows."
                : "Select an installed printer queue to inspect it.";
            SyncControls();
            return;
        }

        ConsoleCheck? check = _checks.GetValueOrDefault(queue.Name);
        QueueDetails? details = Details(queue);
        var text = new StringBuilder().AppendLine(queue.Name)
            .AppendLine($"{ConsoleHealth.ScopeText(queue.Scope)}  |  {ConsoleHealth.StatusText(details, check?.Probe)}")
            .AppendLine($"Driver: {details?.Driver ?? "Unavailable"}")
            .AppendLine($"Port: {details?.Port ?? "Unavailable"}  |  Type: {ConsoleHealth.PortKindText(ConsoleHealth.Kind(details, snapshot.Ports.Items))}")
            .AppendLine($"Jobs: {details?.Jobs.ToString() ?? "Unknown"}");
        if (details?.Share is string share)
            text.AppendLine($"Share name: {share}");
        if (snapshot.Spooler is { DefaultError: 0 } spooler && Diagnose.Same(spooler.DefaultQueue, queue.Name))
            text.AppendLine("Default printer for this Windows user");

        if (check is not null)
        {
            text.AppendLine($"Last checked: {check.CheckedAt.LocalDateTime:g}");
            if (check.Probe.OpenError != 0)
                text.AppendLine($"Open queue: {ErrorText(check.Probe.OpenError)}");
            else if (check.Probe.DetailsError != 0)
                text.AppendLine($"Read queue properties: {ErrorText(check.Probe.DetailsError)}");
            else
                text.AppendLine(queue.Scope == QueueScope.SharedConnection ? "Live shared queue opened." : "Local Windows queue opened.");
            if (check.Events.Length != 0)
                text.AppendLine($"Matching PrintService/Admin events: {string.Join(", ", check.Events.Select(item => item.Id).Distinct())}");
            else
                text.AppendLine(check.EventsAvailable ? "No matching recent PrintService/Admin events." : "PrintService/Admin events unavailable.");
        }

        text.AppendLine().AppendLine(ConsoleHealth.Guidance(queue, details, snapshot, check?.Probe));
        if (_tests.TryGetValue(queue.Name, out ConsoleTest? test))
        {
            text.AppendLine().AppendLine($"Last test: {test.TestedAt.LocalDateTime:g}").AppendLine(test.Result.Message);
            if (test.Result.Code is int code)
                text.AppendLine(ErrorText(code));
            if (test.Result.JobState is string state)
                text.AppendLine(test.PageConfirmed ? "Physical output confirmed." : state);
        }

        _detail.Text = text.ToString();
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
        SyncControls();
    }

    private void ExportReport()
    {
        if (CreateReport() is not string json)
            return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save redacted printer inventory",
            FileName = "Kumpuni-technician-report.json",
            Filter = "JSON report (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, json);
            _message.Text = "Redacted inventory report saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _message.Text = $"The report could not be saved ({error.GetType().Name}).";
        }
    }

    private QueueEntry? SelectedQueue() => _queues.CurrentRow?.Tag as QueueEntry;
    private QueueDetails? Details(QueueEntry queue) => _checks.GetValueOrDefault(queue.Name)?.Probe.Details ?? queue.Details;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        SyncControls();
    }

    private void SyncControls()
    {
        QueueEntry? queue = SelectedQueue();
        _refresh.Enabled = !_busy;
        _export.Enabled = !_busy && _snapshot is not null;
        _filter.Enabled = !_busy;
        _inventory.Enabled = !_busy;
        _connection.Enabled = !_busy && _inventory.SelectedTab == _queuePage;
        _check.Enabled = !_busy && queue is not null;
        _test.Enabled = !_busy && queue is not null && _snapshot is not null
            && ConsoleHealth.MaySendTest(Details(queue), _snapshot.Ports.Items, _checks.GetValueOrDefault(queue.Name)?.Probe);
        _printed.Enabled = !_busy && queue is not null
            && _tests.GetValueOrDefault(queue.Name) is { Result.Verified: true, PageConfirmed: false };
        _repair.Enabled = !_busy && queue is { Scope: QueueScope.SharedConnection } && Target.Parse(queue.Name) is not null;
    }

    private static string DescribeInventory(ConsoleSnapshot snapshot)
    {
        var errors = new List<string>();
        AddError("local queues", snapshot.Local.Error);
        AddError("connected shares", snapshot.Shared.Error);
        AddError("drivers", snapshot.Drivers.Error);
        AddError("ports", snapshot.Ports.Error);
        if (snapshot.Spooler is null)
            errors.Add("local spooler status unavailable");
        else
            AddError("local spooler", snapshot.Spooler.Error);

        return errors.Count == 0 ? $"Inventory refreshed at {snapshot.CapturedAt.LocalDateTime:t}."
            : $"Partial inventory. {string.Join(" | ", errors)}";

        void AddError(string part, int code)
        {
            if (code != 0)
                errors.Add($"{part} ({ErrorText(code)})");
        }
    }

    private static bool Matches(string query, params string?[] values) => query.Length == 0
        || values.Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    private static bool IsWorkerError(Exception error) => error is Win32Exception or InvalidOperationException or JsonException or IOException;
    private static string ErrorText(int code) => $"0x{unchecked((uint)code):X8} ({code}) {new Win32Exception(code).Message}";

    private static Button Button(string text) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(5, 2, 5, 2), Margin = new Padding(0, 0, 6, 3)
    };

    private static DataGridView Grid(string name, params (string Label, int Weight)[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AccessibleName = name, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        };
        foreach ((string label, int weight) in columns)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = label, FillWeight = weight, MinimumWidth = label == "Jobs" ? 49 : 64,
                SortMode = DataGridViewColumnSortMode.Automatic,
                ValueType = label == "Jobs" || label == "Model" ? typeof(uint) : typeof(string)
            });
        }
        KumpuniTheme.StyleGrid(grid);
        return grid;
    }
}

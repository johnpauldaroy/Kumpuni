using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

internal enum TechnicianAction { RegisterDriver, ShareQueue, GrantPrint, ResumeJob, RestartJob, CancelJob, RetryConnection, PreparePrintingAccount }
internal sealed record TechnicianActionRecord(DateTimeOffset Time, TechnicianAction Action, int Code, bool Verified);

internal sealed class TechnicianActionsPanel : UserControl
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button _export = MakeButton("Export action report");
    private readonly TextBox _model = new() { Width = 340, MaxLength = 256, PlaceholderText = "Exact Windows driver model" };
    private readonly TextBox _retryPath = new() { Width = 340, MaxLength = 512, PlaceholderText = @"Optional retry path: \\HOST\SHARE" };
    private readonly TextBox _clientIp = new() { Width = 230, MaxLength = 64, PlaceholderText = "Affected client IP address" };
    private readonly ComboBox _profile = new() { Width = 145, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Host firewall profile" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 445, Width = 85, AccessibleName = "TCP port" };
    private readonly ComboBox _queues = new() { Width = 305, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Local printer queue" };
    private readonly TextBox _share = new() { Width = 210, MaxLength = 80, PlaceholderText = "Printer share name" };
    private readonly TextBox _account = new() { Width = 235, MaxLength = 256, PlaceholderText = @"DOMAIN\User or Group" };
    private readonly DataGridView _comparison = MakeGrid("Client and host comparison", "Evidence", "Client", "Print host");
    private readonly DataGridView _firewall = MakeGrid("Firewall rules for this client", "Rule", "Action", "TCP port", "Client address");
    private readonly DataGridView _jobs = MakeGrid("Jobs on this local queue", "Job ID", "Document", "Owner", "Status", "Pages", "Printed", "Submitted UTC");
    private readonly Button _publish = MakeButton("Share selected queue");
    private readonly Button _grant = MakeButton("Grant Print");
    private readonly Button _refreshJobs = MakeButton("Refresh jobs");
    private readonly Button _resume = MakeButton("Resume selected job");
    private readonly Button _restart = MakeButton("Restart selected job");
    private readonly Button _cancel = MakeButton("Cancel selected job");
    private readonly List<TechnicianActionRecord> _log = [];
    private string? _clientReport;
    private string? _hostReport;
    private FirewallCheck? _firewallCheck;
    private bool _busy;

    public bool IsMutating { get; private set; }

    public TechnicianActionsPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = KumpuniTheme.Canvas;
        Padding = new Padding(14);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_tabs, 0, 0);
        layout.Controls.Add(Bar(_export, _status), 0, 1);
        Controls.Add(layout);

        Label filenames = new() { AutoSize = true, Margin = new Padding(4, 5, 4, 2) };
        Button client = MakeButton("Open client report");
        Button host = MakeButton("Open host report");
        AddPage("Compare reports", "Choose the two redacted Shared printer repair reports for the same UNC share.",
            _comparison, Bar(client, host, filenames));
        client.Click += (_, _) => ChooseReport(false, filenames);
        host.Click += (_, _) => ChooseReport(true, filenames);

        Button driver = MakeButton("Choose approved INF and register");
        AddPage("Register driver", "Use the exact driver model supplied by IT and its approved local printer INF. Registration requires Windows administrator approval.",
            new Panel { Dock = DockStyle.Fill }, Bar(_model, driver), Bar(_retryPath));
        driver.Click += async (_, _) => await RegisterDriverAsync();

        Button firewall = MakeButton("Check inbound rules");
        _profile.Items.AddRange(["Active profiles", "Domain", "Private", "Public"]);
        _profile.SelectedIndex = 0;
        AddPage("Client firewall", "Run on the print host. Enter the affected client IP and TCP port. Rule conditions outside the address, profile and port can still apply.",
            _firewall, Bar(_clientIp, _profile, new Label { Text = "TCP port", AutoSize = true, Margin = new Padding(8, 7, 1, 0) }, _port, firewall));
        firewall.Click += async (_, _) => await CheckFirewallAsync();

        Button queueList = MakeButton("Refresh local queues");
        AddPage("Local queues and jobs", "Use this page on the print host. Sharing uses the queue's existing permissions. Job changes apply to the selected job.",
            _jobs, Bar(queueList, _queues, _refreshJobs), Bar(_share, _publish, _account, _grant), Bar(_resume, _restart, _cancel));
        queueList.Click += async (_, _) => await RunAsync(() => LoadQueuesAsync(showSuccess: true));
        _queues.SelectedIndexChanged += (_, _) => { _jobs.Rows.Clear(); Sync(); };
        _jobs.SelectionChanged += (_, _) => Sync();
        _refreshJobs.Click += async (_, _) => await RunAsync(LoadJobsAsync);
        _publish.Click += async (_, _) => await ChangeQueueAsync(publish: true);
        _grant.Click += async (_, _) => await ChangeQueueAsync(publish: false);
        _resume.Click += async (_, _) => await ChangeJobAsync(PrintJobAction.Resume);
        _restart.Click += async (_, _) => await ChangeJobAsync(PrintJobAction.Restart);
        _cancel.Click += async (_, _) => await ChangeJobAsync(PrintJobAction.Cancel);

        _export.Click += (_, _) => Export();
        Sync();
    }

    internal bool CheckBinding()
    {
        _ = _tabs.Handle;
        if (_tabs.TabPages.Count != 4 || _export.Enabled || _publish.Enabled || _grant.Enabled)
            return false;
        _tabs.SelectedIndex = 3;
        _queues.Items.Add(new QueueEntry("Fixture local printer", QueueScope.Local));
        _queues.SelectedIndex = 0;
        int row = _jobs.Rows.Add(7, "Fixture job", "Fixture user", "Paused", 1, 0, "Unknown");
        _jobs.Rows[row].Tag = new JobSelection(7, 1, "20261125120000123", 1, 0, "Fixture job", "Fixture user");
        _jobs.CurrentCell = _jobs.Rows[row].Cells[0];
        Sync();
        return _publish.Enabled && !_grant.Enabled && _resume.Enabled && _cancel.Enabled && !_restart.Enabled;
    }

    private void ChooseReport(bool onHost, Label filenames)
    {
        using var picker = new OpenFileDialog { Filter = "Kumpuni sharing report (*.json)|*.json", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        if (onHost)
            _hostReport = picker.FileName;
        else
            _clientReport = picker.FileName;
        filenames.Text = $"{(_clientReport is null ? 0 : 1) + (_hostReport is null ? 0 : 1)} of 2 reports selected";
        _comparison.Rows.Clear();
        if (_clientReport is null || _hostReport is null)
            return;
        try
        {
            foreach (ReportDifference row in SharingReportComparison.CompareFiles(_clientReport, _hostReport))
            {
                int index = _comparison.Rows.Add(row.Field, row.Client, row.Host);
                if (row.Client != row.Host && Comparable(row.Client) && Comparable(row.Host))
                    _comparison.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(255, 247, 220);
            }
            _status.Text = "Reports loaded. Highlighted values differ; review whether the settings are compatible.";
        }
        catch (InvalidDataException error) { _status.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _status.Text = "Kumpuni could not read these two sharing reports.";
        }

        static bool Comparable(string value) => value is not ("Unknown" or "Not applicable" or "Not available from the client")
            && !value.Contains("Unknown", StringComparison.Ordinal);
    }

    private async Task RegisterDriverAsync()
    {
        string model = _model.Text.Trim();
        if (model.Length == 0)
        {
            _status.Text = "Enter the exact printer driver model supplied by IT.";
            return;
        }
        Target? retry = Target.Parse(_retryPath.Text);
        if (!string.IsNullOrWhiteSpace(_retryPath.Text) && (retry is null || SharingSettings.IsLocalHost(retry)))
        {
            _status.Text = @"Enter a trusted remote printer path as \\HOST\SHARE, or leave the retry path empty.";
            return;
        }
        using var picker = new OpenFileDialog { Filter = "Printer driver INF (*.inf)|*.inf", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        string scope = retry is null ? "" : $"\n\nRetry {retry.Path} for this Windows user after verification?";
        if (Confirm($"Register \"{model}\" from this approved printer package on this PC?\n\n{picker.FileName}{scope}", "Register printer driver"))
            await MutateWithRetryAsync(TechnicianAction.RegisterDriver, retry, picker.FileName, model);
    }

    private Task CheckFirewallAsync()
    {
        if (!IPAddress.TryParse(_clientIp.Text.Trim(), out IPAddress? ip))
        {
            _status.Text = "Enter the affected client's IPv4 or IPv6 address.";
            return Task.CompletedTask;
        }
        int profile = _profile.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
        int port = (int)_port.Value;
        return RunAsync(async () =>
        {
            FirewallCheck check = await ClientFirewall.CheckAsync(ip, profile, port);
            if (IsDisposed)
                return;
            _firewallCheck = check;
            _firewall.Rows.Clear();
            foreach (FirewallMatch rule in check.Matches)
                _firewall.Rows.Add(rule.Name, rule.Action, rule.PortMatch, rule.RemoteMatch);
            _status.Text = check.Store switch
            {
                "ActiveStore" => $"{check.Matches.Length} candidates from ActiveStore for TCP {port}.",
                "LocalRules" => $"{check.Matches.Length} local candidates for TCP {port}. ActiveStore unavailable ({check.PolicyError}). Run as administrator for effective rules.",
                _ => $"Windows firewall query failed ({check.PolicyError})."
            };
        });
    }

    private async Task LoadQueuesAsync(bool showSuccess)
    {
        string? selection = SelectedQueue?.Name;
        ConsoleSnapshot? report = (await Worker.RunAsync("--console-scan"))?.Console;
        if (IsDisposed)
            return;
        if (report is null || report.Local.Error != 0)
        {
            _status.Text = $"Windows did not return local printer queues ({report?.Local.Error.ToString() ?? "worker unavailable"}).";
            return;
        }
        _queues.Items.Clear();
        _queues.DisplayMember = nameof(QueueEntry.Name);
        foreach (QueueEntry queue in report.Local.Items.OrderBy(queue => queue.Name, StringComparer.CurrentCultureIgnoreCase))
            _queues.Items.Add(queue);
        for (int i = 0; i < _queues.Items.Count; i++)
        {
            if (_queues.Items[i] is QueueEntry listed && Diagnose.Same(listed.Name, selection))
                _queues.SelectedIndex = i;
        }
        if (_queues.SelectedIndex < 0 && _queues.Items.Count != 0)
            _queues.SelectedIndex = 0;
        if (showSuccess)
            _status.Text = $"{_queues.Items.Count} local queues. Select a queue to view jobs or change sharing.";
    }

    private async Task LoadJobsAsync()
    {
        if (SelectedQueue is not QueueEntry queue)
            return;
        Catalog<JobSelection>? jobs = (await Worker.RunAsync("--technician-jobs", queue.Name))?.Jobs;
        if (IsDisposed)
            return;
        _jobs.Rows.Clear();
        if (jobs is null || jobs.Error != 0)
        {
            _status.Text = $"Windows did not return jobs ({jobs?.Error.ToString() ?? "worker unavailable"}).";
            return;
        }

        foreach (JobSelection job in jobs.Items)
        {
            int index = _jobs.Rows.Add(job.Id, job.Document ?? "", job.Owner ?? "", DescribeJob(job.Status), job.Pages, job.Printed,
                DateTime.TryParseExact(job.Stamp, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime submitted)
                ? submitted.ToString("u", CultureInfo.InvariantCulture) : "Unknown");
            _jobs.Rows[index].Tag = job;
        }
        _status.Text = jobs.Items.Length >= 128 ? "Showing the first 128 jobs." : $"{jobs.Items.Length} jobs.";
    }

    private async Task ChangeQueueAsync(bool publish)
    {
        if (SelectedQueue is not QueueEntry queue)
            return;
        string value = publish ? _share.Text.Trim() : _account.Text.Trim();
        if (publish ? !NativePrinting.ValidShareName(value) : value.Split('\\').Length != 2)
        {
            _status.Text = publish ? "Enter a valid printer share name." : @"Enter a specific account or group in DOMAIN\Name form.";
            return;
        }
        string prompt = publish ? $"Share the local queue \"{queue.Name}\" as \"{value}\"? Its existing permissions will apply. Windows may enable its File and Printer Sharing firewall exception."
            : $"Grant Print to \"{value}\" for the local queue \"{queue.Name}\"?";
        if (Confirm(prompt, publish ? "Share printer" : "Grant Print"))
            await MutateAsync(publish ? TechnicianAction.ShareQueue : TechnicianAction.GrantPrint, queue.Name, value);
    }

    private async Task ChangeJobAsync(PrintJobAction action)
    {
        if (SelectedQueue is not QueueEntry queue || SelectedJob is not JobSelection job)
            return;
        if (!Confirm($"{action} job {job.Id} on \"{queue.Name}\"?", $"{action} print job", action == PrintJobAction.Cancel))
            return;
        TechnicianAction operation = action switch
        {
            PrintJobAction.Resume => TechnicianAction.ResumeJob,
            PrintJobAction.Restart => TechnicianAction.RestartJob,
            _ => TechnicianAction.CancelJob
        };
        await MutateAsync(operation, queue.Name, job.Id.ToString(CultureInfo.InvariantCulture), job.Stamp);
    }

    private Task MutateAsync(TechnicianAction action, params string[] arguments) =>
        MutateWithRetryAsync(action, null, arguments);

    private Task MutateWithRetryAsync(TechnicianAction action, Target? retry, params string[] arguments) => RunAsync(async () =>
    {
        int code;
        string? retryStatus = null;
        IsMutating = true;
        try
        {
            code = await Worker.RunTechnicianActionAsync(action, arguments);
            if (IsDisposed)
                return;
            _log.Add(new(DateTimeOffset.UtcNow, action, code, code == 0 || action == TechnicianAction.RegisterDriver && code == 183));
            if (action == TechnicianAction.RegisterDriver && retry is not null && code is 0 or 183)
            {
                _status.Text = "Driver verified. Retrying the selected share as this Windows user...";
                ActionResult? connection = (await Worker.RunAsync("--repair", retry.Path, Repair.Connect))?.Action;
                if (IsDisposed)
                    return;
                _log.Add(new(DateTimeOffset.UtcNow, TechnicianAction.RetryConnection,
                    connection?.Code ?? (connection?.Verified == true ? 0 : 13), connection?.Verified == true));
                retryStatus = connection?.Message ?? "Windows did not confirm the connection retry.";
            }
        }
        finally { IsMutating = false; }
        if (action == TechnicianAction.ShareQueue)
            await LoadQueuesAsync(showSuccess: false);
        else if (action is TechnicianAction.ResumeJob or TechnicianAction.RestartJob or TechnicianAction.CancelJob)
            await LoadJobsAsync();
        if (IsDisposed)
            return;
        _status.Text = code switch
        {
            0 when action == TechnicianAction.GrantPrint => "Print access entry verified. Retest using the affected Windows account.",
            0 when action == TechnicianAction.RegisterDriver => "Driver registration verified. " + (retryStatus ?? "Retry from Shared printer repair."),
            183 when action == TechnicianAction.RegisterDriver => "The exact driver model is already registered. " + (retryStatus ?? "Retry from Shared printer repair."),
            0 => "The requested change was verified in Windows.",
            1223 => "Windows elevation was cancelled.",
            1237 => "Windows did not confirm the expected state. Refresh before retrying.",
            _ => $"Windows returned 0x{unchecked((uint)code):X8} ({code}). Check the selected item and privileges."
        };
    });

    private async Task RunAsync(Func<Task> execute)
    {
        if (_busy)
            return;
        _busy = true;
        _status.Text = "Waiting for Windows...";
        Sync();
        try { await execute(); }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or JsonException)
        {
            if (!IsDisposed)
                _status.Text = $"Windows could not complete this operation ({error.GetType().Name}).";
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
                Sync();
        }
    }

    private bool Confirm(string text, string title, bool warning = false) =>
        MessageBox.Show(this, text, title, MessageBoxButtons.OKCancel,
            warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    private void Export()
    {
        using var save = new SaveFileDialog { FileName = "Kumpuni-technician-actions.json", Filter = "JSON report (*.json)|*.json", OverwritePrompt = true };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            FirewallCheck? firewall = _firewallCheck;
            File.WriteAllText(save.FileName, JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, ReportType = "TechnicianActions", ExportedAt = DateTimeOffset.UtcNow,
                Actions = _log.Select(item => new { item.Time, Action = item.Action.ToString(), item.Code, item.Verified }),
                Firewall = firewall is null ? null : new
                {
                    firewall.Store, firewall.PolicyError, firewall.Profile, firewall.Port,
                    Candidates = firewall.Matches.GroupBy(item => new { item.Action, item.PortMatch, item.RemoteMatch })
                        .Select(group => new { group.Key.Action, group.Key.PortMatch, group.Key.RemoteMatch, Count = group.Count() })
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = "Redacted technician action report saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Windows could not save the report ({error.GetType().Name}).";
        }
    }

    private QueueEntry? SelectedQueue => _queues.SelectedItem as QueueEntry;
    private JobSelection? SelectedJob => _jobs.CurrentRow?.Tag as JobSelection;

    private void Sync()
    {
        _tabs.Enabled = !_busy;
        _export.Enabled = !_busy && (_log.Count != 0 || _firewallCheck is not null);
        _publish.Enabled = !_busy && SelectedQueue is { Scope: QueueScope.Local };
        _grant.Enabled = !_busy && SelectedQueue is { Scope: QueueScope.SharedHere };
        _refreshJobs.Enabled = !_busy && SelectedQueue is not null;
        _resume.Enabled = Eligible(PrintJobAction.Resume);
        _restart.Enabled = Eligible(PrintJobAction.Restart);
        _cancel.Enabled = Eligible(PrintJobAction.Cancel);

        bool Eligible(PrintJobAction action) => !_busy && SelectedJob is JobSelection job && NativePrinting.CanApplyJobAction(action, job.Status);
    }

    internal static string DescribeJob(uint status)
    {
        if ((status & 0x4) != 0) return "Deleting";
        if ((status & 0x100) != 0) return "Deleted";
        if ((status & 1) != 0) return "Paused";
        if ((status & (0x2 | 0x20 | 0x40 | 0x200 | 0x400)) != 0) return "Needs attention";
        if ((status & 0x10) != 0) return "Printing";
        if ((status & 0x8) != 0) return "Spooling";
        if ((status & (0x80 | 0x1000)) != 0) return "Completed";
        return $"Queued (0x{status:X})";
    }

    private void AddPage(string name, string instructions, Control contents, params Control[] bars)
    {
        var tab = new TabPage(name);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var text = new Label { Text = instructions, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(text, 0, 0);
        for (int i = 0; i < bars.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(bars[i], 0, i + 1);
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(contents, 0, bars.Length + 1);
        tab.Controls.Add(layout);
        _tabs.TabPages.Add(tab);
    }

    private static FlowLayoutPanel Bar(params Control[] items)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        bar.Controls.AddRange(items);
        return bar;
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true };
    private static DataGridView MakeGrid(string name, params string[] columns)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, AccessibleName = name, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            ReadOnly = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
        };
        foreach (string column in columns)
            grid.Columns.Add(column, column);
        KumpuniTheme.StyleGrid(grid);
        return grid;
    }
}

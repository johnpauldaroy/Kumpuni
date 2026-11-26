using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;

namespace Kumpuni;

internal static class KumpuniTheme
{
    public static readonly Color Canvas = Color.FromArgb(243, 246, 242);
    public static readonly Color Ink = Color.FromArgb(24, 41, 31);
    public static readonly Color Muted = Color.FromArgb(80, 98, 88);
    public static readonly Color Line = Color.FromArgb(217, 228, 220);
    public static readonly Color Primary = Color.FromArgb(22, 101, 52);
    public static readonly Color PrimaryHover = Color.FromArgb(20, 83, 45);
    public static readonly Color Tint = Color.FromArgb(225, 242, 228);
    public static readonly Color Forest = Color.FromArgb(18, 59, 46);
    public static readonly Color Lime = Color.FromArgb(196, 237, 185);
    private static readonly Color TableSelection = Color.FromArgb(205, 235, 216);

    public static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.GridColor = Line;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = TableSelection;
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Canvas;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Tint;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Tint;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink;
    }

    public static void StylePrimary(Button button)
    {
        button.BackColor = Primary;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Primary;
        button.FlatAppearance.MouseOverBackColor = PrimaryHover;
        button.UseVisualStyleBackColor = false;
    }

    public static Label Label(string text, float size = 10, bool bold = false) => new()
    {
        Text = text, AutoSize = true, ForeColor = Ink, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    public static Button Button(string text, bool primary = false) => new ActionButton(text, primary);

    public static Button HeroAction(string text) => new ActionButton(text, primary: true, hero: true);

    private sealed class ActionButton : Button
    {
        private readonly bool _primary;
        private readonly bool _hero;
        private bool _hover;

        public ActionButton(string text, bool primary, bool hero = false)
        {
            _primary = primary;
            _hero = hero;
            Text = text;
            AutoSize = true;
            MinimumSize = new Size(104, 40);
            Padding = new Padding(13, 5, 13, 5);
            Margin = new Padding(0, 0, 8, 0);
            Font = new Font("Segoe UI", 10, FontStyle.Bold);
            ForeColor = primary && !hero ? Color.White : Ink;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(_hero ? Forest : Parent?.BackColor ?? Color.White);
            Color fill = !Enabled ? Canvas : _hero ? (_hover ? Tint : Lime) : _primary ? (_hover ? PrimaryHover : Primary)
                : _hover ? Tint : Color.White;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), new Size(8, 8));
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, shape);
            if (!_primary || !Enabled)
            {
                using var border = new Pen(Line);
                e.Graphics.DrawPath(border, shape);
            }
            Color text = Enabled ? ForeColor : Muted;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), text, fill);
        }
    }
}

internal sealed class QuickRepairPanel : UserControl
{
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Padding = new Padding(25, 18, 25, 16) };
    private readonly ComboBox _queue = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 512,
        AccessibleName = "Installed printer or shared printer path" };
    private readonly CheckBox _default = new() { AutoSize = true, Text = "Make this my default printer when ready" };
    private readonly Button _save = KumpuniTheme.Button("Save printer", primary: true);
    private readonly Button _cancel = KumpuniTheme.Button("Cancel");
    private readonly Button _refresh = KumpuniTheme.Button("Refresh list");
    private readonly Button _run = KumpuniTheme.HeroAction("Check and fix");
    private readonly Button _change = KumpuniTheme.Button("Change printer");
    private readonly Button _export = KumpuniTheme.Button("Export report");
    private readonly Label _setupStatus = KumpuniTheme.Label("");
    private static readonly string[] PhaseNames = ["Diagnose", "Apply repair", "Verify"];
    private readonly Label[] _phaseStates = new Label[PhaseNames.Length];
    private readonly Label _connectionType = KumpuniTheme.Label("", 8.5f, true);
    private readonly Label _connectionLocation = KumpuniTheme.Label("", 8.5f, true);
    private readonly Label _defaultPreference = KumpuniTheme.Label("", 8.5f, true);
    private readonly Label _queueName = KumpuniTheme.Label("", 20, true);
    private readonly Label _queueType = KumpuniTheme.Label("", 9);
    private readonly Label _resultTitle = KumpuniTheme.Label("No check run yet", 11, true);
    private readonly Label _resultCaption = KumpuniTheme.Label("");
    private readonly RichTextBox _resultDetail = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White,
        DetectUrls = false, AccessibleName = "Printer check results", ScrollBars = RichTextBoxScrollBars.Vertical,
        Font = new Font("Segoe UI", 10), ForeColor = KumpuniTheme.Muted
    };
    private readonly Panel _setupCard;
    private readonly Control _profileCard;
    private readonly Panel _resultCard;
    private readonly Action<PrinterProfile> _saveProfile;
    private readonly Func<PrinterProfile, Action<QuickProgress>, Task<QuickOutcome>> _runProfile;
    private PrinterProfile? _profile;
    private QuickOutcome? _last;
    private QueueEntry[] _choices = [];
    private bool _editing;
    private bool _scanning;
    private bool _running;
    private bool _loadedChoices;
    private bool _ignoreChoiceChange;

    public event EventHandler? DefaultActionChanged;
    public IButtonControl DefaultAction => _editing ? _save : _run;
    public bool IsRunning => _running;

    public QuickRepairPanel(Func<PrinterProfile?>? load = null, Action<PrinterProfile>? save = null,
        Func<PrinterProfile, Action<QuickProgress>, Task<QuickOutcome>>? run = null)
    {
        _saveProfile = save ?? PrinterProfileStore.Save;
        _runProfile = run ?? QuickRepair.RunAsync;
        Dock = DockStyle.Fill;
        BackColor = KumpuniTheme.Canvas;
        ForeColor = KumpuniTheme.Ink;
        _layout.BackColor = BackColor;
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _viewport.Controls.Add(_layout);
        Controls.Add(_viewport);
        _viewport.SizeChanged += (_, _) => SizeWorkspace();

        var heading = Column(auto: true);
        var title = KumpuniTheme.Label("My printer", 27, true);
        title.Margin = new Padding(0, 0, 0, 5);
        var subtitle = KumpuniTheme.Label("Set it up once. Check and fix whenever you need to.", 10);
        subtitle.ForeColor = KumpuniTheme.Muted;
        subtitle.Margin = new Padding(1, 0, 0, 15);
        heading.Controls.Add(title);
        heading.Controls.Add(subtitle);
        _layout.Controls.Add(heading, 0, 0);

        _setupCard = Card(BuildSetup(), auto: true);
        _profileCard = BuildProfile();
        _resultCard = Card(BuildResults(), auto: false, padding: 14);
        _setupCard.Margin = new Padding(0, 0, 0, 14);
        _profileCard.Margin = new Padding(0, 0, 0, 14);
        _resultCard.Margin = Padding.Empty;

        _save.Click += (_, _) => SaveSetup();
        _cancel.Click += (_, _) => { _editing = false; RenderState(); };
        _refresh.Click += async (_, _) => await LoadChoicesAsync();
        _change.Click += async (_, _) => await BeginSetupAsync();
        _run.Click += async (_, _) => await RunAsync();
        _export.Click += (_, _) => Export();
        _queue.TextChanged += (_, _) =>
        {
            if (!_ignoreChoiceChange && !_scanning)
                _setupStatus.Text = "";
            SyncButtons();
        };

        try { _profile = (load ?? PrinterProfileStore.Load)(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _setupStatus.Text = "The saved printer could not be loaded. Choose a printer to update the setup.";
        }
        _editing = _profile is null;
        RenderState();
    }

    public Task InitializeAsync() => _editing && !_loadedChoices ? LoadChoicesAsync() : Task.CompletedTask;

    private Control BuildSetup()
    {
        var body = Column(auto: true);
        AddText(body, "Set up your printer", 17, true, 0, 6);
        Label instruction = AddText(body, @"Choose an installed printer or enter a shared path such as \\PRINT-PC\Office.", 10, false, 0, 13);
        instruction.ForeColor = KumpuniTheme.Muted;
        AddText(body, "Printer queue or shared path", 10, true, 0, 5);
        _queue.Margin = new Padding(0, 0, 0, 12);
        body.Controls.Add(_queue);
        _default.Margin = new Padding(0, 0, 0, 10);
        body.Controls.Add(_default);
        Label consent = AddText(body, "Saving allows Check and fix to connect this trusted share, reconnect a verified idle connection, start eligible printing services, and apply your default-printer choice. Windows may ask for administrator approval.", 9, false, 0, 14);
        consent.ForeColor = KumpuniTheme.Muted;
        consent.MaximumSize = new Size(560, 0);
        body.Controls.Add(Buttons(_save, _refresh, _cancel));
        _setupStatus.ForeColor = KumpuniTheme.Muted;
        _setupStatus.Margin = new Padding(0, 8, 0, 0);
        _setupStatus.MaximumSize = new Size(560, 0);
        body.Controls.Add(_setupStatus);
        return body;
    }

    private Control BuildProfile()
    {
        var overview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        overview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        overview.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hero = new PrinterHero
        {
            Dock = DockStyle.Fill, Padding = new Padding(19, 16, 19, 16),
            MinimumSize = new Size(0, 173), Margin = new Padding(0, 0, 14, 0)
        };
        var body = Column(auto: true);
        body.Dock = DockStyle.Top;
        body.BackColor = Color.Transparent;
        Label label = AddText(body, "Saved printer", 9, true, 0, 5);
        label.ForeColor = KumpuniTheme.Lime;
        label.BackColor = Color.Transparent;
        _queueName.AutoSize = false;
        _queueName.AutoEllipsis = true;
        _queueName.Dock = DockStyle.Top;
        _queueName.Height = _queueName.Font.Height + 5;
        _queueName.Margin = Padding.Empty;
        _queueName.ForeColor = Color.White;
        _queueName.BackColor = Color.Transparent;
        body.Controls.Add(_queueName);
        _queueType.ForeColor = KumpuniTheme.Lime;
        _queueType.BackColor = Color.Transparent;
        _queueType.Dock = DockStyle.Top;
        _queueType.AutoEllipsis = true;
        _queueType.Margin = new Padding(0, 4, 0, 12);
        body.Controls.Add(_queueType);
        var runRow = Buttons(_run);
        runRow.BackColor = Color.Transparent;
        body.Controls.Add(runRow);
        hero.Controls.Add(body);
        overview.Controls.Add(hero, 0, 0);

        var connection = Column(auto: true);
        AddText(connection, "Connection", 13, true, 0, 4);
        AddConnectionRow(connection, "Type", _connectionType);
        AddConnectionRow(connection, "Location", _connectionLocation);
        AddConnectionRow(connection, "Default", _defaultPreference);
        var actions = Buttons(_change);
        actions.Margin = new Padding(0, 4, 0, 0);
        connection.Controls.Add(actions);
        Panel card = Card(connection, auto: true, padding: 14);
        card.Margin = Padding.Empty;
        overview.Controls.Add(card, 1, 0);
        return overview;

    }

    private static void AddConnectionRow(TableLayoutPanel parent, string name, Label value)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 5)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label key = KumpuniTheme.Label(name, 9);
        key.ForeColor = KumpuniTheme.Muted;
        key.Margin = new Padding(0, 3, 7, 3);
        row.Controls.Add(key, 0, 0);
        value.AutoSize = false;
        value.Height = value.Font.Height + 6;
        value.Dock = DockStyle.Top;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.AutoEllipsis = true;
        value.Margin = Padding.Empty;
        value.AccessibleName = name;
        row.Controls.Add(value, 1, 0);
        parent.Controls.Add(row);
    }

    private Control BuildResults()
    {
        var body = Column(auto: false);
        body.RowCount = 5;
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 6)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label lastCheck = KumpuniTheme.Label("Last check", 15, true);
        lastCheck.Anchor = AnchorStyles.Left;
        lastCheck.Margin = Padding.Empty;
        _export.Margin = Padding.Empty;
        header.Controls.Add(lastCheck, 0, 0);
        header.Controls.Add(_export, 1, 0);
        body.Controls.Add(header, 0, 0);

        _resultTitle.MaximumSize = new Size(550, 0);
        _resultTitle.Margin = new Padding(0, 1, 0, 3);
        body.Controls.Add(_resultTitle, 0, 1);
        _resultCaption.ForeColor = KumpuniTheme.Muted;
        _resultCaption.MaximumSize = new Size(550, 0);
        _resultCaption.Margin = new Padding(0, 0, 0, 9);
        body.Controls.Add(_resultCaption, 0, 2);

        var timeline = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Margin = new Padding(0, 0, 0, 9)
        };
        for (int index = 0; index < PhaseNames.Length; index++)
        {
            timeline.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / PhaseNames.Length));
            var step = Column(auto: true);
            step.Padding = new Padding(9, 5, 6, 5);
            step.Margin = new Padding(0, 0, index == PhaseNames.Length - 1 ? 0 : 8, 0);
            step.BackColor = KumpuniTheme.Canvas;
            step.Controls.Add(KumpuniTheme.Label(PhaseNames[index], 9, true));
            Label state = _phaseStates[index] = KumpuniTheme.Label("Waiting", 9);
            state.ForeColor = KumpuniTheme.Muted;
            state.AccessibleName = PhaseNames[index] + " status";
            step.Controls.Add(state);
            timeline.Controls.Add(step, index, 0);
        }
        body.Controls.Add(timeline, 0, 3);
        _resultDetail.Margin = Padding.Empty;
        body.Controls.Add(_resultDetail, 0, 4);
        return body;
    }

    private async Task BeginSetupAsync()
    {
        if (_running)
            return;
        _editing = true;
        _queue.Text = _profile?.Queue ?? "";
        _default.Checked = _profile?.MakeDefault == true;
        RenderState();
        if (!_loadedChoices)
            await LoadChoicesAsync();
        if (!IsDisposed)
            _queue.Focus();
    }

    private async Task LoadChoicesAsync()
    {
        if (_scanning || _running)
            return;
        _scanning = true;
        _setupStatus.Text = "Looking for printers installed in Windows...";
        SyncButtons();
        try
        {
            ConsoleSnapshot? snapshot = (await Worker.RunAsync("--console-scan"))?.Console;
            if (IsDisposed)
                return;
            if (snapshot is null)
            {
                _setupStatus.Text = "Windows did not return a printer list. Enter a shared path or refresh.";
                return;
            }

            _choices = snapshot.Queues();
            _loadedChoices = true;
            string current = _queue.Text;
            _ignoreChoiceChange = true;
            try
            {
                _queue.Items.Clear();
                _queue.Items.AddRange(_choices.Select(choice => (object)choice.Name).ToArray());
                _queue.Text = current;
            }
            finally { _ignoreChoiceChange = false; }
            _setupStatus.Text = _choices.Length == 0
                ? "No installed printers were found. Enter a shared path if IT has provided one."
                : $"{_choices.Length} installed {(_choices.Length == 1 ? "printer" : "printers")} found.";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or JsonException)
        {
            if (!IsDisposed)
                _setupStatus.Text = "Windows did not return a printer list. Enter a shared path or refresh.";
        }
        finally
        {
            _scanning = false;
            if (!IsDisposed)
                SyncButtons();
        }
    }

    private void SaveSetup()
    {
        string input = _queue.Text.Trim();
        Target? target = Target.Parse(input);
        QueueEntry? local = _choices.FirstOrDefault(item => item.Scope != QueueScope.SharedConnection && Diagnose.Same(item.Name, input));
        bool storedLocal = _profile is { Shared: false } saved && Diagnose.Same(saved.Queue, input);
        if (target is null && local is null && !storedLocal)
        {
            _setupStatus.Text = "Select an installed printer from the list or enter a valid shared printer path.";
            _queue.Focus();
            return;
        }

        var setup = new PrinterProfile(target?.Path ?? local?.Name ?? _profile!.Queue, target is not null, _default.Checked);
        try
        {
            _saveProfile(setup);
            _profile = setup;
            _last = null;
            _editing = false;
            RenderState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _setupStatus.Text = "Windows could not save this printer setup.";
        }
    }

    private async Task RunAsync()
    {
        if (_running || _editing || _profile is not PrinterProfile profile)
            return;

        _running = true;
        _last = null;
        _resultTitle.Text = "Checking your printer";
        ResetPhases();
        SetPhase(QuickPhase.Diagnose, "In progress", KumpuniTheme.Primary);
        _resultCaption.Text = "Reading the Windows queue...";
        _resultDetail.Text = "";
        _resultDetail.Visible = true;
        _resultTitle.ForeColor = KumpuniTheme.Ink;
        SyncButtons();
        try
        {
            _last = await _runProfile(profile, step =>
            {
                if (!IsDisposed)
                    ShowProgress(step);
            });
        }
        finally
        {
            _running = false;
            if (!IsDisposed)
                RenderState();
        }
    }

    private void RenderState()
    {
        _layout.SuspendLayout();
        _layout.Controls.Remove(_setupCard);
        _layout.Controls.Remove(_profileCard);
        _layout.Controls.Remove(_resultCard);
        if (_editing)
            _layout.Controls.Add(_setupCard, 0, 1);
        else if (_profile is PrinterProfile profile)
        {
            Target? target = profile.Shared ? Target.Parse(profile.Queue) : null;
            _queueName.Text = target?.Share ?? profile.Queue;
            _queueType.Text = target?.Path ?? "Installed in Windows";
            _connectionType.Text = profile.Shared ? "Windows share" : "Installed queue";
            _connectionLocation.Text = target?.Host ?? "This PC";
            _defaultPreference.Text = profile.MakeDefault ? "Set when ready" : "Keep current";
            _layout.Controls.Add(_profileCard, 0, 1);
            _layout.Controls.Add(_resultCard, 0, 2);
            RenderResult();
        }
        SizeWorkspace();
        _layout.ResumeLayout(true);
        SyncButtons();
        DefaultActionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SizeWorkspace()
    {
        int preferredHeight = LogicalToDeviceUnits(_editing ? 450 : 570);
        _layout.Height = Math.Max(_viewport.ClientSize.Height, preferredHeight);
    }

    private void RenderResult()
    {
        if (_last is not QuickOutcome report)
        {
            _resultTitle.Text = "No check run yet";
            ResetPhases();
            _resultTitle.ForeColor = KumpuniTheme.Ink;
            _resultCaption.Text = "Run Check and fix to see the results.";
            _resultDetail.Text = "";
            _resultDetail.Visible = false;
            return;
        }

        _resultDetail.Visible = true;
        _resultTitle.Text = report.Title;
        _resultTitle.ForeColor = report.Ready ? KumpuniTheme.Primary : KumpuniTheme.Ink;
        _resultCaption.Text = $"Last checked {report.RanAt.LocalDateTime:g}";
        bool inspected = report.SharedReport is not null || report.LocalBefore is not null || report.Ready;
        bool attempted = report.Steps.Length != 0;
        bool verified = attempted && report.Steps.All(step => step.Verified)
            && (report.SharedRun?.Final is not null || report.LocalAfter is not null);
        Color attention = Color.FromArgb(130, 70, 24);
        SetPhase(QuickPhase.Diagnose, inspected ? "Complete" : "Unavailable",
            inspected ? KumpuniTheme.Primary : attention);
        SetPhase(QuickPhase.Repair, attempted ? verified ? "Applied" : "Check needed"
                : inspected && report.Ready ? "Not needed" : "Not applied",
            attempted && !verified ? attention : KumpuniTheme.Muted);
        SetPhase(QuickPhase.Verify, report.Ready ? "Ready" : inspected ? "Needs attention" : "Unavailable",
            report.Ready ? KumpuniTheme.Primary : attention);
        var text = new StringBuilder(report.Detail);
        foreach (QuickStep step in report.Steps)
        {
            text.AppendLine().AppendLine().Append(step.Action).Append(step.Verified ? "  |  Verified" : "  |  Check needed")
                .AppendLine().Append(step.Message);
            if (step.Code is int code && code != 0)
                text.Append($" (Windows {code})");
        }
        _resultDetail.Text = text.ToString();
    }

    private void ShowProgress(QuickProgress progress)
    {
        _resultCaption.Text = progress.Message;
        if (progress.Phase != QuickPhase.Diagnose)
            SetPhase(QuickPhase.Diagnose, "Complete", KumpuniTheme.Primary);
        switch (progress.Phase)
        {
            case QuickPhase.Repair:
                SetPhase(QuickPhase.Verify, "Waiting", KumpuniTheme.Muted);
                break;
            case QuickPhase.Verify:
                SetPhase(QuickPhase.Repair, "Attempted", KumpuniTheme.Muted);
                break;
        }
        SetPhase(progress.Phase, "In progress", KumpuniTheme.Primary);
    }

    private void ResetPhases()
    {
        for (int index = 0; index < PhaseNames.Length; index++)
            SetPhase((QuickPhase)index, "Waiting", KumpuniTheme.Muted);
    }

    private void SetPhase(QuickPhase phase, string text, Color color)
    {
        Label state = _phaseStates[(int)phase];
        state.Text = text;
        state.ForeColor = color;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Control card in new[] { _setupCard, _profileCard, _resultCard })
            {
                if (card.Parent is null)
                    card.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private void SyncButtons()
    {
        _run.Enabled = !_running && _profile is not null;
        _change.Enabled = !_running;
        _refresh.Enabled = !_scanning;
        _save.Enabled = !_scanning || Target.Parse(_queue.Text) is not null;
        _cancel.Visible = _profile is not null;
        _export.Enabled = _last is not null && !_running;
    }

    private void Export()
    {
        if (_profile is null || _last is null)
            return;
        using var picker = new SaveFileDialog
        {
            FileName = "Kumpuni-printer-check.json", Filter = "JSON report (*.json)|*.json", OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllText(picker.FileName, QuickRepair.Serialize(_profile, _last));
            _resultCaption.Text = "Redacted report saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _resultCaption.Text = "Windows could not save the report.";
        }
    }

    internal static async Task<bool> CheckBindingAsync()
    {
        PrinterProfile? saved = null;
        using var panel = new QuickRepairPanel(() => null, profile => saved = profile,
            (profile, progress) =>
            {
                progress(new(QuickPhase.Diagnose, "Check Windows queue"));
                return Task.FromResult(new QuickOutcome("Your printer is ready", "Fixture result", true, [], DateTimeOffset.UtcNow));
            });
        _ = panel.Handle;
        panel._queue.Text = @"\\PRINT-FIXTURE\Office";
        panel._save.PerformClick();
        if (saved?.Queue != @"\\PRINT-FIXTURE\Office" || panel._editing || panel.DefaultAction != panel._run)
            return false;
        await panel.RunAsync();
        return panel._last?.Ready == true && panel._export.Enabled
            && panel._resultTitle.Text == "Your printer is ready"
            && panel._connectionLocation.Text == "PRINT-FIXTURE"
            && panel._phaseStates.Select(label => label.Text).SequenceEqual(["Complete", "Not needed", "Ready"]);
    }

    private sealed class PrinterHero : Panel
    {
        private readonly Bitmap _photo = KumpuniBrand.LoadPrinterPhoto();

        public PrinterHero()
        {
            BackColor = KumpuniTheme.Forest;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (ClientSize is not { Width: > 4, Height: > 4 } size)
                return;

            int photoWidth = (int)(size.Width * .63);
            float scale = Math.Max(photoWidth / (float)_photo.Width, size.Height / (float)_photo.Height);
            float width = _photo.Width * scale;
            float height = _photo.Height * scale;
            Rectangle photoBounds = new(size.Width - photoWidth, 0, photoWidth, size.Height);
            var saved = e.Graphics.Save();
            e.Graphics.SetClip(photoBounds);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_photo, new RectangleF(size.Width - width, (size.Height - height) / 2, width, height));
            e.Graphics.Restore(saved);

            int overlayWidth = Math.Max(1, (int)(size.Width * .88) - photoBounds.Left);
            Rectangle overlay = new(photoBounds.Left - 2, 0, overlayWidth + 2, size.Height);
            using var shade = new LinearGradientBrush(overlay, BackColor, Color.FromArgb(0, BackColor), 0f);
            e.Graphics.FillRectangle(shade, overlay);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _photo.Dispose();
            base.Dispose(disposing);
        }
    }

    private static Panel Card(Control content, bool auto, int padding = 20)
    {
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = KumpuniTheme.Line, Padding = new Padding(1), AutoSize = auto, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var inside = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(padding), AutoSize = auto, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        inside.Controls.Add(content);
        frame.Controls.Add(inside);
        frame.SizeChanged += (_, _) =>
        {
            if (frame.Width < 18 || frame.Height < 18)
                return;
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(frame.ClientRectangle, new Size(10, 10));
            Region? previous = frame.Region;
            frame.Region = new Region(shape);
            previous?.Dispose();
        };
        return frame;
    }

    private static TableLayoutPanel Column(bool auto)
    {
        var column = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = auto,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return column;
    }

    private static Label AddText(Control parent, string text, float size, bool bold, int top, int bottom)
    {
        Label label = KumpuniTheme.Label(text, size, bold);
        label.Margin = new Padding(0, top, 0, bottom);
        parent.Controls.Add(label);
        return label;
    }

    private static FlowLayoutPanel Buttons(params Control[] controls)
    {
        var tray = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty, BackColor = Color.White };
        tray.Controls.AddRange(controls);
        return tray;
    }
}

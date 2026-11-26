namespace Kumpuni;

internal sealed class PrinterHelpPanel : UserControl
{
    private readonly TabControl _connections = new() { Dock = DockStyle.Fill };
    private readonly TextBox _host = new()
    {
        Width = 255, MaxLength = 253, PlaceholderText = "Printer hostname or IP address",
        AccessibleName = "Printer hostname or IP address"
    };
    private readonly ComboBox _protocol = new()
    {
        Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Printing protocol"
    };
    private readonly NumericUpDown _port = new()
    {
        Width = 80, Minimum = 1, Maximum = 65535, AccessibleName = "TCP port"
    };
    private readonly TextBox _path = new()
    {
        Width = 330, MaxLength = 512, PlaceholderText = @"\\HOST\SHARE", AccessibleName = "Shared printer path"
    };
    private readonly Button _usbCheck = Button("Check USB detection");
    private readonly Button _networkCheck = Button("Check TCP connection");
    private readonly Label _usbResult = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly Label _networkResult = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly Label _sharedResult = new() { AutoSize = true, MaximumSize = new Size(620, 0) };

    private UsbPrinterCheck? _lastUsb;
    private PrinterEndpointCheck? _lastNetwork;

    public PrinterHelpEvidence? Evidence => _lastUsb is null && _lastNetwork is null ? null : new(_lastUsb, _lastNetwork);

    public PrinterHelpPanel(Action<PrinterConnection> showQueues, Action<string> openSharedRepair, Action<Label> openSettings)
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(16);
        BackColor = KumpuniTheme.Canvas;
        Controls.Add(_connections);

        FlowLayoutPanel usb = AddPage("USB",
            "Connect the powered-on printer directly to this PC with a data cable. Try another cable or USB port if Windows misses it. "
            + "When Windows detects the printer, use Printers & scanners to add a queue or inspect an existing USB queue.");
        usb.Controls.Add(Actions(_usbCheck, QueueButton(PrinterConnection.Usb), SettingsButton(_usbResult)));
        usb.Controls.Add(_usbResult);
        _usbCheck.Click += async (_, _) => await CheckUsbAsync();

        FlowLayoutPanel network = AddPage("Network",
            "Use the address shown on the printer's network page. For an installed queue, compare it with the address and protocol "
            + "in Windows port settings. For WSD, use Windows discovery on the printer's network. For IPP, use the device's full IPP URL during setup.");
        _protocol.Items.AddRange(["RAW", "LPR", "IPP / IPPS"]);
        _protocol.SelectedIndexChanged += (_, _) => _port.Value = _protocol.SelectedIndex switch
        {
            1 => 515,
            2 => 631,
            _ => 9100
        };
        _protocol.SelectedIndex = 0;
        network.Controls.Add(Actions(_host, _protocol, new Label { Text = "TCP port", AutoSize = true, Margin = new Padding(6, 7, 0, 0) }, _port));
        network.Controls.Add(Actions(_networkCheck, QueueButton(PrinterConnection.Network), SettingsButton(_networkResult)));
        network.Controls.Add(_networkResult);
        _networkCheck.Click += async (_, _) => await CheckNetworkAsync();
        _host.TextChanged += (_, _) => _networkResult.Text = "";
        _port.ValueChanged += (_, _) => _networkResult.Text = "";
        _protocol.SelectedIndexChanged += (_, _) => _networkResult.Text = "";

        FlowLayoutPanel shared = AddPage("Shared",
            "For a printer published by a Windows print host, enter the exact share path supplied by the host operator. "
            + "Shared printer repair checks access, print RPC and driver policy for that path.");
        shared.Controls.Add(_path);
        var diagnose = Button("Open shared printer repair");
        shared.Controls.Add(Actions(diagnose, QueueButton(PrinterConnection.Shared)));
        shared.Controls.Add(_sharedResult);
        diagnose.Click += (_, _) =>
        {
            if (Target.Parse(_path.Text) is Target target)
                openSharedRepair(target.Path);
            else
            {
                _sharedResult.Text = @"Enter a Windows printer share in \\HOST\SHARE form.";
                _path.Focus();
            }
        };
        _path.TextChanged += (_, _) => _sharedResult.Text = "";

        Button QueueButton(PrinterConnection connection)
        {
            var button = Button("View installed queues");
            button.Click += (_, _) => showQueues(connection);
            return button;
        }

        Button SettingsButton(Label result)
        {
            var button = Button("Open Windows printer settings");
            button.Click += (_, _) => openSettings(result);
            return button;
        }
    }

    internal static bool CheckBinding()
    {
        PrinterConnection? queueFilter = null;
        string? sharedPath = null;
        using var panel = new PrinterHelpPanel(connection => queueFilter = connection, path => sharedPath = path, _ => { });

        panel.CreateControl();
        panel._connections.SelectedIndex = 1;
        if (panel._port.Value != 9100)
            return false;
        panel._protocol.SelectedIndex = 1;
        if (panel._port.Value != 515)
            return false;
        panel._protocol.SelectedIndex = 2;
        if (panel._port.Value != 631)
            return false;

        FindButton("View installed queues").PerformClick();
        if (queueFilter != PrinterConnection.Network)
            return false;

        panel._connections.SelectedIndex = 2;
        Button openRepair = FindButton("Open shared printer repair");
        panel._path.Text = @"\\HOST";
        openRepair.PerformClick();
        if (sharedPath is not null || panel._sharedResult.Text.Length == 0)
            return false;
        panel._path.Text = @"\\HOST\Printer";
        openRepair.PerformClick();
        return sharedPath == @"\\HOST\Printer" && panel.Evidence is null;

        Button FindButton(string title) =>
            Descendants(panel._connections.SelectedTab!).OfType<Button>().Single(button => button.Text == title);

        static IEnumerable<Control> Descendants(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(child => Descendants(child).Prepend(child));
    }

    private async Task CheckUsbAsync()
    {
        _usbCheck.Enabled = false;
        _usbResult.Text = "Checking Windows USB printer interfaces...";
        try
        {
            var (present, error) = await Task.Run(PrinterHelp.ReadUsbPresence);
            if (!IsDisposed)
            {
                _lastUsb = new(DateTimeOffset.Now, present, error);
                _usbResult.Text = present switch
                {
                    true => "Windows reports an active USB printer interface on this PC. Open the queue list to verify the intended printer's driver and port.",
                    false => "Windows reports no active USB printer interface. Check power, the USB cable and Device Manager. Some devices use a vendor driver with a different interface.",
                    null => $"Windows could not list USB printer interfaces (Configuration Manager code 0x{error:X}). Check Device Manager."
                };
            }
        }
        finally
        {
            if (!IsDisposed)
                _usbCheck.Enabled = true;
        }
    }

    private async Task CheckNetworkAsync()
    {
        string? host = PrinterHelp.ParseHost(_host.Text);
        if (host is null)
        {
            _networkResult.Text = "Enter a hostname or IP address. Set the TCP port separately.";
            _host.Focus();
            return;
        }

        int port = (int)_port.Value;
        SetNetworkInputsEnabled(false);
        _networkResult.Text = $"Checking TCP port {port}...";
        try
        {
            PrinterEndpointStatus status = await Worker.ProbePrinterEndpointAsync(host, port);
            if (!IsDisposed)
            {
                _lastNetwork = new(DateTimeOffset.Now, port, status);
                _networkResult.Text = status switch
                {
                    PrinterEndpointStatus.Reachable => $"TCP port {port} accepted the connection. Use the queue test to verify printing.",
                    PrinterEndpointStatus.Unreachable => $"TCP port {port} did not accept a connection within the check. Verify the configured port, printer power and network route.",
                    PrinterEndpointStatus.NameNotResolved => "Windows could not resolve the printer hostname. Verify the name or use the printer's IP address.",
                    _ => "The hostname lookup timed out. Verify the network connection or use the printer's IP address."
                };
            }
        }
        finally
        {
            if (!IsDisposed)
                SetNetworkInputsEnabled(true);
        }
    }

    private void SetNetworkInputsEnabled(bool enabled) =>
        _networkCheck.Enabled = _host.Enabled = _protocol.Enabled = _port.Enabled = enabled;

    private FlowLayoutPanel AddPage(string title, string instructions)
    {
        var page = new TabPage(title);
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(15)
        };
        content.Controls.Add(new Label { Text = instructions, AutoSize = true, MaximumSize = new Size(620, 0),
            Margin = new Padding(3, 3, 3, 13) });
        page.Controls.Add(content);
        _connections.TabPages.Add(page);
        return content;
    }

    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 7) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Button Button(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
}

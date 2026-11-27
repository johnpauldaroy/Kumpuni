using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Kumpuni;

// Opens an authenticated SMB session to the print host through Windows' own credential prompt.
// Windows collects credentials and can save them for this user when the provider supports it.
internal static class HostSignIn
{
    private const int ConnectTemporary = 0x4;
    private const int ConnectInteractive = 0x8;
    private const int ConnectPrompt = 0x10;
    private const int ExtendedError = 1208;

    public static async Task<ActionResult> SignInAsync(Target target, Form? owner)
    {
        using var dialog = new Form
        {
            Text = "Sign in to print host", FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MaximizeBox = false, MinimizeBox = false,
            ClientSize = new Size(470, 222), Font = new Font("Segoe UI", 10), AutoScaleMode = AutoScaleMode.Font
        };
        var account = new TextBox
        {
            Dock = DockStyle.Top, Text = SuggestedAccount(target), MaxLength = 256, AccessibleName = "Print host account"
        };
        var save = new Button { Text = "Save in Windows", DialogResult = DialogResult.OK, AutoSize = true };
        var temporary = new Button { Text = "Sign in once", DialogResult = DialogResult.Retry, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save, temporary]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"Print host: {target.Host}", AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = @"Host account (HOST\user or user@domain)", AutoSize = true }, 0, 1);
        layout.Controls.Add(account, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "Windows prompts for the password. Saving replaces the Windows credential for this host and lets other Windows apps use the sign-in.",
            AutoSize = true, MaximumSize = new Size(430, 0), Padding = new Padding(0, 8, 0, 8)
        }, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        save.Enabled = IsAccount(account.Text);
        account.TextChanged += (_, _) => save.Enabled = IsAccount(account.Text);
        DialogResult choice = dialog.ShowDialog(owner);

        return choice switch
        {
            DialogResult.OK => await SaveCredentialAsync(target, account.Text.Trim()),
            DialogResult.Retry => SignIn(target, owner?.Handle ?? IntPtr.Zero),
            _ => DescribeResult(1223)
        };
    }

    internal static string SuggestedAccount(Target target) => IPAddress.TryParse(target.Host, out _)
        ? "" : $@"{target.Host.Split('.')[0]}\{PrintingAccount.Name}";

    internal static bool IsAccount(string input) =>
        input.Trim() is { Length: > 0 and <= 256 } value
        && !value.Any(c => char.IsControl(c) || c is '"' or '/' or ':' or '<' or '>' or '|')
        && (value.Split('\\') is [string host, string user] && host.Trim().Length > 0 && user.Trim().Length > 0
            || !value.Contains('\\') && value.Split('@') is [string name, string domain] && name.Trim().Length > 0 && domain.Trim().Length > 0);

    internal static ProcessStartInfo CredentialPrompt(Target target, string account)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmdkey.exe"))
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        info.ArgumentList.Add($"/add:{target.Host}");
        info.ArgumentList.Add($"/user:{account}");
        info.ArgumentList.Add("/pass");
        return info;
    }

    private static async Task<ActionResult> SaveCredentialAsync(Target target, string account)
    {
        if (!IsAccount(account))
            return new("Enter an authorized host account in HOST\\user or user@domain format.", Code: 87);
        try
        {
            using Process? process = Process.Start(CredentialPrompt(target, account));
            if (process is null)
                return new("Windows could not open the credential prompt.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new("Windows saved the credential. Kumpuni checks print RPC and the queue from this Windows session.", Verified: true)
                : new("Windows did not confirm that it saved the credential.", Code: process.ExitCode);
        }
        catch (Win32Exception error)
        {
            return new("Windows could not open the credential prompt.", Code: error.NativeErrorCode);
        }
    }

    public static ActionResult SignIn(Target target, IntPtr owner)
    {
        var resource = new NetResource { RemoteName = $@"\\{target.Host}\IPC$" };
        int code = WNetAddConnection3W(owner, resource, null, null, ConnectTemporary | ConnectInteractive | ConnectPrompt);
        return DescribeResult(code, code == ExtendedError ? ReadProviderError() : null);
    }

    internal static ActionResult DescribeResult(int code, int? providerCode = null)
    {
        return code switch
        {
            0 => new("Windows signed in to the print host. Kumpuni checks the queue again.", Verified: true),
            1223 => new("Sign-in was cancelled. Nothing was changed.", Code: code),
            1219 => new("Windows already has a session to this host with a different account. "
                + "Disconnect the other connections to the host or sign out of Windows, then try again.", Code: code),
            86 or 1326 => new("The print host did not accept the user name or password.", Code: code),
            1327 or 1385 => new("Windows restricted this account's network sign-in. Use an authorized host account with a password.", Code: code),
            1272 or Connectivity.GuestSigningRequired or Connectivity.GuestEncryptionRequired =>
                new("Windows still received a guest session. Use an authorized account with a password and check the host's local account sharing policy.", Code: code),
            53 or 67 => new("Windows could not reach the print host to sign in.", Code: code),
            ExtendedError => new(providerCode is int detail
                ? $"Windows could not sign in to the print host. The network provider returned {Diagnose.CodeText(detail)}."
                : "Windows could not sign in to the print host. The network provider did not supply a more specific code.", Code: code),
            _ => new("Windows did not complete the sign-in to the print host.", Code: code)
        };
    }

    private static int? ReadProviderError()
    {
        // The extended error is thread-local. Read it before any other WNet call.
        // Keep the numeric code in reports because provider text can identify the user or host.
        var description = new StringBuilder(512);
        var provider = new StringBuilder(256);
        int status = WNetGetLastErrorW(out int code, description, description.Capacity, provider, provider.Capacity);
        return status == 0 && code is not (0 or ExtendedError) ? code : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection3W(IntPtr owner, NetResource resource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetGetLastErrorW(out int error, StringBuilder description, int descriptionLength,
        StringBuilder provider, int providerLength);
}

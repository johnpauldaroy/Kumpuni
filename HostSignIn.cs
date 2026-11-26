using System.Runtime.InteropServices;

namespace Kumpuni;

// Opens an authenticated SMB session to the print host through Windows' own credential prompt.
// Kumpuni never receives the password: Windows collects it and, when the user selects
// "Remember my credentials", stores it in Credential Manager for this Windows user.
internal static class HostSignIn
{
    private const int ConnectTemporary = 0x4;
    private const int ConnectInteractive = 0x8;
    private const int ConnectPrompt = 0x10;

    public static ActionResult SignIn(Target target, IntPtr owner)
    {
        var resource = new NetResource { RemoteName = $@"\\{target.Host}\IPC$" };
        int code = WNetAddConnection3W(owner, resource, null, null, ConnectTemporary | ConnectInteractive | ConnectPrompt);
        return code switch
        {
            0 => new("Windows signed in to the print host. Kumpuni checks the queue again.", Verified: true),
            1223 => new("Sign-in was cancelled. Nothing was changed.", Code: code),
            1219 => new("Windows already has a session to this host with a different account. "
                + "Disconnect the other connections to the host or sign out of Windows, then try again.", Code: code),
            86 or 1326 => new("The print host did not accept the user name or password.", Code: code),
            53 or 67 => new("Windows could not reach the print host to sign in.", Code: code),
            _ => new("Windows did not complete the sign-in to the print host.", Code: code)
        };
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
}

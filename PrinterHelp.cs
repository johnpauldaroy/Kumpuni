using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Kumpuni;

internal enum PrinterConnection { All, Usb, Network, Shared }
internal enum PrinterEndpointStatus { Reachable, Unreachable, NameNotResolved, NameResolutionTimedOut }

internal sealed record UsbPrinterCheck(DateTimeOffset CheckedAt, bool? Present, uint Error);
internal sealed record PrinterEndpointCheck(DateTimeOffset CheckedAt, int TcpPort, PrinterEndpointStatus Status);
internal sealed record PrinterHelpEvidence(UsbPrinterCheck? Usb, PrinterEndpointCheck? Network);

internal static class PrinterHelp
{
    public static string? ParseHost(string text)
    {
        string host = text.Trim();
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            return null;

        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            return IPAddress.TryParse(host[1..^1], out IPAddress? literal)
                && literal.AddressFamily == AddressFamily.InterNetworkV6 ? literal.ToString() : null;
        }
        if (host.Contains('[') || host.Contains(']'))
            return null;
        if (IPAddress.TryParse(host, out IPAddress? address))
            return address.ToString();
        return Uri.CheckHostName(host) == UriHostNameType.Dns ? host : null;
    }

    public static (bool? Present, uint Error) ReadUsbPresence()
    {
        const uint presentOnly = 0; // CM_GET_DEVICE_INTERFACE_LIST_PRESENT
        const uint bufferSmall = 0x1A; // CR_BUFFER_SMALL
        const uint invalidData = 0x1F; // CR_INVALID_DATA
        Guid usbPrintClass = new("28D78FAD-5A12-11D1-AE5B-0000F803A8C2"); // GUID_DEVINTERFACE_USBPRINT

        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint error = CM_Get_Device_Interface_List_SizeW(out uint length, in usbPrintClass, IntPtr.Zero, presentOnly);
            if (error != 0)
                return (null, error);
            if (length is 0 or > 1_048_576)
                return (null, invalidData);

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
            try
            {
                error = CM_Get_Device_Interface_ListW(in usbPrintClass, IntPtr.Zero, buffer, length, presentOnly);
                if (error == 0)
                    return (Marshal.ReadInt16(buffer) != 0, 0);
                // Windows can add an interface between the size and list calls.
                if (error != bufferSmall)
                    return (null, error);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return (null, bufferSmall);
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClass,
        IntPtr deviceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(in Guid interfaceClass, IntPtr deviceId,
        IntPtr buffer, uint length, uint flags);
}

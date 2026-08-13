using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VolumeMixer;

public static class Net
{
    /// <summary>
    /// The IPv4 address of the interface that holds the default route, i.e. the address
    /// phones on the LAN can actually reach, regardless of VPN/Hyper-V/WSL virtual adapters.
    /// The UDP connect() never sends a packet; it only asks the OS routing table.
    /// </summary>
    public static IPAddress? GetPrimaryLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endpoint && !IPAddress.IsLoopback(endpoint.Address))
                return endpoint.Address;
        }
        catch
        {
            // no default route (offline), fall back to interface enumeration
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              && nic.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(addr => addr.Address)
                .FirstOrDefault(addr => addr.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }
}

using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PMK.Launcher.Net
{
    /// <summary>
    /// Finds a VPN adapter friends can reach the host through when the router
    /// can't be opened (Moemon's last resort): Radmin VPN (26.x.x.x), Hamachi
    /// (25.x.x.x), Tailscale (100.64.0.0/10 on its adapter) and ZeroTier.
    /// </summary>
    public static class VpnDetect
    {
        public static bool TryFind(out string name, out IPAddress address)
        {
            name = null;
            address = null;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] b = ua.Address.GetAddressBytes();
                        string found =
                            desc.Contains("radmin") || b[0] == 26 ? "Radmin VPN" :
                            desc.Contains("hamachi") || b[0] == 25 ? "Hamachi" :
                            desc.Contains("tailscale") ? "Tailscale" :
                            desc.Contains("zerotier") ? "ZeroTier" : null;
                        if (found != null)
                        {
                            name = found;
                            address = ua.Address;
                            return true;
                        }
                    }
                }
            }
            catch (NetworkInformationException) { }
            return false;
        }
    }
}

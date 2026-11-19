using System;
using System.Threading.Tasks;
using Mono.Nat;

namespace PMK.Launcher.Net
{
    /// <summary>Asks the router to forward the host port (UPnP / NAT-PMP), and reports the public address.</summary>
    public sealed class Upnp : IDisposable
    {
        private readonly int port;
        private INatDevice device;
        public string Status { get; private set; } = "Router: looking for UPnP...";

        private Upnp(int port)
        {
            this.port = port;
        }

        public static Upnp Start(int port)
        {
            var u = new Upnp(port);
            NatUtility.DeviceFound += u.OnDeviceFound;
            NatUtility.StartDiscovery();
            Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
            {
                if (u.device == null)
                    u.Status = "Router: no UPnP - forward TCP port " + port + " or use a VPN";
            });
            return u;
        }

        private async void OnDeviceFound(object sender, DeviceEventArgs e)
        {
            if (device != null) return;
            device = e.Device;
            try
            {
                await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 0, "Protect Me Knight PC"));
                var ip = await device.GetExternalIPAsync();
                Status = $"Internet {ip}:{port} (port forwarded by UPnP)";
                Log.Write("UPnP: " + Status);
            }
            catch (Exception ex)
            {
                Status = "Router refused port forwarding - forward TCP port " + port + " manually";
                Log.Write("UPnP failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            NatUtility.DeviceFound -= OnDeviceFound;
            try { NatUtility.StopDiscovery(); } catch (Exception) { }
            var d = device;
            if (d != null)
            {
                try { d.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port)).Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            }
        }
    }
}

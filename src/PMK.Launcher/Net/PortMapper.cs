using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;

namespace PMK.Launcher.Net
{
    public enum PortMapState { Searching, Open, Failed, Off }
    public enum PortMapError { None, NoRouter, Refused, Cgnat }

    /// <summary>
    /// Opens the host's router to the internet (UPnP, or NAT-PMP on Apple
    /// routers), following Moemon Emerald Kai Co-op's design:
    /// * the mapping is leased for 2 hours and renewed every 30 minutes, and
    ///   recorded in portmap.txt, so a crash can't leave the port open: the
    ///   lease runs out, and the next start removes the leftover mapping;
    /// * if the game's port is taken on the router, the next of 8 ports is
    ///   tried (the invite code can carry any of them);
    /// * a router whose own internet address isn't public (carrier-grade or
    ///   double NAT) can't be reached from outside: that is reported plainly
    ///   instead of handing out a code that can't work.
    /// Everything runs in the background.
    /// </summary>
    public sealed class PortMapper : IDisposable
    {
        private const int LeaseSeconds = 7200;
        private static readonly TimeSpan Renew = TimeSpan.FromMinutes(30);
        private const string Description = "Protect Me Knight PC";

        private readonly int internalPort;
        private readonly string recordPath;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private INatDevice device;
        private Mapping mapping;
        private int leftoverPort = -1;

        public PortMapState State { get; private set; } = PortMapState.Searching;
        public PortMapError Error { get; private set; }
        public IPAddress PublicAddress { get; private set; }
        public int ExternalPort { get; private set; }
        public string Method { get; private set; } = "";
        public string InviteCode => State == PortMapState.Open ? Net.InviteCode.Make(PublicAddress, ExternalPort) : null;

        private PortMapper(string dataDir, int internalPort)
        {
            this.internalPort = internalPort;
            recordPath = Path.Combine(dataDir, "portmap.txt");
            try
            {
                if (File.Exists(recordPath) && int.TryParse(File.ReadAllText(recordPath).Trim(), out int p))
                    leftoverPort = p;
            }
            catch (IOException) { }
        }

        public static PortMapper Start(string dataDir, int internalPort)
        {
            var m = new PortMapper(dataDir, internalPort);
            NatUtility.DeviceFound += m.OnDeviceFound;
            NatUtility.StartDiscovery(NatProtocol.Upnp, NatProtocol.Pmp);
            Task.Delay(TimeSpan.FromSeconds(8), m.cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && m.device == null && m.State == PortMapState.Searching)
                {
                    m.Error = PortMapError.NoRouter;
                    m.State = PortMapState.Failed;
                    Log.Write("Router: no UPnP / NAT-PMP answer");
                }
            });
            return m;
        }

        private async void OnDeviceFound(object sender, DeviceEventArgs e)
        {
            lock (this)
            {
                if (device != null || cts.IsCancellationRequested) return;
                device = e.Device;
            }
            Method = e.Device.NatProtocol == NatProtocol.Upnp ? "UPnP" : "NAT-PMP";
            try
            {
                await OpenAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Router: " + ex.Message);
                if (State == PortMapState.Searching)
                {
                    Error = PortMapError.Refused;
                    State = PortMapState.Failed;
                }
            }
        }

        private async Task OpenAsync()
        {
            // A mapping left over from a crash.
            if (leftoverPort > 0)
            {
                try { await device.DeletePortMapAsync(new Mapping(Protocol.Tcp, internalPort, leftoverPort)); } catch (Exception) { }
                DeleteRecord();
            }

            bool anyRefusedForConflict = false;
            for (int i = 0; i < Net.InviteCode.PortCount && !cts.IsCancellationRequested; i++)
            {
                int ext = Net.InviteCode.DefaultPort + i;
                int lease = LeaseSeconds;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        mapping = await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, internalPort, ext, lease, Description));
                        ExternalPort = mapping.PublicPort > 0 ? mapping.PublicPort : ext;
                        break;
                    }
                    catch (MappingException ex) when ((int)ex.ErrorCode == 725 && lease != 0)
                    {
                        lease = 0; // the router only takes permanent mappings: the record file still cleans it up
                    }
                    catch (MappingException ex) when ((int)ex.ErrorCode == 718)
                    {
                        anyRefusedForConflict = true; // another PC has this port
                        mapping = null;
                        break;
                    }
                }
                if (mapping != null) break;
            }
            if (mapping == null)
            {
                Error = PortMapError.Refused;
                State = PortMapState.Failed;
                Log.Write("Router refused the port mapping" + (anyRefusedForConflict ? " (all 8 ports in use)" : ""));
                return;
            }
            WriteRecord();

            IPAddress routerIp = null;
            try { routerIp = await device.GetExternalIPAsync(); } catch (Exception) { }
            if (routerIp == null || routerIp.Equals(IPAddress.Any) || Net.InviteCode.IsNonPublic(routerIp))
            {
                var seen = await AskPublicIpAsync();
                if ((routerIp == null || routerIp.Equals(IPAddress.Any)) && seen != null && !Net.InviteCode.IsNonPublic(seen))
                {
                    routerIp = seen;
                }
                else
                {
                    Log.Write($"Router address {routerIp} is not public: carrier-grade or double NAT");
                    await RemoveAsync();
                    Error = PortMapError.Cgnat;
                    State = PortMapState.Failed;
                    return;
                }
            }
            PublicAddress = routerIp;
            State = PortMapState.Open;
            Log.Write($"Router: opened TCP {ExternalPort} -> {internalPort} via {Method}, internet address {PublicAddress}");
            _ = RenewLoop();
        }

        private async Task RenewLoop()
        {
            while (!cts.IsCancellationRequested && mapping != null && mapping.Lifetime != 0)
            {
                try { await Task.Delay(Renew, cts.Token); } catch (TaskCanceledException) { return; }
                try
                {
                    mapping = await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, internalPort, ExternalPort, LeaseSeconds, Description));
                }
                catch (Exception ex)
                {
                    Log.Write("Router: renewing the port mapping failed: " + ex.Message);
                }
            }
        }

        private static async Task<IPAddress> AskPublicIpAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                string s = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                return IPAddress.TryParse(s, out var ip) ? ip : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void WriteRecord()
        {
            try { File.WriteAllText(recordPath, ExternalPort.ToString()); } catch (IOException) { }
        }

        private void DeleteRecord()
        {
            try { if (File.Exists(recordPath)) File.Delete(recordPath); } catch (IOException) { }
        }

        private async Task RemoveAsync()
        {
            var d = device;
            var m = mapping;
            mapping = null;
            if (d != null && m != null)
            {
                try { await d.DeletePortMapAsync(new Mapping(Protocol.Tcp, internalPort, ExternalPort)); } catch (Exception) { }
                Log.Write($"Router: closed TCP {ExternalPort}");
            }
            DeleteRecord();
        }

        public void Dispose()
        {
            cts.Cancel();
            NatUtility.DeviceFound -= OnDeviceFound;
            try { NatUtility.StopDiscovery(); } catch (Exception) { }
            try { RemoveAsync().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            State = PortMapState.Off;
        }
    }
}

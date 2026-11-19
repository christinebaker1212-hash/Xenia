using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PMK.Launcher.Net
{
    public sealed class LanGame
    {
        public string Host;
        public int Port;
        public string Name;
        public int Players;
        public bool Locked;
        public bool Started;
        public string Version;
        public bool Compatible;
    }

    /// <summary>
    /// Finds games hosted on the same network, after Moemon's LAN discovery:
    /// the joiner sends a UDP probe to the broadcast address of every adapter
    /// (Windows only sends the plain broadcast through one), plus loopback for
    /// a game hosted on this PC; hosts answer with their name, players and
    /// port. Internet games never appear here: those use an invite code.
    /// </summary>
    public static class LanDiscovery
    {
        public const int Port = 47821;
        private static readonly byte[] ProbeMagic = Encoding.ASCII.GetBytes("PMK?");
        private static readonly byte[] AnswerMagic = Encoding.ASCII.GetBytes("PMK!");

        /// <summary>What a host advertises (read on the responder thread).</summary>
        public sealed class Advert
        {
            public volatile string Name = "Game";
            public volatile int Players;
            public volatile bool Locked;
            public volatile bool Started;
            public int TcpPort;
        }

        /// <summary>Answers probes until disposed. Returns null if the UDP port is busy.</summary>
        public static IDisposable StartResponder(Advert advert)
        {
            UdpClient udp;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            }
            catch (SocketException ex)
            {
                Log.Write($"LAN discovery unavailable (UDP {Port}): {ex.Message}");
                return null;
            }
            var cts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync(cts.Token);
                        var b = res.Buffer;
                        if (b.Length < 6 || !b.AsSpan(0, 4).SequenceEqual(ProbeMagic)) continue;
                        var ms = new MemoryStream();
                        var w = new BinaryWriter(ms, Encoding.UTF8);
                        w.Write(AnswerMagic);
                        w.Write(NetProtocol.Version);
                        w.Write((ushort)advert.TcpPort);
                        w.Write(advert.Name ?? "Game");
                        w.Write((byte)advert.Players);
                        w.Write((byte)((advert.Locked ? 1 : 0) | (advert.Started ? 2 : 0)));
                        w.Write(AppInfo.Version);
                        var reply = ms.ToArray();
                        await udp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException) { }
                }
            });
            return new Disposer(() => { cts.Cancel(); udp.Dispose(); });
        }

        private sealed class Disposer : IDisposable
        {
            private Action action;
            public Disposer(Action a) { action = a; }
            public void Dispose() { action?.Invoke(); action = null; }
        }

        /// <summary>A running search; call Probe() to (re)send, read Games any time.</summary>
        public sealed class Search : IDisposable
        {
            private readonly UdpClient udp;
            private readonly CancellationTokenSource cts = new CancellationTokenSource();
            private readonly object gate = new object();
            private readonly List<LanGame> games = new List<LanGame>();
            public int Version { get; private set; }

            public Search()
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                Task.Run(Receive);
                Probe();
            }

            public List<LanGame> Games
            {
                get { lock (gate) return games.ToList(); }
            }

            public void Probe()
            {
                var probe = new byte[6];
                ProbeMagic.CopyTo(probe, 0);
                probe[4] = (byte)NetProtocol.Version;
                probe[5] = (byte)(NetProtocol.Version >> 8);
                foreach (var target in Targets())
                {
                    try { udp.Send(probe, probe.Length, new IPEndPoint(target, Port)); }
                    catch (SocketException) { }
                }
            }

            private static IEnumerable<IPAddress> Targets()
            {
                var list = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                            byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                            if (mask.All(b => b == 255) || mask.All(b => b == 0)) continue;
                            var bc = new byte[4];
                            for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                            list.Add(new IPAddress(bc));
                        }
                    }
                }
                catch (NetworkInformationException) { }
                return list.Distinct();
            }

            private async Task Receive()
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var res = await udp.ReceiveAsync(cts.Token);
                        var r = new BinaryReader(new MemoryStream(res.Buffer), Encoding.UTF8);
                        if (!r.ReadBytes(4).AsSpan().SequenceEqual(AnswerMagic)) continue;
                        ushort proto = r.ReadUInt16();
                        int port = r.ReadUInt16();
                        string name = r.ReadString();
                        int players = r.ReadByte();
                        int flags = r.ReadByte();
                        string version = r.ReadString();
                        var g = new LanGame
                        {
                            Host = res.RemoteEndPoint.Address.ToString(),
                            Port = port,
                            Name = name,
                            Players = players,
                            Locked = (flags & 1) != 0,
                            Started = (flags & 2) != 0,
                            Version = version,
                            Compatible = proto == NetProtocol.Version && version == AppInfo.Version,
                        };
                        lock (gate)
                        {
                            // The same game seen through two adapters (or loopback + LAN): keep one, prefer loopback.
                            var same = games.FindIndex(x => x.Name == g.Name && x.Port == g.Port);
                            if (same >= 0)
                            {
                                if (g.Host == "127.0.0.1") games[same].Host = g.Host;
                                games[same].Players = g.Players;
                                games[same].Started = g.Started;
                                games[same].Locked = g.Locked;
                            }
                            else
                            {
                                games.Add(g);
                            }
                            Version++;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) when (ex is SocketException || ex is EndOfStreamException || ex is IOException) { }
                }
            }

            public void Dispose()
            {
                cts.Cancel();
                udp.Dispose();
            }
        }
    }
}

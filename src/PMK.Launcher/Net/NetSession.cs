using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher.Net
{
    /// <summary>
    /// Online session over TCP. The host relays every message, so clients
    /// only need to reach the host (port forwarding via UPnP when available,
    /// or a VPN such as Tailscale/ZeroTier).
    /// </summary>
    public sealed class NetSession : IDisposable
    {
        private sealed class Peer
        {
            public int Id;
            public string Name;
            public int LocalPlayers;
            public Connection Conn;      // null for the local machine
            public int LastInputFrame = -1;
            public long PingMs = -1;
        }

        private sealed class Connection
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public volatile bool Closed;
            public int PeerId = -1;
            public string Remote;
        }

        private readonly Settings settings;
        private readonly string gameHash;
        private readonly ConcurrentQueue<(Connection conn, byte[] msg)> inbox = new ConcurrentQueue<(Connection, byte[])>();
        private readonly List<Peer> peers = new List<Peer>();
        private readonly Dictionary<int, ulong> localHashes = new Dictionary<int, ulong>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private TcpListener listener;
        private Connection hostConn;
        private int localId;
        private int nextPeerId = 1;
        private string status;
        private Upnp upnp;
        private long lastPingTicks;

        public bool IsHost { get; }
        public NetStartInfo StartInfo { get; private set; }
        public NetInput Input { get; private set; }
        public string FatalError { get; private set; }
        public string DesyncWarning { get; private set; }

        private NetSession(Settings settings, string gameHash, bool host)
        {
            this.settings = settings;
            this.gameHash = gameHash ?? "";
            IsHost = host;
        }

        // ------------------------------------------------------------------ setup

        public static NetSession Host(Settings settings, string gameHash)
        {
            var s = new NetSession(settings, gameHash, true);
            s.localId = 0;
            s.peers.Add(new Peer { Id = 0, Name = settings.PlayerName, LocalPlayers = settings.LocalPlayers });
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    try
                    {
                        s.listener = new TcpListener(IPAddress.IPv6Any, settings.Port);
                        s.listener.Server.DualMode = true;
                        s.listener.Start();
                    }
                    catch (SocketException)
                    {
                        s.listener = null; // IPv6 present but unusable: fall back to IPv4
                    }
                }
                if (s.listener == null)
                {
                    s.listener = new TcpListener(IPAddress.Any, settings.Port);
                    s.listener.Start();
                }
                s.status = $"Waiting for players on port {settings.Port}";
                Task.Run(() => s.AcceptLoop(s.cts.Token));
                if (settings.UseUpnp)
                    s.upnp = Upnp.Start(settings.Port);
                Log.Write($"Hosting on port {settings.Port}");
            }
            catch (SocketException ex)
            {
                s.FatalError = $"Could not open port {settings.Port}: {ex.Message}";
            }
            return s;
        }

        public static NetSession Join(Settings settings, string address, string gameHash)
        {
            var s = new NetSession(settings, gameHash, false);
            s.status = "Connecting to " + address + "...";
            Task.Run(() => s.ConnectAsync(address));
            return s;
        }

        private async Task ConnectAsync(string address)
        {
            try
            {
                var (host, port) = ParseAddress(address, settings.Port);
                // TcpClient() picks IPv4 or IPv6 from what the name resolves to.
                var client = new TcpClient();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await client.ConnectAsync(host, port, timeout.Token);
                client.NoDelay = true;
                var conn = new Connection { Client = client, Stream = client.GetStream(), Remote = address };
                hostConn = conn;
                StartReader(conn);
                Send(conn, NetProtocol.Build(MsgType.Hello, w =>
                {
                    w.Write(NetProtocol.Version);
                    w.Write(AppInfo.Version);
                    w.Write(gameHash);
                    w.Write(settings.PlayerName);
                    w.Write((byte)settings.LocalPlayers);
                }));
                status = "Connected, waiting for the host to start";
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException || ex is IOException || ex is FormatException)
            {
                FatalError = $"Could not connect to {address}: {ex.Message}";
            }
        }

        public static (string host, int port) ParseAddress(string address, int defaultPort)
        {
            address = address.Trim();
            if (address.StartsWith("["))
            {
                int end = address.IndexOf(']');
                string h = address.Substring(1, end - 1);
                int p = end + 2 <= address.Length && address.Length > end + 1 && address[end + 1] == ':' ? int.Parse(address.Substring(end + 2)) : defaultPort;
                return (h, p);
            }
            int colon = address.LastIndexOf(':');
            if (colon > 0 && address.IndexOf(':') == colon)
                return (address.Substring(0, colon), int.Parse(address.Substring(colon + 1)));
            return (address, defaultPort);
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
                    var conn = new Connection { Client = client, Stream = client.GetStream(), Remote = client.Client.RemoteEndPoint?.ToString() };
                    StartReader(conn);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { }
            }
        }

        private void StartReader(Connection conn)
        {
            var t = new Thread(() =>
            {
                var header = new byte[2];
                try
                {
                    while (!conn.Closed)
                    {
                        ReadExact(conn.Stream, header, 2);
                        int len = header[0] | (header[1] << 8);
                        var msg = new byte[len];
                        ReadExact(conn.Stream, msg, len);
                        inbox.Enqueue((conn, msg));
                    }
                }
                catch (Exception) { }
                conn.Closed = true;
                inbox.Enqueue((conn, null));
            })
            { IsBackground = true, Name = "net-reader" };
            t.Start();
        }

        private static void ReadExact(Stream s, byte[] buf, int n)
        {
            int read = 0;
            while (read < n)
            {
                int r = s.Read(buf, read, n - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }
        }

        private void Send(Connection c, byte[] msg)
        {
            if (c == null || c.Closed) return;
            try
            {
                lock (c.WriteLock) c.Stream.Write(msg, 0, msg.Length);
            }
            catch (Exception)
            {
                c.Closed = true;
            }
        }

        private void Broadcast(byte[] msg, Connection except = null)
        {
            foreach (var p in peers)
                if (p.Conn != null && p.Conn != except) Send(p.Conn, msg);
        }

        // ------------------------------------------------------------------ main-thread pump

        public void Pump()
        {
            while (inbox.TryDequeue(out var item))
            {
                try
                {
                    if (item.msg == null) OnDisconnected(item.conn);
                    else Handle(item.conn, item.msg);
                }
                catch (Exception ex) when (ex is IOException || ex is FormatException || ex is ArgumentException)
                {
                    Log.Write("Bad message: " + ex.Message);
                }
            }
            // Ping once a second for the lobby/status display.
            long now = Environment.TickCount64;
            if (now - lastPingTicks > 1000)
            {
                lastPingTicks = now;
                var ping = NetProtocol.Build(MsgType.Ping, w => w.Write(now));
                if (IsHost) Broadcast(ping); else Send(hostConn, ping);
            }
        }

        private void Handle(Connection conn, byte[] msg)
        {
            var r = new BinaryReader(new MemoryStream(msg), Encoding.UTF8);
            var type = (MsgType)r.ReadByte();
            switch (type)
            {
                case MsgType.Hello when IsHost: OnHello(conn, r); break;
                case MsgType.Welcome when !IsHost:
                    localId = r.ReadByte();
                    break;
                case MsgType.Reject when !IsHost:
                    FatalError = "The host refused the connection: " + r.ReadString();
                    break;
                case MsgType.Lobby when !IsHost: OnLobby(r); break;
                case MsgType.Start when !IsHost: OnStart(r); break;
                case MsgType.Input: OnInput(conn, msg, r); break;
                case MsgType.Hash when IsHost: OnHash(r); break;
                case MsgType.Desync:
                    DesyncWarning = $"Desync detected at frame {r.ReadInt32()} - restart the online game";
                    break;
                case MsgType.PeerLeft when !IsHost:
                    {
                        int id = r.ReadByte();
                        int from = r.ReadInt32();
                        Input?.PeerLeft(id, from);
                        peers.RemoveAll(p => p.Id == id);
                        break;
                    }
                case MsgType.Ping:
                    Send(conn, NetProtocol.Build(MsgType.Pong, w => w.Write(r.ReadInt64())));
                    break;
                case MsgType.Pong:
                    {
                        long sent = r.ReadInt64();
                        long ms = Environment.TickCount64 - sent;
                        var p = IsHost ? peers.FirstOrDefault(x => x.Conn == conn) : peers.FirstOrDefault(x => x.Id == 0);
                        if (p != null) p.PingMs = ms;
                        else if (!IsHost) hostPing = ms;
                        break;
                    }
            }
        }

        private long hostPing = -1;

        private void OnHello(Connection conn, BinaryReader r)
        {
            ushort proto = r.ReadUInt16();
            string version = r.ReadString();
            string hash = r.ReadString();
            string name = r.ReadString();
            int players = Math.Clamp((int)r.ReadByte(), 1, 4);
            string reason = null;
            if (proto != NetProtocol.Version || version != AppInfo.Version)
                reason = $"version mismatch (host v{AppInfo.Version}, you v{version}) - update both copies";
            else if (!string.Equals(hash, gameHash, StringComparison.OrdinalIgnoreCase))
                reason = "your game files differ from the host's";
            else if (StartInfo != null)
                reason = "the game has already started";
            else if (peers.Sum(p => p.LocalPlayers) + players > 4)
                reason = "the game is full (4 players maximum)";
            if (reason != null)
            {
                Send(conn, NetProtocol.Build(MsgType.Reject, w => w.Write(reason)));
                Log.Write($"Rejected {conn.Remote}: {reason}");
                Task.Delay(500).ContinueWith(_ => { try { conn.Client.Close(); } catch (Exception) { } });
                return;
            }
            var peer = new Peer { Id = nextPeerId++, Name = Clean(name), LocalPlayers = players, Conn = conn };
            conn.PeerId = peer.Id;
            peers.Add(peer);
            Send(conn, NetProtocol.Build(MsgType.Welcome, w => w.Write((byte)peer.Id)));
            Log.Write($"{peer.Name} joined from {conn.Remote}");
            BroadcastLobby();
        }

        private static string Clean(string s)
        {
            s = new string((s ?? "").Where(c => c >= 32 && c <= 126).ToArray()).Trim();
            if (s.Length == 0) s = "Player";
            return s.Length > 16 ? s.Substring(0, 16) : s;
        }

        private void BroadcastLobby()
        {
            var msg = NetProtocol.Build(MsgType.Lobby, w =>
            {
                w.Write((byte)peers.Count);
                foreach (var p in peers)
                {
                    w.Write((byte)p.Id);
                    w.Write(p.Name);
                    w.Write((byte)p.LocalPlayers);
                }
            });
            Broadcast(msg);
        }

        private void OnLobby(BinaryReader r)
        {
            peers.Clear();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
                peers.Add(new Peer { Id = r.ReadByte(), Name = r.ReadString(), LocalPlayers = r.ReadByte() });
        }

        public int PlayerCount => peers.Sum(p => p.LocalPlayers);

        public bool CanStart => IsHost && StartInfo == null && FatalError == null && peers.Count >= 1;

        public void StartGame()
        {
            if (!CanStart) return;
            var info = new NetStartInfo
            {
                Seed = new Random().Next(1, int.MaxValue),
                InputDelay = settings.InputDelayFrames,
            };
            int slot = 0;
            foreach (var p in peers.OrderBy(p => p.Id))
                for (int i = 0; i < p.LocalPlayers && slot < 4; i++)
                    info.SlotOwners[slot++] = (byte)p.Id;
            Broadcast(NetProtocol.Build(MsgType.Start, w =>
            {
                w.Write(info.Seed);
                w.Write((byte)info.InputDelay);
                w.Write(info.SlotOwners);
            }));
            Begin(info);
        }

        private void OnStart(BinaryReader r)
        {
            var info = new NetStartInfo
            {
                Seed = r.ReadInt32(),
                InputDelay = r.ReadByte(),
                SlotOwners = r.ReadBytes(4),
            };
            Begin(info);
        }

        /// <summary>Hook for local input sampling: (local player index, frame) -> pad state.</summary>
        public Func<int, int, GamePadState> SampleLocal { get; set; }

        private void Begin(NetStartInfo info)
        {
            listener?.Stop(); // no late joiners
            Input = new NetInput(info, localId, (i, f) => SampleLocal?.Invoke(i, f) ?? default, SendInputs, SendHash);
            StartInfo = info;
            status = "In game";
        }

        private void SendInputs(int frame, (int slot, GamePadState state)[] batch)
        {
            var msg = NetProtocol.Build(MsgType.Input, w =>
            {
                w.Write((byte)localId);
                w.Write(frame);
                w.Write((byte)batch.Length);
                foreach (var (slot, state) in batch)
                {
                    w.Write((byte)slot);
                    NetProtocol.WriteState(w, state);
                }
            });
            if (IsHost) Broadcast(msg); else Send(hostConn, msg);
        }

        private void OnInput(Connection conn, byte[] raw, BinaryReader r)
        {
            int peerId = r.ReadByte();
            int frame = r.ReadInt32();
            int n = r.ReadByte();
            if (IsHost)
            {
                // Clients may only send their own inputs; relay them to everyone else.
                var peer = peers.FirstOrDefault(p => p.Conn == conn);
                if (peer == null || peer.Id != peerId) return;
                peer.LastInputFrame = Math.Max(peer.LastInputFrame, frame);
                var framed = new byte[raw.Length + 2];
                framed[0] = (byte)raw.Length;
                framed[1] = (byte)(raw.Length >> 8);
                Buffer.BlockCopy(raw, 0, framed, 2, raw.Length);
                Broadcast(framed, conn);
            }
            for (int i = 0; i < n; i++)
            {
                int slot = r.ReadByte();
                var state = NetProtocol.ReadState(r);
                if (StartInfo != null && slot >= 0 && slot < 4 && StartInfo.SlotOwners[slot] == peerId)
                    Input?.Receive(frame, slot, state);
            }
        }

        private void SendHash(int frame, ulong hash)
        {
            if (frame % 600 == 0) Log.Write($"Net frame {frame} hash {hash:X16}");
            if (IsHost)
            {
                localHashes[frame] = hash;
                CheckHash(frame, hash, 0);
            }
            else
            {
                Send(hostConn, NetProtocol.Build(MsgType.Hash, w => { w.Write((byte)localId); w.Write(frame); w.Write(hash); }));
            }
        }

        private readonly Dictionary<int, List<(int peer, ulong hash)>> pendingHashes = new Dictionary<int, List<(int, ulong)>>();

        private void OnHash(BinaryReader r)
        {
            int peer = r.ReadByte();
            int frame = r.ReadInt32();
            ulong hash = r.ReadUInt64();
            if (localHashes.TryGetValue(frame, out ulong mine))
                Compare(frame, mine, peer, hash);
            else
            {
                if (!pendingHashes.TryGetValue(frame, out var list)) pendingHashes[frame] = list = new List<(int, ulong)>();
                list.Add((peer, hash));
            }
        }

        private void CheckHash(int frame, ulong mine, int self)
        {
            if (pendingHashes.TryGetValue(frame, out var list))
            {
                foreach (var (peer, hash) in list) Compare(frame, mine, peer, hash);
                pendingHashes.Remove(frame);
            }
            foreach (var k in localHashes.Keys.Where(k => k < frame - NetInput.HashInterval * 10).ToList())
                localHashes.Remove(k);
        }

        private void Compare(int frame, ulong mine, int peer, ulong theirs)
        {
            if (mine == theirs || DesyncWarning != null) return;
            DesyncWarning = $"Desync detected at frame {frame} - restart the online game";
            Log.Write($"Desync with peer {peer} at frame {frame}: {mine:X16} vs {theirs:X16}");
            Broadcast(NetProtocol.Build(MsgType.Desync, w => w.Write(frame)));
        }

        private void OnDisconnected(Connection conn)
        {
            if (!IsHost)
            {
                if (conn == hostConn && FatalError == null)
                    FatalError = StartInfo != null ? "The connection to the host was lost." : "The host closed the session.";
                return;
            }
            var peer = peers.FirstOrDefault(p => p.Conn == conn);
            if (peer == null) return;
            peers.Remove(peer);
            Log.Write($"{peer.Name} left");
            if (StartInfo != null)
            {
                int from = peer.LastInputFrame + 1;
                Input?.PeerLeft(peer.Id, from);
                Broadcast(NetProtocol.Build(MsgType.PeerLeft, w => { w.Write((byte)peer.Id); w.Write(from); }));
            }
            else
            {
                BroadcastLobby();
            }
        }

        // ------------------------------------------------------------------ display

        public string LobbyText()
        {
            var sb = new StringBuilder();
            if (FatalError != null) return FatalError;
            sb.Append(status).Append('\n');
            int slot = 1;
            foreach (var p in peers.OrderBy(p => p.Id))
            {
                string slots = string.Join(",", Enumerable.Range(slot, Math.Min(p.LocalPlayers, 5 - slot)).Select(s => "P" + s));
                slot += p.LocalPlayers;
                string ping = p.Id == localId ? "you" : p.PingMs >= 0 ? p.PingMs + " ms" : IsHost ? "..." : hostPing >= 0 && p.Id == 0 ? hostPing + " ms" : "";
                sb.Append($"{p.Name}  [{slots}]  {ping}\n");
            }
            if (IsHost)
            {
                sb.Append("Share one of these addresses:\n");
                foreach (var a in LocalAddresses().Take(3)) sb.Append($"  LAN {a}:{settings.Port}\n");
                if (upnp != null) sb.Append("  ").Append(upnp.Status).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        public string StatusLine()
        {
            if (Input == null) return status;
            string ping = IsHost
                ? string.Join(" ", peers.Where(p => p.Conn != null).Select(p => $"{p.Name}:{p.PingMs}ms"))
                : $"host ping {hostPing} ms";
            return $"Online - frame {Input.Frame} - {ping}";
        }

        public static IEnumerable<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(ua.Address.ToString());
                }
            }
            catch (NetworkInformationException) { }
            return list;
        }

        public void Dispose()
        {
            cts.Cancel();
            try { listener?.Stop(); } catch (SocketException) { }
            foreach (var p in peers)
                if (p.Conn != null) { p.Conn.Closed = true; try { p.Conn.Client.Close(); } catch (Exception) { } }
            if (hostConn != null) { hostConn.Closed = true; try { hostConn.Client.Close(); } catch (Exception) { } }
            upnp?.Dispose();
        }
    }
}

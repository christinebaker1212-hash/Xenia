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
    /// Online session over TCP. The host relays every message, so clients only
    /// need to reach the host. Connection handling follows Moemon Emerald Kai
    /// Co-op (src/mp): invite codes, router port mapping with CGNAT detection,
    /// VPN fallback, LAN discovery, hashed room passwords, keepalive and idle
    /// timeouts, and automatic reconnection to a game in progress.
    /// </summary>
    public sealed class NetSession : IDisposable
    {
        /// <summary>How long a dropped player may take to reconnect before the game goes on without them.</summary>
        public const int ReconnectGraceMs = 20000;
        private const int IdleTimeoutMs = 15000;
        private const int KeepAliveMs = 1000;

        private sealed class Peer
        {
            public int Id;
            public string Name;
            public int LocalPlayers;
            public bool IsLocal;
            public Connection Conn;           // null for the local machine, or while disconnected
            public int LastInputFrame = -1;
            public long PingMs = -1;
            public long DisconnectedAt;       // TickCount64 when the connection dropped (0 = connected)
        }

        private sealed class Connection
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object WriteLock = new object();
            public volatile bool Closed;
            public long LastReceive = Environment.TickCount64;
            public string Remote;
        }

        private readonly Settings settings;
        private readonly string gameHash;
        private readonly string dataDir;
        private readonly ConcurrentQueue<(Connection conn, byte[] msg)> inbox = new ConcurrentQueue<(Connection, byte[])>();
        private readonly List<Peer> peers = new List<Peer>();
        private readonly List<Connection> pending = new List<Connection>();   // connected, not yet welcomed
        private readonly Dictionary<int, ulong> localHashes = new Dictionary<int, ulong>();
        private readonly Dictionary<int, List<(int peer, ulong hash)>> pendingHashes = new Dictionary<int, List<(int, ulong)>>();
        private readonly List<(int frame, byte[] framed)> inputHistory = new List<(int, byte[])>(); // host: for resuming peers
        private readonly List<(int peer, int from)> leftEvents = new List<(int, int)>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly byte[] roomKey;
        private readonly ulong sessionId;
        private TcpListener listener;
        private Connection hostConn;
        private int localId;
        private int nextPeerId = 1;
        private string status;
        private PortMapper portMapper;
        private IDisposable lanResponder;
        private readonly LanDiscovery.Advert advert = new LanDiscovery.Advert();
        private Timer keepAlive;
        private long hostPing = -1;
        private bool codeCopied;

        // Client: where to (re)connect.
        private string joinHost;
        private int joinPort;
        private long reconnectDeadline;
        private bool reconnectInFlight;
        private long nextReconnectAttempt;

        public bool IsHost { get; }
        public NetStartInfo StartInfo { get; private set; }
        public NetInput Input { get; private set; }
        public string FatalError { get; private set; }
        public string DesyncWarning { get; private set; }
        /// <summary>The host wants a (different) password: ask the player and join again.</summary>
        public bool NeedsPassword { get; private set; }
        /// <summary>The host's name, once known (remembered for REJOIN).</summary>
        public string HostName => peers.FirstOrDefault(p => p.Id == 0)?.Name;
        public bool Reconnecting => !IsHost && reconnectDeadline > 0;
        /// <summary>Test hook: drop the connection (as a network failure would) at this frame.</summary>
        public int DropAtFrame { get; set; } = -1;

        private NetSession(Settings settings, string gameHash, string dataDir, bool host, byte[] key)
        {
            this.settings = settings;
            this.gameHash = gameHash ?? "";
            this.dataDir = dataDir;
            IsHost = host;
            roomKey = key ?? new byte[32];
            var rnd = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rnd);
            sessionId = BitConverter.ToUInt64(rnd, 0);
            keepAlive = new Timer(_ => SendKeepAlive(), null, KeepAliveMs, KeepAliveMs);
        }

        // ------------------------------------------------------------------ setup

        public static NetSession Host(Settings settings, string gameHash, string dataDir)
        {
            var s = new NetSession(settings, gameHash, dataDir, true, InviteCode.RoomKey(settings.RoomPassword));
            s.localId = 0;
            s.peers.Add(new Peer { Id = 0, Name = settings.PlayerName, LocalPlayers = settings.LocalPlayers, IsLocal = true });
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
                s.status = "Waiting for players";
                Task.Run(() => s.AcceptLoop(s.cts.Token));
                if (settings.UseUpnp)
                    s.portMapper = PortMapper.Start(dataDir, settings.Port);
                s.advert.Name = settings.PlayerName;
                s.advert.TcpPort = settings.Port;
                s.advert.Locked = !string.IsNullOrEmpty(settings.RoomPassword);
                s.advert.Players = settings.LocalPlayers;
                s.lanResponder = LanDiscovery.StartResponder(s.advert);
                Log.Write($"Hosting on port {settings.Port}" + (s.advert.Locked ? " (password)" : ""));
            }
            catch (SocketException ex)
            {
                s.FatalError = $"Could not open port {settings.Port}: {ex.Message}. Is another copy of the game hosting?";
            }
            return s;
        }

        /// <summary>Joins by invite code or address. <paramref name="key"/> is the room key (InviteCode.RoomKey).</summary>
        public static NetSession Join(Settings settings, string target, string gameHash, string dataDir, byte[] key)
        {
            var s = new NetSession(settings, gameHash, dataDir, false, key);
            try
            {
                (s.joinHost, s.joinPort) = InviteCode.ResolveJoinTarget(target, settings.Port);
            }
            catch (FormatException)
            {
                s.FatalError = "That doesn't look like an invite code (like K7QM-3XPA) or an address (like 26.12.34.56).";
                return s;
            }
            s.status = "Connecting...";
            Task.Run(() => s.ConnectAsync(false));
            return s;
        }

        private async Task ConnectAsync(bool resume)
        {
            try
            {
                // TcpClient() picks IPv4 or IPv6 from what the name resolves to.
                var client = new TcpClient();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(resume ? 4 : 8)))
                    await client.ConnectAsync(joinHost, joinPort, timeout.Token);
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var conn = new Connection { Client = client, Stream = client.GetStream(), Remote = $"{joinHost}:{joinPort}" };
                hostConn = conn;
                StartReader(conn);
                Send(conn, NetProtocol.Build(MsgType.Hello, w =>
                {
                    w.Write(NetProtocol.Version);
                    w.Write(AppInfo.Version);
                    w.Write(gameHash);
                    w.Write(settings.PlayerName);
                    w.Write((byte)settings.LocalPlayers);
                    w.Write(roomKey);
                    w.Write(resume ? StartInfo.SessionId : 0UL);
                    w.Write((byte)(resume ? localId : 255));
                    w.Write(resume && Input != null ? Input.Frame : 0);
                }));
                if (!resume) status = "Connected, waiting for the host to start";
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException || ex is IOException)
            {
                if (resume)
                    Log.Write("Reconnect attempt failed: " + ex.Message);
                else
                    FatalError = $"Couldn't reach {joinHost}:{joinPort}. Ask your friend to check they're hosting, that the code is current, "
                        + "and that Windows Firewall allows the game on Private AND Public networks.";
            }
            finally
            {
                reconnectInFlight = false;
            }
        }

        public static (string host, int port) ParseAddress(string address, int defaultPort)
        {
            address = (address ?? "").Trim();
            if (address.Length == 0) throw new FormatException("empty address");
            if (address.StartsWith("["))
            {
                int end = address.IndexOf(']');
                if (end < 0) throw new FormatException("bad address");
                string h = address.Substring(1, end - 1);
                int p = address.Length > end + 1 && address[end + 1] == ':' ? int.Parse(address.Substring(end + 2)) : defaultPort;
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
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    var conn = new Connection { Client = client, Stream = client.GetStream(), Remote = client.Client.RemoteEndPoint?.ToString() };
                    lock (pending) pending.Add(conn);
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
                        Interlocked.Exchange(ref conn.LastReceive, Environment.TickCount64);
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

        private static void Send(Connection c, byte[] msg)
        {
            if (c == null || c.Closed) return;
            try
            {
                lock (c.WriteLock) c.Stream.Write(msg, 0, msg.Length);
            }
            catch (Exception)
            {
                c.Closed = true;
                try { c.Client.Close(); } catch (Exception) { }
            }
        }

        private static void Close(Connection c)
        {
            if (c == null) return;
            c.Closed = true;
            try { c.Client.Close(); } catch (Exception) { }
        }

        private void Broadcast(byte[] msg, Connection except = null)
        {
            foreach (var p in peers.ToArray())
                if (p.Conn != null && p.Conn != except) Send(p.Conn, msg);
        }

        /// <summary>
        /// Background keepalive: keeps connections alive (and idle timeouts
        /// honest) even while the game loop is busy, e.g. while the window is
        /// being dragged on Windows.
        /// </summary>
        private void SendKeepAlive()
        {
            try
            {
                var ping = NetProtocol.Build(MsgType.Ping, w => w.Write(Environment.TickCount64));
                if (IsHost) Broadcast(ping); else Send(hostConn, ping);
            }
            catch (Exception) { }
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

            long now = Environment.TickCount64;

            // Idle timeout: a connection that has gone silent is dead even if TCP hasn't noticed.
            foreach (var c in AllConnections())
                if (!c.Closed && now - Interlocked.Read(ref c.LastReceive) > IdleTimeoutMs)
                {
                    Log.Write($"No data from {c.Remote} for {IdleTimeoutMs / 1000} s: closing");
                    Close(c);
                }

            if (IsHost)
            {
                // Players who didn't come back in time: the game goes on without them.
                foreach (var p in peers.Where(p => p.DisconnectedAt > 0 && now - p.DisconnectedAt > ReconnectGraceMs).ToList())
                    DropPeer(p, "did not reconnect in time");
                advert.Players = PlayerCount;
                advert.Started = StartInfo != null;
                if (!codeCopied && portMapper?.InviteCode != null)
                {
                    codeCopied = true;
                    Clipboard(portMapper.InviteCode);
                }
            }
            else if (reconnectDeadline > 0)
            {
                if (now > reconnectDeadline)
                {
                    reconnectDeadline = 0;
                    FatalError = "Lost the connection to the host and couldn't reconnect.";
                }
                else if (!reconnectInFlight && now >= nextReconnectAttempt)
                {
                    reconnectInFlight = true;
                    nextReconnectAttempt = now + 2000;
                    Task.Run(() => ConnectAsync(true));
                }
            }

            if (DropAtFrame >= 0 && Input != null && Input.Frame >= DropAtFrame && !IsHost)
            {
                DropAtFrame = -1;
                Log.Write("Test: dropping the connection");
                Close(hostConn);
            }
        }

        private IEnumerable<Connection> AllConnections()
        {
            var list = new List<Connection>();
            if (hostConn != null) list.Add(hostConn);
            foreach (var p in peers) if (p.Conn != null) list.Add(p.Conn);
            lock (pending) list.AddRange(pending);
            return list;
        }

        private void Handle(Connection conn, byte[] msg)
        {
            var r = new BinaryReader(new MemoryStream(msg), Encoding.UTF8);
            var type = (MsgType)r.ReadByte();
            if (!IsHost && conn != hostConn) return; // a stale connection's leftovers
            switch (type)
            {
                case MsgType.Hello when IsHost: OnHello(conn, r); break;
                case MsgType.Welcome when !IsHost: OnWelcome(r); break;
                case MsgType.Reject when !IsHost: OnReject(r); break;
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
                case MsgType.PeerStatus when !IsHost:
                    {
                        int id = r.ReadByte();
                        bool connected = r.ReadByte() != 0;
                        var p = peers.FirstOrDefault(x => x.Id == id);
                        if (p != null) p.DisconnectedAt = connected ? 0 : Environment.TickCount64;
                        break;
                    }
                case MsgType.Bye:
                    if (IsHost)
                    {
                        var p = peers.FirstOrDefault(x => x.Conn == conn);
                        if (p != null) DropPeer(p, "left");
                    }
                    else
                    {
                        FatalError = "The host ended the session.";
                        reconnectDeadline = 0;
                    }
                    break;
                case MsgType.Ping:
                    Send(conn, NetProtocol.Build(MsgType.Pong, w => w.Write(r.ReadInt64())));
                    break;
                case MsgType.Pong:
                    {
                        long ms = Environment.TickCount64 - r.ReadInt64();
                        if (IsHost)
                        {
                            var p = peers.FirstOrDefault(x => x.Conn == conn);
                            if (p != null) p.PingMs = ms;
                        }
                        else hostPing = ms;
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------ host: joining and resuming

        private void OnHello(Connection conn, BinaryReader r)
        {
            lock (pending) pending.Remove(conn);
            ushort proto = r.ReadUInt16();
            string version = r.ReadString();
            string hash = r.ReadString();
            string name = r.ReadString();
            int players = Math.Clamp((int)r.ReadByte(), 1, 4);
            byte[] key = r.ReadBytes(32);
            ulong resumeSession = r.ReadUInt64();
            int resumePeer = r.ReadByte();
            int resumeFrame = r.ReadInt32();

            RejectCode code = RejectCode.Other;
            string reason = null;
            if (proto != NetProtocol.Version || version != AppInfo.Version)
                reason = $"version mismatch (host v{AppInfo.Version}, you v{version}) - update both copies";
            else if (!string.Equals(hash, gameHash, StringComparison.OrdinalIgnoreCase))
                reason = "your game files differ from the host's";
            else if (!key.AsSpan().SequenceEqual(roomKey))
            {
                code = RejectCode.Password;
                reason = key.All(b => b == 0) ? "this game has a password" : "wrong password";
            }

            if (reason == null && resumeSession != 0)
            {
                // A player reconnecting to the game in progress.
                var peer = peers.FirstOrDefault(p => p.Id == resumePeer && !p.IsLocal);
                if (StartInfo == null || resumeSession != sessionId || peer == null)
                {
                    code = RejectCode.Dropped;
                    reason = "the game went on without you (reconnected too late)";
                }
                else
                {
                    Close(peer.Conn);
                    peer.Conn = conn;
                    peer.DisconnectedAt = 0;
                    Send(conn, NetProtocol.Build(MsgType.Welcome, w => { w.Write((byte)peer.Id); w.Write(sessionId); w.Write(peer.LastInputFrame); }));
                    SendLobbyTo(conn);
                    foreach (var (pid, from) in leftEvents)
                        Send(conn, NetProtocol.Build(MsgType.PeerLeft, w => { w.Write((byte)pid); w.Write(from); }));
                    foreach (var (frame, framed) in inputHistory)
                        if (frame >= resumeFrame) Send(conn, framed);
                    Broadcast(NetProtocol.Build(MsgType.PeerStatus, w => { w.Write((byte)peer.Id); w.Write((byte)1); }), conn);
                    Log.Write($"{peer.Name} reconnected from {conn.Remote} (frame {resumeFrame})");
                    return;
                }
            }
            else if (reason == null && StartInfo != null)
                reason = "the game has already started";
            else if (reason == null && PlayerCount + players > 4)
                reason = "the game is full (4 players maximum)";

            if (reason != null)
            {
                Send(conn, NetProtocol.Build(MsgType.Reject, w => { w.Write((byte)code); w.Write(reason); }));
                Log.Write($"Rejected {conn.Remote}: {reason}");
                Task.Delay(500).ContinueWith(_ => Close(conn));
                return;
            }
            var np = new Peer { Id = nextPeerId++, Name = Clean(name), LocalPlayers = players, Conn = conn };
            peers.Add(np);
            Send(conn, NetProtocol.Build(MsgType.Welcome, w => { w.Write((byte)np.Id); w.Write(sessionId); w.Write(-1); }));
            Log.Write($"{np.Name} joined from {conn.Remote}");
            BroadcastLobby();
        }

        private static string Clean(string s)
        {
            s = new string((s ?? "").Where(c => c >= 32 && c <= 126).ToArray()).Trim();
            if (s.Length == 0) s = "Player";
            return s.Length > 16 ? s.Substring(0, 16) : s;
        }

        private byte[] LobbyMessage() => NetProtocol.Build(MsgType.Lobby, w =>
        {
            w.Write((byte)peers.Count);
            foreach (var p in peers)
            {
                w.Write((byte)p.Id);
                w.Write(p.Name);
                w.Write((byte)p.LocalPlayers);
                w.Write((byte)(p.DisconnectedAt > 0 ? 0 : 1));
            }
        });

        private void BroadcastLobby() => Broadcast(LobbyMessage());
        private void SendLobbyTo(Connection c) => Send(c, LobbyMessage());

        private void OnDisconnected(Connection conn)
        {
            lock (pending) pending.Remove(conn);
            if (!IsHost)
            {
                if (conn != hostConn || FatalError != null) return;
                if (StartInfo != null)
                {
                    // In a game: keep trying to get back in for a while (Moemon's RECONNECTING...).
                    if (reconnectDeadline == 0)
                    {
                        Log.Write("Connection to the host lost, reconnecting");
                        reconnectDeadline = Environment.TickCount64 + ReconnectGraceMs;
                        nextReconnectAttempt = Environment.TickCount64 + 500;
                    }
                }
                else
                {
                    FatalError = "The host closed the session.";
                }
                return;
            }
            var peer = peers.FirstOrDefault(p => p.Conn == conn);
            if (peer == null) return;
            if (StartInfo != null)
            {
                // Hold their place: everyone waits while they reconnect.
                peer.Conn = null;
                peer.DisconnectedAt = Environment.TickCount64;
                Log.Write($"{peer.Name} dropped; waiting up to {ReconnectGraceMs / 1000} s for them to reconnect");
                Broadcast(NetProtocol.Build(MsgType.PeerStatus, w => { w.Write((byte)peer.Id); w.Write((byte)0); }));
            }
            else
            {
                DropPeer(peer, "left");
            }
        }

        private void DropPeer(Peer peer, string why)
        {
            if (!peers.Remove(peer)) return;
            Close(peer.Conn);
            Log.Write($"{peer.Name} {why}");
            if (StartInfo != null)
            {
                int from = peer.LastInputFrame + 1;
                leftEvents.Add((peer.Id, from));
                Input?.PeerLeft(peer.Id, from);
                Broadcast(NetProtocol.Build(MsgType.PeerLeft, w => { w.Write((byte)peer.Id); w.Write(from); }));
            }
            else
            {
                BroadcastLobby();
            }
        }

        // ------------------------------------------------------------------ client side

        private void OnWelcome(BinaryReader r)
        {
            localId = r.ReadByte();
            r.ReadUInt64(); // session id (also in Start)
            int lastFromMe = r.ReadInt32();
            if (reconnectDeadline > 0)
            {
                reconnectDeadline = 0;
                Log.Write($"Reconnected to the host; resending inputs after frame {lastFromMe}");
                Input?.ResendAfter(lastFromMe);
            }
        }

        private void OnReject(BinaryReader r)
        {
            var code = (RejectCode)r.ReadByte();
            string reason = r.ReadString();
            reconnectDeadline = 0;
            if (code == RejectCode.Password)
                NeedsPassword = true;
            FatalError = "The host refused the connection: " + reason;
        }

        private void OnLobby(BinaryReader r)
        {
            peers.Clear();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var p = new Peer { Id = r.ReadByte(), Name = r.ReadString(), LocalPlayers = r.ReadByte() };
                p.DisconnectedAt = r.ReadByte() == 0 ? Environment.TickCount64 : 0;
                p.IsLocal = p.Id == localId;
                peers.Add(p);
            }
        }

        public int PlayerCount => peers.Sum(p => p.LocalPlayers);

        public bool CanStart => IsHost && StartInfo == null && FatalError == null && peers.Count >= 1;

        public void StartGame()
        {
            if (!CanStart) return;
            var info = new NetStartInfo
            {
                SessionId = sessionId,
                Seed = new Random().Next(1, int.MaxValue),
                InputDelay = settings.InputDelayFrames,
            };
            int slot = 0;
            foreach (var p in peers.OrderBy(p => p.Id))
                for (int i = 0; i < p.LocalPlayers && slot < 4; i++)
                    info.SlotOwners[slot++] = (byte)p.Id;
            Broadcast(NetProtocol.Build(MsgType.Start, w =>
            {
                w.Write(info.SessionId);
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
                SessionId = r.ReadUInt64(),
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
            // The host keeps listening: dropped players reconnect through it (new joins are refused).
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
            if (IsHost)
            {
                Remember(frame, msg);
                Broadcast(msg);
            }
            else
            {
                Send(hostConn, msg);
            }
        }

        /// <summary>Host: keeps the last minute of inputs for players who reconnect.</summary>
        private void Remember(int frame, byte[] framed)
        {
            inputHistory.Add((frame, framed));
            int cutoff = (Input?.Frame ?? 0) - 3600;
            if (inputHistory.Count > 0 && inputHistory[0].frame < cutoff)
                inputHistory.RemoveAll(e => e.frame < cutoff);
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
                if (frame <= peer.LastInputFrame) return; // resent after a reconnect: already relayed
                peer.LastInputFrame = frame;
                var framed = new byte[raw.Length + 2];
                framed[0] = (byte)raw.Length;
                framed[1] = (byte)(raw.Length >> 8);
                Buffer.BlockCopy(raw, 0, framed, 2, raw.Length);
                Remember(frame, framed);
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

        // ------------------------------------------------------------------ desync detection

        private void SendHash(int frame, ulong hash)
        {
            if (frame % 600 == 0) Log.Write($"Net frame {frame} hash {hash:X16}");
            if (IsHost)
            {
                localHashes[frame] = hash;
                if (pendingHashes.TryGetValue(frame, out var list))
                {
                    foreach (var (peer, h) in list) Compare(frame, hash, peer, h);
                    pendingHashes.Remove(frame);
                }
                foreach (var k in localHashes.Keys.Where(k => k < frame - NetInput.HashInterval * 10).ToList())
                    localHashes.Remove(k);
            }
            else
            {
                Send(hostConn, NetProtocol.Build(MsgType.Hash, w => { w.Write((byte)localId); w.Write(frame); w.Write(hash); }));
            }
        }

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

        private void Compare(int frame, ulong mine, int peer, ulong theirs)
        {
            if (mine == theirs || DesyncWarning != null) return;
            DesyncWarning = $"Desync detected at frame {frame} - restart the online game";
            Log.Write($"Desync with peer {peer} at frame {frame}: {mine:X16} vs {theirs:X16}");
            Broadcast(NetProtocol.Build(MsgType.Desync, w => w.Write(frame)));
        }

        // ------------------------------------------------------------------ display

        /// <summary>
        /// How friends can reach this host, in the spirit of Moemon's hosting
        /// message: the invite code (copied), or plainly why there isn't one.
        /// </summary>
        public string ReachabilityText()
        {
            if (!IsHost) return "";
            var sb = new StringBuilder();
            bool hasVpn = VpnDetect.TryFind(out string vpnName, out var vpnIp);
            var pm = portMapper;
            if (pm == null)
                sb.Append("Automatic port opening is off (Options > Online).\n");
            else if (pm.State == PortMapState.Searching)
                sb.Append("Opening your router to the internet...\n");
            else if (pm.State == PortMapState.Open)
                sb.Append($"Open to the internet! Invite code: {pm.InviteCode} (copied)\nFriends choose JOIN A GAME and type it.\n");
            else
            {
                string why = pm.Error switch
                {
                    PortMapError.Cgnat => "Your internet provider shares one address between many homes (CGNAT)",
                    PortMapError.Refused => "Your router refused to open the game's port",
                    _ => "Your router's automatic port opening (UPnP) is off",
                };
                sb.Append(why).Append(",\nso friends elsewhere can't reach you directly.\n");
                if (!hasVpn)
                    sb.Append(pm.Error == PortMapError.Cgnat
                        ? "Let a friend with a normal connection host, or use Radmin VPN / Tailscale.\n"
                        : "Turn on UPnP in your router, or use Radmin VPN / Tailscale.\n");
            }
            if (hasVpn)
            {
                string code = InviteCode.Make(vpnIp, settings.Port);
                sb.Append($"On {vpnName}, friends type {vpnIp}" + (code != null ? $" (or {code})" : "") + "\n");
            }
            var lan = LocalAddresses().FirstOrDefault();
            if (lan != null) sb.Append($"Same home network: they'll see you in JOIN A GAME ({lan}).\n");
            sb.Append("Windows Firewall: allow the game on Private AND Public networks.");
            return sb.ToString();
        }

        public string LobbyText()
        {
            if (FatalError != null) return FatalError;
            var sb = new StringBuilder();
            sb.Append(status).Append('\n');
            int slot = 1;
            foreach (var p in peers.OrderBy(p => p.Id))
            {
                string slots = string.Join(",", Enumerable.Range(slot, Math.Max(0, Math.Min(p.LocalPlayers, 5 - slot))).Select(s => "P" + s));
                slot += p.LocalPlayers;
                string ping = p.Id == localId ? "you" : p.DisconnectedAt > 0 ? "reconnecting..." : IsHost ? (p.PingMs >= 0 ? p.PingMs + " ms" : "...") : p.Id == 0 && hostPing >= 0 ? hostPing + " ms" : "";
                sb.Append($"{p.Name}  [{slots}]  {ping}\n");
            }
            if (IsHost)
            {
                if (!string.IsNullOrEmpty(settings.RoomPassword)) sb.Append("Password protected.\n");
                sb.Append(ReachabilityText());
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>One line shown while the game waits on the network, or null.</summary>
        public string WaitingText()
        {
            if (Reconnecting)
                return $"Reconnecting to the host... ({Math.Max(0, (reconnectDeadline - Environment.TickCount64) / 1000)} s)";
            if (Input == null || !Input.Waiting) return null;
            var names = new List<string>();
            foreach (int id in Input.WaitingForPeers())
            {
                var p = peers.FirstOrDefault(x => x.Id == id);
                if (p == null) continue;
                if (p.DisconnectedAt > 0)
                {
                    long left = Math.Max(0, (ReconnectGraceMs - (Environment.TickCount64 - p.DisconnectedAt)) / 1000);
                    names.Add($"{p.Name} (reconnecting, {left} s)");
                }
                else names.Add(p.Name);
            }
            return names.Count > 0 ? "Waiting for " + string.Join(", ", names) + "..." : "Waiting for other players...";
        }

        public string StatusLine()
        {
            if (Input == null) return status;
            string ping = IsHost
                ? string.Join(" ", peers.Where(p => !p.IsLocal).Select(p => $"{p.Name}:{(p.DisconnectedAt > 0 ? "--" : p.PingMs.ToString())}ms"))
                : $"host ping {hostPing} ms";
            string code = IsHost && portMapper?.InviteCode != null ? "  code " + portMapper.InviteCode : "";
            return $"Online - frame {Input.Frame} - {ping}{code}";
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

        private static void Clipboard(string text)
        {
            try { SDL3.SDL.SDL_SetClipboardText(text); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }

        public void Dispose()
        {
            keepAlive?.Dispose();
            keepAlive = null;
            // Say goodbye so nobody waits for a reconnect that won't come.
            var bye = NetProtocol.Build(MsgType.Bye, null);
            if (IsHost) Broadcast(bye); else Send(hostConn, bye);
            cts.Cancel();
            try { listener?.Stop(); } catch (SocketException) { }
            foreach (var p in peers) Close(p.Conn);
            lock (pending) foreach (var c in pending) Close(c);
            Close(hostConn);
            lanResponder?.Dispose();
            portMapper?.Dispose();
        }
    }
}

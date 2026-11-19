using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PMK.Compat;
using PMK.Compat.Input;

namespace PMK.Launcher.Net
{
    /// <summary>
    /// Lockstep input for online play. Frame F may only run once every
    /// player slot's input for F is known. Local input is sampled
    /// <c>delay</c> frames ahead and sent to the other machines, which hides
    /// up to delay x 16.7 ms of one-way latency.
    /// </summary>
    public sealed class NetInput : IInputProvider
    {
        public const int HashInterval = 120;

        private readonly NetStartInfo start;
        private readonly int localId;
        private readonly Func<int, int, GamePadState> sampleLocal;   // (local player index, frame) -> state
        private readonly Action<int, (int slot, GamePadState state)[]> send;
        private readonly Action<int, ulong> sendHash;
        private readonly Dictionary<int, GamePadState?[]> frames = new Dictionary<int, GamePadState?[]>();
        private readonly Dictionary<int, int> leftFrom = new Dictionary<int, int>(); // peer -> first frame without them
        private readonly List<int> localSlots = new List<int>();
        // Own inputs already sent, kept so they can be sent again after a reconnect.
        private readonly Dictionary<int, (int, GamePadState)[]> sentHistory = new Dictionary<int, (int, GamePadState)[]>();
        private int sentUpTo;
        private int stallTicks;

        public int Frame { get; private set; }
        public bool Waiting => stallTicks > 3;
        public int StallTicks => stallTicks;

        public NetInput(NetStartInfo start, int localId, Func<int, int, GamePadState> sampleLocal,
            Action<int, (int, GamePadState)[]> send, Action<int, ulong> sendHash)
        {
            this.start = start;
            this.localId = localId;
            this.sampleLocal = sampleLocal;
            this.send = send;
            this.sendHash = sendHash;
            for (int s = 0; s < 4; s++)
                if (start.SlotOwners[s] == localId) localSlots.Add(s);

            // The first `delay` frames run with everyone idle (nobody has sent anything yet).
            var idle = NetProtocol.Quantize(new GamePadState(Vector2.Zero, Vector2.Zero, 0f, 0f));
            for (int f = 0; f < start.InputDelay; f++)
            {
                var arr = new GamePadState?[4];
                for (int s = 0; s < 4; s++)
                    if (start.SlotOwners[s] != 255) arr[s] = idle;
                frames[f] = arr;
            }
            sentUpTo = start.InputDelay;
        }

        /// <summary>Remote (or relayed) input for a frame.</summary>
        public void Receive(int frame, int slot, GamePadState state)
        {
            if (frame < Frame || slot < 0 || slot > 3) return;
            if (!frames.TryGetValue(frame, out var arr))
                frames[frame] = arr = new GamePadState?[4];
            arr[slot] = state;
        }

        public void PeerLeft(int peerId, int fromFrame)
        {
            if (!leftFrom.ContainsKey(peerId)) leftFrom[peerId] = Math.Max(fromFrame, Frame);
        }

        private bool SlotGone(int slot, int frame)
        {
            int owner = start.SlotOwners[slot];
            return owner == 255 || (leftFrom.TryGetValue(owner, out int f) && frame >= f);
        }

        private void SendLocal()
        {
            while (sentUpTo <= Frame + start.InputDelay)
            {
                var batch = new (int, GamePadState)[localSlots.Count];
                for (int i = 0; i < localSlots.Count; i++)
                {
                    var raw = sampleLocal(i, sentUpTo);
                    var st = NetProtocol.Quantize(CompatHost.InputSuspended ? Idle(raw) : raw);
                    batch[i] = (localSlots[i], st);
                    Receive(sentUpTo, localSlots[i], st);
                }
                send(sentUpTo, batch);
                sentHistory[sentUpTo] = batch;
                sentHistory.Remove(sentUpTo - 3600);
                sentUpTo++;
            }
        }

        private static GamePadState Idle(GamePadState s) =>
            s.IsConnected ? new GamePadState(Vector2.Zero, Vector2.Zero, 0f, 0f) : default;

        /// <summary>Sends again every own input after <paramref name="lastReceivedByHost"/> (after a reconnect).</summary>
        public void ResendAfter(int lastReceivedByHost)
        {
            for (int f = lastReceivedByHost + 1; f < sentUpTo; f++)
                if (sentHistory.TryGetValue(f, out var batch)) send(f, batch);
        }

        /// <summary>Peer ids whose input the current frame is still waiting for.</summary>
        public IEnumerable<int> WaitingForPeers()
        {
            frames.TryGetValue(Frame, out var arr);
            var ids = new HashSet<int>();
            for (int s = 0; s < 4; s++)
                if (!SlotGone(s, Frame) && (arr == null || !arr[s].HasValue)) ids.Add(start.SlotOwners[s]);
            return ids;
        }

        public bool CanAdvance()
        {
            SendLocal();
            frames.TryGetValue(Frame, out var arr);
            for (int s = 0; s < 4; s++)
            {
                if (SlotGone(s, Frame)) continue;
                if (arr == null || !arr[s].HasValue)
                {
                    stallTicks++;
                    return false;
                }
            }
            stallTicks = 0;
            return true;
        }

        public void Latch(GamePadState[] slots)
        {
            frames.TryGetValue(Frame, out var arr);
            for (int s = 0; s < 4; s++)
                slots[s] = SlotGone(s, Frame) || arr == null || !arr[s].HasValue ? default : arr[s].Value;
        }

        public void AfterFrame()
        {
            frames.Remove(Frame);
            Frame++;
            if (Frame % HashInterval == 0)
                sendHash(Frame, DetRandom.StreamHash ^ (ulong)DetRandom.DrawCount);
        }

        public void SetVibration(int slot, float left, float right)
        {
            int local = localSlots.IndexOf(slot);
            if (local < 0) return;
            Microsoft.Xna.Framework.Input.GamePad.SetVibration((PlayerIndex)local, left, right);
        }
    }
}

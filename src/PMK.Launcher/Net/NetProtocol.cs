using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher.Net
{
    public enum MsgType : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        Lobby = 4,
        Start = 5,
        Input = 6,
        Hash = 7,
        PeerLeft = 8,
        Ping = 9,
        Pong = 10,
        Desync = 11,
    }

    public sealed class NetStartInfo
    {
        public int Seed;
        public int InputDelay;
        /// <summary>Peer id that owns each of the four player slots (255 = empty).</summary>
        public byte[] SlotOwners = { 255, 255, 255, 255 };
    }

    /// <summary>Message encoding helpers. Every message is [u16 length][u8 type][payload].</summary>
    public static class NetProtocol
    {
        public const ushort Version = 1;
        public const int StateSize = 9;

        public static byte[] Build(MsgType type, Action<BinaryWriter> body)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write((ushort)0);
            w.Write((byte)type);
            body?.Invoke(w);
            int len = (int)ms.Length - 2;
            if (len > ushort.MaxValue) throw new InvalidOperationException("Message too large");
            var buf = ms.ToArray();
            buf[0] = (byte)len;
            buf[1] = (byte)(len >> 8);
            return buf;
        }

        /// <summary>Packs a pad state into 9 bytes (buttons, connected flag, sticks, triggers).</summary>
        public static void WriteState(BinaryWriter w, GamePadState s)
        {
            ushort buttons = 0;
            for (int i = 0; i < LocalInput.AllButtons.Length; i++)
                if (s.IsButtonDown(LocalInput.AllButtons[i])) buttons |= (ushort)(1 << i);
            w.Write(buttons);
            w.Write((byte)(s.IsConnected ? 1 : 0));
            w.Write(Q(s.ThumbSticks.Left.X));
            w.Write(Q(s.ThumbSticks.Left.Y));
            w.Write(Q(s.ThumbSticks.Right.X));
            w.Write(Q(s.ThumbSticks.Right.Y));
            w.Write((byte)Math.Round(Math.Clamp(s.Triggers.Left, 0f, 1f) * 255));
            w.Write((byte)Math.Round(Math.Clamp(s.Triggers.Right, 0f, 1f) * 255));
        }

        public static GamePadState ReadState(BinaryReader r)
        {
            ushort buttons = r.ReadUInt16();
            bool connected = (r.ReadByte() & 1) != 0;
            var ls = new Vector2(D(r.ReadSByte()), D(r.ReadSByte()));
            var rs = new Vector2(D(r.ReadSByte()), D(r.ReadSByte()));
            float lt = r.ReadByte() / 255f;
            float rt = r.ReadByte() / 255f;
            if (!connected) return default;
            var list = new List<Buttons>();
            for (int i = 0; i < LocalInput.AllButtons.Length; i++)
                if ((buttons & (1 << i)) != 0) list.Add(LocalInput.AllButtons[i]);
            return new GamePadState(ls, rs, lt, rt, list.ToArray());
        }

        /// <summary>Round-trips a state through the wire format (local players use this too, so all machines agree).</summary>
        public static GamePadState Quantize(GamePadState s)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            WriteState(w, s);
            ms.Position = 0;
            return ReadState(new BinaryReader(ms));
        }

        private static sbyte Q(float v) => (sbyte)Math.Round(Math.Clamp(v, -1f, 1f) * 127f);
        private static float D(sbyte v) => v / 127f;
    }
}

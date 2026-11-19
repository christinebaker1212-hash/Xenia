using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace PMK.Launcher.Net
{
    /// <summary>
    /// Invite codes, after Moemon Emerald Kai Co-op's (src/mp/mp_portmap.c):
    /// the host's public IPv4 address plus which of the 8 game ports, as 7
    /// Crockford base-32 characters and a check character, e.g. "K7QM-3XPA".
    /// Typing mistakes between look-alike characters are forgiven
    /// (O = 0, I/L = 1, U = V) and a wrong character fails the check.
    /// </summary>
    public static class InviteCode
    {
        public const int DefaultPort = 47820;
        public const int PortCount = 8;
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static bool CanEncodePort(int port) => port >= DefaultPort && port < DefaultPort + PortCount;

        public static string Make(IPAddress address, int port)
        {
            if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !CanEncodePort(port))
                return null;
            byte[] b = address.GetAddressBytes();
            uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            ulong v = ((ulong)((port - DefaultPort) & 7) << 32) | ip;
            var c = new char[8];
            for (int i = 6; i >= 0; i--)
            {
                c[i] = Alphabet[(int)(v & 31)];
                v >>= 5;
            }
            int sum = 0;
            for (int i = 0; i < 7; i++) sum += Alphabet.IndexOf(c[i]) * (i + 3);
            c[7] = Alphabet[sum % 31];
            return new string(c, 0, 4) + "-" + new string(c, 4, 4);
        }

        private static int Value(char ch)
        {
            ch = char.ToUpperInvariant(ch);
            if (ch == 'O') ch = '0';
            if (ch == 'I' || ch == 'L') ch = '1';
            if (ch == 'U') ch = 'V';
            return Alphabet.IndexOf(ch);
        }

        public static bool TryParse(string code, out IPAddress address, out int port)
        {
            address = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(code)) return false;
            var vals = new int[8];
            int n = 0;
            foreach (char ch in code.Trim())
            {
                if (ch == '-' || ch == ' ') continue;
                int x = Value(ch);
                if (x < 0 || n >= 8) return false;
                vals[n++] = x;
            }
            if (n != 8) return false;
            ulong v = 0;
            int sum = 0;
            for (int i = 0; i < 7; i++)
            {
                v = (v << 5) | (ulong)vals[i];
                sum += vals[i] * (i + 3);
            }
            if (vals[7] != sum % 31) return false;
            uint ip = (uint)v;
            if (ip == 0) return false;
            address = new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });
            port = DefaultPort + (int)((v >> 32) & 7);
            return true;
        }

        /// <summary>
        /// What a player typed in JOIN: an invite code, or an address
        /// ("1.2.3.4", "1.2.3.4:47821", "name.example", "[::1]:47820").
        /// </summary>
        public static (string host, int port) ResolveJoinTarget(string input, int defaultPort)
        {
            if (TryParse(input, out var ip, out int port))
                return (ip.ToString(), port);
            return NetSession.ParseAddress(input, defaultPort);
        }

        /// <summary>Private, shared (carrier-grade NAT) or otherwise unreachable from the internet.</summary>
        public static bool IsNonPublic(IPAddress address)
        {
            if (address == null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return true;
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || b[0] == 0
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64)   // 100.64.0.0/10: carrier-grade NAT
                || (b[0] == 169 && b[1] == 254);
        }

        /// <summary>
        /// Room key: SHA-256 of the password, so it never crosses the network
        /// in clear (as Moemon's room keys). All zeros means "no password".
        /// </summary>
        public static byte[] RoomKey(string password)
        {
            if (string.IsNullOrEmpty(password)) return new byte[32];
            return SHA256.HashData(Encoding.UTF8.GetBytes("pmk-room\n" + password));
        }
    }
}

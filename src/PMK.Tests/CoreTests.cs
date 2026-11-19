using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PMK.Compat;
using PMK.Compat.Audio.Xact;
using PMK.Launcher;
using PMK.Launcher.Net;
using PMK.Patcher;

namespace PMK.Tests;

public class DetRandomTests
{
    [Fact]
    public void TrackedRandomMatchesSystemRandom()
    {
        var a = new Random(12345);
        var b = DetRandom.Create(12345);
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.Next(), b.Next());
            Assert.Equal(a.Next(100), b.Next(100));
            Assert.Equal(a.Next(-50, 50), b.Next(-50, 50));
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    [Fact]
    public void SameSeedGivesSameStreamHash()
    {
        ulong Run()
        {
            DetRandom.Reset(42);
            var r1 = DetRandom.Create();
            var r2 = DetRandom.Create();
            for (int i = 0; i < 100; i++) { r1.Next(10); r2.NextDouble(); }
            return DetRandom.StreamHash;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void DrawsWhileSuspendedAreNotHashed()
    {
        DetRandom.Reset(7);
        var r = DetRandom.Create();
        r.Next();
        ulong before = DetRandom.StreamHash;
        DetRandom.Suspended = true;
        try { r.Next(); } finally { DetRandom.Suspended = false; }
        Assert.Equal(before, DetRandom.StreamHash);
    }
}

public class NetProtocolTests
{
    [Fact]
    public void StateRoundTripsThroughWireFormat()
    {
        var s = new GamePadState(new Vector2(0.5f, -1f), new Vector2(0f, 0.25f), 0.3f, 1f,
            Buttons.A, Buttons.Start, Buttons.DPadLeft, Buttons.RightShoulder);
        var q = NetProtocol.Quantize(s);
        Assert.True(q.IsConnected);
        Assert.Equal(ButtonState.Pressed, q.Buttons.A);
        Assert.Equal(ButtonState.Pressed, q.Buttons.Start);
        Assert.Equal(ButtonState.Pressed, q.Buttons.RightShoulder);
        Assert.Equal(ButtonState.Released, q.Buttons.B);
        Assert.Equal(ButtonState.Pressed, q.DPad.Left);
        Assert.Equal(-1f, q.ThumbSticks.Left.Y, 2);
        Assert.Equal(1f, q.Triggers.Right, 2);
        // Quantising twice is stable, so every machine sees identical values.
        var qq = NetProtocol.Quantize(q);
        Assert.Equal(q.ThumbSticks.Left, qq.ThumbSticks.Left);
        Assert.Equal(q.Triggers.Left, qq.Triggers.Left);
    }

    [Fact]
    public void DisconnectedPadStaysDisconnected()
    {
        Assert.False(NetProtocol.Quantize(default).IsConnected);
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4", 47820)]
    [InlineData("1.2.3.4:5000", "1.2.3.4", 5000)]
    [InlineData("example.org:6000", "example.org", 6000)]
    [InlineData("[::1]:7000", "::1", 7000)]
    [InlineData("::1", "::1", 47820)]
    public void ParsesAddresses(string input, string host, int port)
    {
        var (h, p) = NetSession.ParseAddress(input, 47820);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }
}

public class LockstepTests
{
    /// <summary>Two machines exchanging inputs through in-memory queues.</summary>
    private sealed class Pair
    {
        public NetInput A, B;
        public readonly Queue<(int frame, (int, GamePadState)[] batch)> toB = new(), toA = new();

        public Pair(int delay)
        {
            var info = new NetStartInfo { Seed = 1, InputDelay = delay, SlotOwners = new byte[] { 0, 1, 255, 255 } };
            A = new NetInput(info, 0, (i, f) => Press(f), (f, b) => toB.Enqueue((f, b)), (f, h) => { });
            B = new NetInput(info, 1, (i, f) => Press(f + 1000), (f, b) => toA.Enqueue((f, b)), (f, h) => { });
        }

        public static GamePadState Press(int f) =>
            new GamePadState(Vector2.Zero, Vector2.Zero, 0f, 0f, f % 3 == 0 ? new[] { Buttons.A } : Array.Empty<Buttons>());

        public void Deliver()
        {
            while (toB.Count > 0) { var (f, b) = toB.Dequeue(); foreach (var (s, st) in b) B.Receive(f, s, st); }
            while (toA.Count > 0) { var (f, b) = toA.Dequeue(); foreach (var (s, st) in b) A.Receive(f, s, st); }
        }
    }

    [Fact]
    public void BothSidesSeeIdenticalInputsEveryFrame()
    {
        var p = new Pair(3);
        var sa = new GamePadState[4];
        var sb = new GamePadState[4];
        int steps = 0;
        for (int tick = 0; tick < 500 && steps < 300; tick++)
        {
            bool ca = p.A.CanAdvance(), cb = p.B.CanAdvance();
            p.Deliver();
            if (ca && cb)
            {
                p.A.Latch(sa); p.B.Latch(sb);
                for (int s = 0; s < 4; s++)
                {
                    Assert.Equal(sa[s].IsConnected, sb[s].IsConnected);
                    Assert.Equal(sa[s].Buttons, sb[s].Buttons);
                }
                p.A.AfterFrame(); p.B.AfterFrame();
                steps++;
            }
        }
        Assert.Equal(300, steps);
        Assert.False(sa[2].IsConnected); // unowned slot
    }

    [Fact]
    public void StallsUntilRemoteInputArrives()
    {
        var p = new Pair(2);
        var slots = new GamePadState[4];
        // Frames 0..delay-1 are pre-filled.
        for (int i = 0; i < 2; i++)
        {
            Assert.True(p.A.CanAdvance());
            p.A.Latch(slots);
            p.A.AfterFrame();
        }
        Assert.False(p.A.CanAdvance()); // nothing from B yet
        p.B.CanAdvance();
        p.Deliver();
        Assert.True(p.A.CanAdvance());
    }

    [Fact]
    public void PeerThatLeftNoLongerBlocks()
    {
        var p = new Pair(1);
        var slots = new GamePadState[4];
        Assert.True(p.A.CanAdvance());
        p.A.Latch(slots);
        p.A.AfterFrame();
        Assert.False(p.A.CanAdvance());
        p.A.PeerLeft(1, 1);
        Assert.True(p.A.CanAdvance());
        p.A.Latch(slots);
        Assert.False(slots[1].IsConnected);
    }
}

public class UpdaterTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("1.10.0", "1.9.2", 1)]
    [InlineData("v1.2.0", "1.2", 0)]
    [InlineData("1.2.0-beta", "1.2.0", -1)]
    [InlineData("0.1.0", "0.2.0", -1)]
    public void ComparesVersions(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(Updater.CompareVersions(a, b)));
    }

    [Fact]
    public void AppliesUpdateWithoutTouchingUserData()
    {
        string root = Path.Combine(Path.GetTempPath(), "pmk-upd-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install");
        string staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(Path.Combine(install, "data"));
        Directory.CreateDirectory(Path.Combine(staging, "data"));
        File.WriteAllText(Path.Combine(install, "app.dll"), "old");
        File.WriteAllText(Path.Combine(install, "data", "settings.json"), "mine");
        File.WriteAllText(Path.Combine(staging, "app.dll"), "new");
        File.WriteAllText(Path.Combine(staging, "new.dll"), "added");
        File.WriteAllText(Path.Combine(staging, "data", "settings.json"), "theirs");
        try
        {
            Updater.ApplyInPlace(staging, install);
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app.dll")));
            Assert.Equal("added", File.ReadAllText(Path.Combine(install, "new.dll")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "data", "settings.json")));
            Assert.True(File.Exists(Path.Combine(install, "app.dll.pmk-old")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class FormatTests
{
    [Fact]
    public void XactVolumeCurve()
    {
        Assert.InRange(XactVolume.ByteToDecibels(0xB4), -0.3f, 0.3f);
        Assert.InRange(XactVolume.ByteToDecibels(0xFF), 5.5f, 6.5f);
        Assert.True(XactVolume.ByteToDecibels(0x00) < -90f);
    }

    [Fact]
    public void RejectsNonStfsFiles()
    {
        Assert.Throws<InvalidDataException>(() => new StfsPackage(new byte[0x2000]));
    }

    [Fact]
    public void SnapsXmaSampleRates()
    {
        Assert.Equal(44100, WaveBankData.SnapRate(44101));
        Assert.Equal(44100, WaveBankData.SnapRate(44193));
        Assert.Equal(48000, WaveBankData.SnapRate(47990));
        Assert.Equal(12345, WaveBankData.SnapRate(12345));
    }

    [Fact]
    public void Xma2HeaderIsWellFormed()
    {
        var e = new WaveEntry { Channels = 2, SampleRate = 44100, Duration = 1000, LoopStart = 10, LoopLength = 500 };
        byte[] riff = AudioConverter.BuildXma2Riff(e, new byte[2048]);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(riff, 0, 4));
        Assert.Equal(riff.Length - 8, BitConverter.ToInt32(riff, 4));
        Assert.Equal(0x166, BitConverter.ToUInt16(riff, 20));
        Assert.Equal(2, BitConverter.ToUInt16(riff, 22));
    }

    [Fact]
    public void SettingsClampOutOfRangeValues()
    {
        var s = new Settings { WindowScale = 99, MusicVolume = -3, KeyboardSlot = 9, PlayerName = "   " };
        s.Clamp();
        Assert.Equal(8, s.WindowScale);
        Assert.Equal(0, s.MusicVolume);
        Assert.Equal(3, s.KeyboardSlot);
        Assert.Equal("Player", s.PlayerName);
    }
}

/// <summary>
/// End-to-end checks against a real game package. Skipped unless
/// PMK_GAME_PACKAGE points at your own copy (never committed or used in CI).
/// </summary>
public class GamePackageTests
{
    private static string Package => Environment.GetEnvironmentVariable("PMK_GAME_PACKAGE");

    [Fact]
    public void ExtractsRelinksAndParsesAudio()
    {
        if (string.IsNullOrEmpty(Package) || !File.Exists(Package))
            return; // no package available: nothing to check
        string dir = Path.Combine(Path.GetTempPath(), "pmk-it-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inst = new GameInstallation(dir);
            inst.Extract(Package, null);
            inst.Relink(null); // throws on any unresolved reference
            Assert.True(File.Exists(Path.Combine(inst.BinDir, "AncHell.dll")));

            var bgm = SoundBankData.Load(Path.Combine(inst.GameDir, "Content", "Sound", "bgm", "Sound Bank.xsb"));
            Assert.NotNull(bgm.FindCue("bgm00"));
            Assert.NotNull(bgm.FindCue("Random")?.Variation);
            Assert.Equal(VariationType.Shuffle, bgm.FindCue("Random").Variation.Type);
            Assert.Equal(VariationType.Ordered, bgm.FindCue("Order").Variation.Type);
            Assert.Equal(255, bgm.FindCue("bgm00").Sound.Clips[0].Events[0].LoopCount);
            Assert.Empty(bgm.Warnings);

            var se = SoundBankData.Load(Path.Combine(inst.GameDir, "Content", "Sound", "se", "se.xsb"));
            for (int i = 0; i < 70; i++) Assert.NotNull(se.FindCue($"se{i:D3}"));
            Assert.Empty(se.Warnings);

            var wb = WaveBankData.Load(Path.Combine(inst.GameDir, "Content", "Sound", "bgm", "Wave Bank.xwb"));
            Assert.Equal(12, wb.Entries.Count);
            Assert.All(wb.Entries, e => Assert.Equal(WaveCodec.Xma, e.Codec));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

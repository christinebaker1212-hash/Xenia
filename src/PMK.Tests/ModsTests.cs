using Mono.Cecil;
using Mono.Cecil.Cil;
using PMK.Mods;
using PMK.Patcher;

namespace PMK.Tests;

public class EndlessTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(11, 1)]
    [InlineData(20, 10)]
    [InlineData(21, 1)]
    [InlineData(105, 5)]
    public void RepeatsTheArcadeStagesInOrder(int stage, int baseStage) =>
        Assert.Equal(baseStage, Endless.BaseStage(stage));

    [Theory]
    [InlineData(1, 1f)]
    [InlineData(10, 1f)]
    [InlineData(11, 1.25f)]
    [InlineData(14, 2f)]
    [InlineData(30, 6f)]
    public void AddsMonstersForEveryStagePastTheLast(int stage, float multiplier) =>
        Assert.Equal(multiplier, Endless.MonsterMultiplier(stage), 4);

    [Theory]
    [InlineData(4, 1.25f, false, 5)]
    [InlineData(2, 1.25f, false, 3)]   // 2.5 rounds up
    [InlineData(1, 1.25f, false, 1)]
    [InlineData(1, 2f, false, 2)]
    [InlineData(0, 3f, false, 0)]      // "no spawn on this difficulty" stays
    [InlineData(-1, 3f, false, -1)]    // end-of-stage marker stays
    [InlineData(1, 3.5f, true, 1)]     // the final boss is never duplicated
    public void ScalesWaveSizes(int count, float multiplier, bool finalBoss, int expected) =>
        Assert.Equal(expected, Endless.ScaleCount(count, multiplier, finalBoss));
}

/// <summary>Mods against a real game package; skipped unless PMK_GAME_PACKAGE is set (see GamePackageTests).</summary>
public class ModsInstallTests
{
    private static string Package => Environment.GetEnvironmentVariable("PMK_GAME_PACKAGE");

    [Fact]
    public void InstallsModsAndHooksTheGame()
    {
        if (string.IsNullOrEmpty(Package) || !File.Exists(Package)) return;
        WithInstall(inst =>
        {
            Assert.NotNull(inst.ModsDir);
            inst.Relink(null);
            Assert.True(File.Exists(Path.Combine(inst.BinDir, "PMK.Mods.dll")));
            Assert.False(string.IsNullOrEmpty(inst.Manifest.ModsSha256));
            Assert.False(inst.NeedsRelink);

            using var game = ModuleDefinition.ReadModule(Path.Combine(inst.BinDir, "AncHell.dll"));
            var update = game.GetType("AncHell.GameMode.GameModeSelect").Methods.First(m => m.Name == "Update");
            var firstCall = update.Body.Instructions.First(i => i.OpCode == OpCodes.Call);
            Assert.Equal("PMK.Mods.ModeSelect", ((MethodReference)firstCall.Operand).DeclaringType.FullName);

            using var mods = ModuleDefinition.ReadModule(Path.Combine(inst.BinDir, "PMK.Mods.dll"));
            Assert.Contains(mods.AssemblyReferences, a => a.Name == "AncHell");
            Assert.DoesNotContain(mods.AssemblyReferences, a => a.Name == ModLinker.GameApiAssemblyName);
        });
    }

    [Fact]
    public void RefusesModsThatDoNotMatchTheGame()
    {
        if (string.IsNullOrEmpty(Package) || !File.Exists(Package)) return;
        WithInstall(inst =>
        {
            // A declaration with the wrong enum value must stop the install instead of corrupting the game.
            string bad = Path.Combine(inst.DataDir, "bad-mods");
            Directory.CreateDirectory(bad);
            File.Copy(Path.Combine(inst.ModsDir, "PMK.Mods.dll"), Path.Combine(bad, "PMK.Mods.dll"));
            using (var api = ModuleDefinition.ReadModule(Path.Combine(inst.ModsDir, "PMK.GameApi.dll"), new ReaderParameters { InMemory = true }))
            {
                var kind = api.GetType("AncHell.Main").NestedTypes.First(t => t.Name == "CHAR_KIND");
                kind.Fields.First(f => f.Name == "SYS_GAMEMODE").Constant = 83;
                api.Write(Path.Combine(bad, "PMK.GameApi.dll"));
            }
            inst.ModsDir = bad;
            var ex = Assert.Throws<RelinkException>(() => inst.Relink(null));
            Assert.Contains("SYS_GAMEMODE", ex.Message);
        });
    }

    private static void WithInstall(Action<GameInstallation> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pmk-mods-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inst = new GameInstallation(dir);
            inst.Extract(Package, null);
            test(inst);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

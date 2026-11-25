using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace PMK.Launcher
{
    /// <summary>Loads the relinked game assemblies and creates the game object.</summary>
    public static class GuestLoader
    {
        private static string binDir;

        public static void Init(string relinkedBinDir)
        {
            binDir = relinkedBinDir;
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(binDir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
        }

        public static PMK.Compat.Game CreateGame()
        {
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(binDir, "AncHell.dll"));
            // Make SpxSprite available by name for FNA's content type readers.
            string spx = Path.Combine(binDir, "SpxSprite.dll");
            if (File.Exists(spx)) AssemblyLoadContext.Default.LoadFromAssemblyPath(spx);

            var mainType = asm.GetTypes().First(t => typeof(PMK.Compat.Game).IsAssignableFrom(t) && !t.IsAbstract);
            // Program.Main does: new Main(isDoubleSize: true).
            var ctor = mainType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            object game = ctor != null ? ctor.Invoke(new object[] { true }) : Activator.CreateInstance(mainType, true);
            return (PMK.Compat.Game)game;
        }
    }
}

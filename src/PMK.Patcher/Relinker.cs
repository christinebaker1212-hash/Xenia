using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PMK.Patcher
{
    /// <summary>
    /// Retargets the game's XNA 3.1 / .NET Compact Framework assemblies to
    /// .NET + FNA + PMK.Compat by rewriting metadata references (no source
    /// code involved). Also replaces clock-seeded <c>new Random()</c> calls
    /// so online play stays in sync.
    /// </summary>
    public sealed class Relinker
    {
        /// <summary>Bump when the relinking rules or PMK.Compat's surface change.</summary>
        public const int PatchVersion = 2;

        private const string Fna = "FNA.NET";
        private const string Compat = "PMK.Compat";

        private static readonly string[] XnaAssemblies =
        {
            "Microsoft.Xna.Framework", "Microsoft.Xna.Framework.Game",
            "Microsoft.Xna.Framework.GamerServices", "Microsoft.Xna.Framework.Net",
        };

        /// <summary>XNA 3.1 types that PMK.Compat replaces, by full name.</summary>
        private static readonly Dictionary<string, string> CompatTypes = new Dictionary<string, string>
        {
            ["Microsoft.Xna.Framework.Game"] = "PMK.Compat.Game",
            ["Microsoft.Xna.Framework.GraphicsDeviceManager"] = "PMK.Compat.GraphicsDeviceManager",
            ["Microsoft.Xna.Framework.Graphics.GraphicsDevice"] = "PMK.Compat.Graphics.GraphicsDevice",
            ["Microsoft.Xna.Framework.Graphics.RenderState"] = "PMK.Compat.Graphics.RenderState",
            ["Microsoft.Xna.Framework.Graphics.RenderTarget2D"] = "PMK.Compat.Graphics.RenderTarget2D",
            ["Microsoft.Xna.Framework.Graphics.SamplerState"] = "PMK.Compat.Graphics.SamplerState",
            ["Microsoft.Xna.Framework.Graphics.SamplerStateCollection"] = "PMK.Compat.Graphics.SamplerStateCollection",
            ["Microsoft.Xna.Framework.Graphics.SpriteBatch"] = "PMK.Compat.Graphics.SpriteBatch",
            ["Microsoft.Xna.Framework.Graphics.SpriteBlendMode"] = "PMK.Compat.Graphics.SpriteBlendMode",
            ["Microsoft.Xna.Framework.Graphics.SaveStateMode"] = "PMK.Compat.Graphics.SaveStateMode",
            ["Microsoft.Xna.Framework.Graphics.ShaderProfile"] = "PMK.Compat.Graphics.ShaderProfile",
            ["Microsoft.Xna.Framework.Graphics.SurfaceFormat"] = "PMK.Compat.Graphics.SurfaceFormat",
            ["Microsoft.Xna.Framework.Graphics.TextureFilter"] = "PMK.Compat.Graphics.TextureFilter",
            ["Microsoft.Xna.Framework.Graphics.CullMode"] = "PMK.Compat.Graphics.CullMode",
            ["Microsoft.Xna.Framework.Content.ContentManager"] = "PMK.Compat.Content.ContentManager",
            ["Microsoft.Xna.Framework.Audio.AudioEngine"] = "PMK.Compat.Audio.AudioEngine",
            ["Microsoft.Xna.Framework.Audio.AudioCategory"] = "PMK.Compat.Audio.AudioCategory",
            ["Microsoft.Xna.Framework.Audio.AudioStopOptions"] = "PMK.Compat.Audio.AudioStopOptions",
            ["Microsoft.Xna.Framework.Audio.SoundBank"] = "PMK.Compat.Audio.SoundBank",
            ["Microsoft.Xna.Framework.Audio.WaveBank"] = "PMK.Compat.Audio.WaveBank",
            ["Microsoft.Xna.Framework.Audio.Cue"] = "PMK.Compat.Audio.Cue",
            ["Microsoft.Xna.Framework.GamerServices.Gamer"] = "PMK.Compat.GamerServices.Gamer",
            ["Microsoft.Xna.Framework.GamerServices.GamerPresence"] = "PMK.Compat.GamerServices.GamerPresence",
            ["Microsoft.Xna.Framework.GamerServices.GamerPresenceMode"] = "PMK.Compat.GamerServices.GamerPresenceMode",
            ["Microsoft.Xna.Framework.GamerServices.GamerServicesComponent"] = "PMK.Compat.GamerServices.GamerServicesComponent",
            ["Microsoft.Xna.Framework.GamerServices.Guide"] = "PMK.Compat.GamerServices.Guide",
            ["Microsoft.Xna.Framework.GamerServices.SignedInGamer"] = "PMK.Compat.GamerServices.SignedInGamer",
            ["Microsoft.Xna.Framework.GamerServices.SignedInGamerCollection"] = "PMK.Compat.GamerServices.SignedInGamerCollection",
            ["Microsoft.Xna.Framework.Input.GamePad"] = "PMK.Compat.Input.GamePad",
            ["Microsoft.Xna.Framework.Input.Keyboard"] = "PMK.Compat.Input.Keyboard",
        };

        /// <summary>Types that moved namespace between XNA 3.1 and 4.0.</summary>
        private static readonly Dictionary<string, string> MovedFnaTypes = new Dictionary<string, string>
        {
            ["Microsoft.Xna.Framework.Graphics.Color"] = "Microsoft.Xna.Framework.Color",
        };

        private readonly List<string> searchDirs = new List<string>();
        public List<string> Log { get; } = new List<string>();

        /// <summary>Folder holding PMK.Mods.dll and PMK.GameApi.dll; null installs the game without mods.</summary>
        public string ModsDir { get; set; }

        public Relinker(params string[] extraSearchDirs)
        {
            searchDirs.Add(Path.GetDirectoryName(typeof(PMK.Compat.Game).Assembly.Location));
            searchDirs.Add(Path.GetDirectoryName(typeof(Microsoft.Xna.Framework.Vector2).Assembly.Location));
            searchDirs.Add(Path.GetDirectoryName(typeof(object).Assembly.Location));
            searchDirs.AddRange(extraSearchDirs);
        }

        /// <summary>Relinks <paramref name="inputs"/> (game assemblies) into <paramref name="outDir"/> as .dll files.</summary>
        public void Run(IEnumerable<string> inputs, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var resolver = new DefaultAssemblyResolver();
            foreach (var d in searchDirs.Distinct()) resolver.AddSearchDirectory(d);
            resolver.AddSearchDirectory(outDir);

            var inputList = inputs.ToList();
            // Relink dependencies first so that later modules resolve against relinked copies.
            inputList.Sort((a, b) => Score(a).CompareTo(Score(b)));
            foreach (string input in inputList)
            {
                var module = ModuleDefinition.ReadModule(input, new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    ReadingMode = ReadingMode.Immediate,
                    InMemory = true,
                });
                RelinkModule(module);
                if (ModsDir != null && module.GetType("AncHell", "Main") != null)
                {
                    var mods = new ModLinker(resolver);
                    mods.Apply(module, ModsDir, outDir);
                    Log.AddRange(mods.Log);
                }
                string outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".dll");
                module.Write(outPath);
                Log.Add($"Relinked {Path.GetFileName(input)} -> {Path.GetFileName(outPath)}");
            }
        }

        private static int Score(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        private void RelinkModule(ModuleDefinition module)
        {
            var fnaRef = AddReference(module, typeof(Microsoft.Xna.Framework.Vector2).Assembly.GetName());
            var compatRef = AddReference(module, typeof(PMK.Compat.Game).Assembly.GetName());

            // 1. Compact Framework BCL references -> desktop facades (.NET forwards them).
            foreach (var ar in module.AssemblyReferences)
            {
                if (ar.Name == "mscorlib" || ar.Name == "System" || ar.Name == "System.Core" || ar.Name == "System.Xml")
                {
                    ar.Version = new Version(4, 0, 0, 0);
                    ar.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                }
            }

            // 2. XNA type references -> FNA or PMK.Compat.
            foreach (var tr in module.GetTypeReferences())
            {
                if (!(tr.Scope is AssemblyNameReference scope) || !XnaAssemblies.Contains(scope.Name))
                    continue;
                string full = tr.FullName;
                if (CompatTypes.TryGetValue(full, out string target))
                {
                    Retarget(tr, target, compatRef);
                }
                else if (MovedFnaTypes.TryGetValue(full, out target))
                {
                    Retarget(tr, target, fnaRef);
                }
                else
                {
                    tr.Scope = fnaRef;
                }
            }

            // 3. Member signature fixes for APIs FNA spells differently.
            foreach (var mr in module.GetMemberReferences().OfType<MethodReference>())
            {
                // XNA 3.1 Color(byte, byte, byte, byte) -> FNA Color(int, int, int, int).
                // Bytes are int32 on the IL stack, so only the signature changes.
                if (mr.Name == ".ctor" && mr.DeclaringType.FullName == "Microsoft.Xna.Framework.Color"
                    && mr.Parameters.Count >= 3 && mr.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.Byte))
                {
                    foreach (var p in mr.Parameters) p.ParameterType = module.TypeSystem.Int32;
                }
            }

            // 4. Deterministic Random for online play.
            var detCreate0 = module.ImportReference(typeof(PMK.Compat.DetRandom).GetMethod("Create", Type.EmptyTypes));
            var detCreate1 = module.ImportReference(typeof(PMK.Compat.DetRandom).GetMethod("Create", new[] { typeof(int) }));
            int randomSites = 0;
            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Newobj || !(ins.Operand is MethodReference ctor)) continue;
                        if (ctor.DeclaringType.FullName != "System.Random") continue;
                        if (ctor.Parameters.Count == 0) { ins.OpCode = OpCodes.Call; ins.Operand = detCreate0; randomSites++; }
                        else if (ctor.Parameters.Count == 1) { ins.OpCode = OpCodes.Call; ins.Operand = detCreate1; randomSites++; }
                    }
                }
            }
            Log.Add($"{module.Name}: {randomSites} Random constructions made deterministic");

            // 5. Drop XNA references and modernise the module header.
            foreach (var ar in module.AssemblyReferences.Where(a => XnaAssemblies.Contains(a.Name)).ToList())
                module.AssemblyReferences.Remove(ar);
            module.RuntimeVersion = "v4.0.30319";
            module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit | ModuleAttributes.StrongNameSigned);
            module.Attributes |= ModuleAttributes.ILOnly;
            module.Architecture = TargetArchitecture.I386;
            if (module.Kind != ModuleKind.Dll)
            {
                module.Kind = ModuleKind.Dll;
                module.EntryPoint = null;
            }

            // 6. Verify every external reference resolves, and point inherited
            //    members at the type that really declares them.
            Validate(module);
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var t in module.Types)
            {
                yield return t;
                foreach (var n in Nested(t)) yield return n;
            }
        }

        private static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes)
            {
                yield return n;
                foreach (var m in Nested(n)) yield return m;
            }
        }

        private static void Retarget(TypeReference tr, string fullName, AssemblyNameReference scope)
        {
            int dot = fullName.LastIndexOf('.');
            tr.Namespace = fullName.Substring(0, dot);
            tr.Name = fullName.Substring(dot + 1);
            tr.Scope = scope;
        }

        private static AssemblyNameReference AddReference(ModuleDefinition module, System.Reflection.AssemblyName name)
        {
            var existing = module.AssemblyReferences.FirstOrDefault(a => a.Name == name.Name);
            if (existing != null) return existing;
            var r = new AssemblyNameReference(name.Name, name.Version) { PublicKeyToken = name.GetPublicKeyToken() ?? Array.Empty<byte>() };
            module.AssemblyReferences.Add(r);
            return r;
        }

        private void Validate(ModuleDefinition module)
        {
            var errors = new List<string>();
            foreach (var tr in module.GetTypeReferences())
            {
                if (tr.Scope is ModuleDefinition) continue;
                TypeDefinition td;
                try { td = tr.Resolve(); } catch (AssemblyResolutionException ex) { errors.Add($"type {tr.FullName}: {ex.Message}"); continue; }
                if (td == null) { errors.Add("type " + tr.FullName + " in " + tr.Scope.Name); continue; }
                if (td.IsValueType != tr.IsValueType)
                    tr.IsValueType = td.IsValueType;
            }
            foreach (var mr in module.GetMemberReferences())
            {
                // Multi-dimensional array accessors (Get/Set/Address/.ctor) are supplied by the runtime.
                if (mr.DeclaringType is ArrayType)
                    continue;
                IMemberDefinition def;
                try { def = mr.Resolve(); } catch (AssemblyResolutionException ex) { errors.Add($"{mr.FullName}: {ex.Message}"); continue; }
                if (def == null)
                {
                    errors.Add(mr.FullName);
                    continue;
                }
                // MemberRef to an inherited member: re-point at the declaring type.
                var declaring = mr.DeclaringType;
                if (!(declaring is GenericInstanceType) && !(declaring is ArrayType)
                    && def.DeclaringType.FullName != declaring.FullName)
                {
                    mr.DeclaringType = module.ImportReference(def.DeclaringType);
                }
            }
            if (errors.Count > 0)
                throw new RelinkException("Unresolved references after relinking:\n  " + string.Join("\n  ", errors.Distinct()));
        }
    }

    public sealed class RelinkException : Exception
    {
        public RelinkException(string message) : base(message) { }
    }
}

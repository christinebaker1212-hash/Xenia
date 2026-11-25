using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace PMK.Patcher
{
    /// <summary>
    /// Installs PMK.Mods into the relinked game:
    /// <list type="number">
    /// <item>retargets PMK.Mods from PMK.GameApi (declarations only) to the real game assembly,</item>
    /// <item>checks every game type, member and enum value PMK.Mods uses against the real game,</item>
    /// <item>makes those game members public so PMK.Mods may use them,</item>
    /// <item>splices calls to PMK.Mods' [Hook] methods into the game methods they name.</item>
    /// </list>
    /// Like the rest of the relinker this works on the user's own copy at
    /// install time; no game code is stored anywhere else.
    /// </summary>
    public sealed class ModLinker
    {
        public const string ModsAssemblyName = "PMK.Mods";
        public const string GameApiAssemblyName = "PMK.GameApi";
        private const string HookAttribute = "PMK.Mods.HookAttribute";
        private const int PrefixKind = 0, PostfixKind = 1;

        private readonly IAssemblyResolver resolver;
        private readonly List<string> errors = new List<string>();
        public List<string> Log { get; } = new List<string>();

        public ModLinker(IAssemblyResolver resolver)
        {
            this.resolver = resolver;
        }

        /// <summary>
        /// Applies the mods in <paramref name="modsDir"/> to <paramref name="game"/> (already
        /// relinked, not yet written) and writes the retargeted PMK.Mods.dll to <paramref name="outDir"/>.
        /// </summary>
        public void Apply(ModuleDefinition game, string modsDir, string outDir)
        {
            string modsPath = Path.Combine(modsDir, ModsAssemblyName + ".dll");
            string apiPath = Path.Combine(modsDir, GameApiAssemblyName + ".dll");
            var readerParams = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
            var mods = ModuleDefinition.ReadModule(modsPath, readerParams);
            var api = ModuleDefinition.ReadModule(apiPath, readerParams);

            var gameRef = mods.AssemblyReferences.FirstOrDefault(a => a.Name == GameApiAssemblyName)
                ?? throw new RelinkException($"{ModsAssemblyName} does not reference {GameApiAssemblyName}.");
            gameRef.Name = game.Assembly.Name.Name;
            gameRef.Version = game.Assembly.Name.Version;
            gameRef.PublicKeyToken = Array.Empty<byte>();
            gameRef.Culture = string.Empty;

            CheckEnums(game, api);
            BindGameReferences(game, mods, gameRef);
            ValidateOtherReferences(mods, gameRef);
            int hooks = InjectHooks(game, mods);
            if (errors.Count > 0)
                throw new RelinkException($"{ModsAssemblyName} does not match this game:\n  " + string.Join("\n  ", errors.Distinct()));

            mods.Write(Path.Combine(outDir, ModsAssemblyName + ".dll"));
            Log.Add($"{ModsAssemblyName}: {hooks} hooks installed");
        }

        // ---- 1. Declarations must match the game -------------------------------------------

        private void CheckEnums(ModuleDefinition game, ModuleDefinition api)
        {
            foreach (var declared in AllTypes(api).Where(t => t.IsEnum))
            {
                var real = game.GetType(declared.FullName);
                if (real == null || !real.IsEnum) { errors.Add("enum " + declared.FullName + " not found"); continue; }
                foreach (var f in declared.Fields.Where(f => f.IsLiteral))
                {
                    var rf = real.Fields.FirstOrDefault(x => x.Name == f.Name && x.IsLiteral);
                    if (rf == null) errors.Add($"enum value {declared.FullName}.{f.Name} not found");
                    else if (!Equals(Convert.ToInt64(rf.Constant), Convert.ToInt64(f.Constant)))
                        errors.Add($"enum value {declared.FullName}.{f.Name} is {rf.Constant} in the game, {f.Constant} in {GameApiAssemblyName}");
                }
            }
        }

        private void BindGameReferences(ModuleDefinition game, ModuleDefinition mods, AssemblyNameReference gameRef)
        {
            foreach (var tr in mods.GetTypeReferences())
            {
                if (!IsGame(tr, gameRef)) continue;
                var td = FindType(game, tr);
                if (td == null) { errors.Add("type " + tr.FullName + " not found"); continue; }
                if (td.IsValueType != tr.IsValueType) tr.IsValueType = td.IsValueType;
                MakePublic(td);
            }
            foreach (var mr in mods.GetMemberReferences())
            {
                if (!IsGame(mr.DeclaringType, gameRef)) continue;
                var td = FindType(game, mr.DeclaringType);
                if (td == null)
                {
                    if (mr.DeclaringType is TypeSpecification) errors.Add("unsupported use of a generic game type: " + mr.FullName);
                    continue; // otherwise reported above
                }
                switch (mr)
                {
                    case FieldReference fr:
                    {
                        var fd = td.Fields.FirstOrDefault(f => f.Name == fr.Name);
                        if (fd == null) errors.Add("field " + fr.FullName + " not found");
                        else if (fd.FieldType.FullName != fr.FieldType.FullName)
                            errors.Add($"field {fr.FullName} is {fd.FieldType.FullName} in the game");
                        else fd.IsPublic = true;
                        break;
                    }
                    case MethodReference mref:
                    {
                        var md = td.Methods.FirstOrDefault(m => m.Name == mref.Name && SameSignature(m, mref));
                        if (md == null) errors.Add("method " + mref.FullName + " not found");
                        else md.IsPublic = true;
                        break;
                    }
                }
            }
        }

        private void ValidateOtherReferences(ModuleDefinition mods, AssemblyNameReference gameRef)
        {
            foreach (var mr in mods.GetMemberReferences())
            {
                if (IsGame(mr.DeclaringType, gameRef) || mr.DeclaringType is ArrayType) continue;
                try
                {
                    if (mr.Resolve() == null) errors.Add(mr.FullName + " not found");
                }
                catch (AssemblyResolutionException ex)
                {
                    errors.Add($"{mr.FullName}: {ex.Message}");
                }
            }
        }

        private static bool IsGame(TypeReference tr, AssemblyNameReference gameRef)
        {
            while (tr is TypeSpecification spec) tr = spec.ElementType;
            while (tr?.DeclaringType != null) tr = tr.DeclaringType;
            return tr != null && tr.Scope is AssemblyNameReference a && a == gameRef;
        }

        private static TypeDefinition FindType(ModuleDefinition game, TypeReference tr)
        {
            if (tr is TypeSpecification) return null;
            if (tr.DeclaringType != null)
                return FindType(game, tr.DeclaringType)?.NestedTypes.FirstOrDefault(n => n.Name == tr.Name);
            return game.GetType(tr.Namespace, tr.Name);
        }

        private static bool SameSignature(MethodReference a, MethodReference b) =>
            a.HasThis == b.HasThis
            && a.ReturnType.FullName == b.ReturnType.FullName
            && a.Parameters.Count == b.Parameters.Count
            && a.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(b.Parameters.Select(p => p.ParameterType.FullName));

        private static void MakePublic(TypeDefinition td)
        {
            for (var t = td; t != null; t = t.DeclaringType)
            {
                if (t.IsNested) t.IsNestedPublic = true;
                else t.IsPublic = true;
            }
        }

        // ---- 2. Hooks ---------------------------------------------------------------------

        private int InjectHooks(ModuleDefinition game, ModuleDefinition mods)
        {
            var modsRef = game.AssemblyReferences.FirstOrDefault(a => a.Name == ModsAssemblyName);
            if (modsRef == null)
            {
                modsRef = new AssemblyNameReference(ModsAssemblyName, mods.Assembly.Name.Version);
                game.AssemblyReferences.Add(modsRef);
            }

            int count = 0;
            foreach (var hook in AllTypes(mods).SelectMany(t => t.Methods))
            {
                foreach (var attr in hook.CustomAttributes.Where(a => a.AttributeType.FullName == HookAttribute))
                {
                    string typeName = (string)attr.ConstructorArguments[0].Value;
                    string methodName = (string)attr.ConstructorArguments[1].Value;
                    int kind = attr.ConstructorArguments.Count > 2 ? Convert.ToInt32(attr.ConstructorArguments[2].Value) : PrefixKind;
                    string where = $"hook {hook.DeclaringType.Name}.{hook.Name} -> {typeName}.{methodName}";

                    if (!hook.IsStatic || hook.DeclaringType.IsNested || hook.HasGenericParameters)
                    {
                        errors.Add(where + ": hooks must be static methods of top-level types");
                        continue;
                    }
                    var type = game.GetType(typeName);
                    var targets = type?.Methods.Where(m => m.Name == methodName && m.HasBody && Accepts(hook, m)).ToList();
                    if (targets == null || targets.Count != 1)
                    {
                        errors.Add(where + (targets == null ? ": type not found" : $": {targets.Count} matching methods"));
                        continue;
                    }
                    var target = targets[0];
                    string ret = hook.ReturnType.FullName;
                    if (target.ReturnType.FullName != "System.Void"
                        || (kind == PrefixKind && ret != "System.Void" && ret != "System.Boolean")
                        || (kind == PostfixKind && ret != "System.Void"))
                    {
                        errors.Add(where + ": the game method must return void; prefixes return void or bool, postfixes void");
                        continue;
                    }

                    var call = HookReference(game, modsRef, hook, target);
                    if (kind == PrefixKind) InjectPrefix(target, call, ret == "System.Boolean");
                    else InjectPostfix(target, call);
                    count++;
                }
            }
            return count;
        }

        /// <summary>Hook parameters: (instance?, game method parameters...), compared by type name.</summary>
        private static bool Accepts(MethodDefinition hook, MethodDefinition target)
        {
            var expected = new List<string>();
            if (target.HasThis) expected.Add(target.DeclaringType.FullName);
            expected.AddRange(target.Parameters.Select(p => p.ParameterType.FullName));
            return hook.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(expected);
        }

        /// <summary>A reference to the hook, built from the game's own types so nothing points back at the game assembly.</summary>
        private static MethodReference HookReference(ModuleDefinition game, AssemblyNameReference modsRef, MethodDefinition hook, MethodDefinition target)
        {
            var declaring = new TypeReference(hook.DeclaringType.Namespace, hook.DeclaringType.Name, game, modsRef);
            var returnType = hook.ReturnType.FullName == "System.Boolean" ? game.TypeSystem.Boolean : game.TypeSystem.Void;
            var reference = new MethodReference(hook.Name, returnType, declaring) { HasThis = false };
            if (target.HasThis) reference.Parameters.Add(new ParameterDefinition(target.DeclaringType));
            foreach (var p in target.Parameters) reference.Parameters.Add(new ParameterDefinition(p.ParameterType));
            return reference;
        }

        private static List<Instruction> CallWithArguments(MethodDefinition target, MethodReference call)
        {
            var seq = new List<Instruction>();
            if (target.HasThis) seq.Add(Instruction.Create(OpCodes.Ldarg, target.Body.ThisParameter));
            foreach (var p in target.Parameters) seq.Add(Instruction.Create(OpCodes.Ldarg, p));
            seq.Add(Instruction.Create(OpCodes.Call, call));
            return seq;
        }

        private static void InjectPrefix(MethodDefinition target, MethodReference call, bool canSkip)
        {
            var body = target.Body;
            body.SimplifyMacros();
            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            var seq = CallWithArguments(target, call);
            if (canSkip)
            {
                seq.Add(Instruction.Create(OpCodes.Brtrue, first));
                seq.Add(Instruction.Create(OpCodes.Ret));
            }
            foreach (var ins in seq) il.InsertBefore(first, ins);
            body.OptimizeMacros();
        }

        private static void InjectPostfix(MethodDefinition target, MethodReference call)
        {
            var body = target.Body;
            body.SimplifyMacros();
            var il = body.GetILProcessor();
            foreach (var ret in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
            {
                // Turn the ret itself into the first instruction of the call, so
                // branches that jumped to the ret now run the postfix too.
                var seq = CallWithArguments(target, call);
                seq.Add(Instruction.Create(OpCodes.Ret));
                ret.OpCode = seq[0].OpCode;
                ret.Operand = seq[0].Operand;
                var previous = ret;
                foreach (var ins in seq.Skip(1))
                {
                    il.InsertAfter(previous, ins);
                    previous = ins;
                }
            }
            body.OptimizeMacros();
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                yield return t;
                foreach (var n in t.NestedTypes) stack.Push(n);
            }
        }
    }
}

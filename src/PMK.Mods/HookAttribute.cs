using System;

namespace PMK.Mods
{
    public enum HookKind
    {
        /// <summary>
        /// Runs before the game method. A hook returning <c>bool</c> skips the
        /// game method when it returns false; a <c>void</c> hook never skips.
        /// </summary>
        Prefix = 0,

        /// <summary>Runs after the game method, before each return.</summary>
        Postfix = 1,
    }

    /// <summary>
    /// Attaches a static method to a game method. The hook takes the same
    /// parameters as the game method (with the instance first for instance
    /// methods). Only methods returning void can be hooked. ModLinker reads
    /// these attributes at install time; nothing scans for them at run time.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HookAttribute : Attribute
    {
        public HookAttribute(string type, string method, HookKind kind = HookKind.Prefix)
        {
            Type = type;
            Method = method;
            Kind = kind;
        }

        /// <summary>Full name of the game type, e.g. "AncHell.GameMode.GameModeSelect".</summary>
        public string Type { get; }
        public string Method { get; }
        public HookKind Kind { get; }
    }
}

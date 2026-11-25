using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Xna.Framework;

namespace PMK.Compat.GamerServices
{
    // Xbox LIVE features have no PC equivalent. These stubs report a full
    // (non-trial) game with nobody signed in, which the game handles fine:
    // sign-in and Marketplace are only reached from trial-mode screens.

    public class GamerServicesComponent : IGameComponent
    {
        public GamerServicesComponent(Game game) { }
        public void Initialize() { }
    }

    public static class Guide
    {
        public static bool IsTrialMode => false;
        public static bool SimulateTrialMode { get; set; }

        /// <summary>The Xbox Guide overlay never opens on PC.</summary>
        public static bool IsVisible => false;

        public static void ShowMarketplace(PlayerIndex player) { }
        public static void ShowSignIn(int paneCount, bool onlineOnly) { }
    }

    public abstract class Gamer
    {
        private static readonly SignedInGamerCollection signedIn = new SignedInGamerCollection(new List<SignedInGamer>());
        public static SignedInGamerCollection SignedInGamers => signedIn;
        public string Gamertag { get; protected set; } = "Player";
    }

    public class SignedInGamer : Gamer
    {
        public PlayerIndex PlayerIndex { get; internal set; }
        public GamerPresence Presence { get; } = new GamerPresence();
    }

    public class SignedInGamerCollection : ReadOnlyCollection<SignedInGamer>
    {
        public SignedInGamerCollection(IList<SignedInGamer> list) : base(list) { }
    }

    public class GamerPresence
    {
        public GamerPresenceMode PresenceMode { get; set; }
        public int PresenceValue { get; set; }
    }

    /// <summary>Values are passed as raw integers by the game; names are not needed.</summary>
    public enum GamerPresenceMode
    {
        None = 0,
    }
}

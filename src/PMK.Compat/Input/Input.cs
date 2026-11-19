using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PMK.Compat.Input
{
    /// <summary>
    /// Supplies the four player slots for one game frame. The launcher swaps
    /// in a local provider (controllers + keyboard) or an online provider
    /// (inputs synchronised between machines).
    /// </summary>
    public interface IInputProvider
    {
        /// <summary>Fills the four slots for the frame about to run.</summary>
        void Latch(GamePadState[] slots);

        /// <summary>Rumble request from the game for a slot.</summary>
        void SetVibration(int slot, float left, float right);
    }

    /// <summary>Per-frame input seen by the game.</summary>
    public static class InputRouter
    {
        private static readonly GamePadState[] slots = new GamePadState[4];
        public static IInputProvider Provider { get; set; }

        /// <summary>Called by the launcher once before each game Update.</summary>
        public static void LatchFrame()
        {
            if (Provider == null || CompatHost.InputSuspended)
            {
                for (int i = 0; i < 4; i++) slots[i] = Idle(i);
                return;
            }
            Provider.Latch(slots);
        }

        /// <summary>A connected pad with nothing pressed (or disconnected if it was).</summary>
        private static GamePadState Idle(int i) =>
            slots[i].IsConnected ? new GamePadState(default(GamePadThumbSticks), default(GamePadTriggers), default(GamePadButtons), default(GamePadDPad)) : default;

        public static GamePadState Get(int slot) => slot >= 0 && slot < 4 ? slots[slot] : default;

        public static void SetVibration(int slot, float left, float right) => Provider?.SetVibration(slot, left, right);
    }

    /// <summary>XNA 3.1 GamePad as seen by the game: reads the latched frame.</summary>
    public static class GamePad
    {
        public static GamePadState GetState(PlayerIndex playerIndex) => InputRouter.Get((int)playerIndex);

        public static bool SetVibration(PlayerIndex playerIndex, float leftMotor, float rightMotor)
        {
            InputRouter.SetVibration((int)playerIndex, leftMotor, rightMotor);
            return true;
        }
    }

    /// <summary>
    /// The game only reads the keyboard for a debug mapping on player 4.
    /// PC keyboard play is delivered through the pad slots instead, so the
    /// game always sees an idle keyboard.
    /// </summary>
    public static class Keyboard
    {
        public static KeyboardState GetState() => default;
    }
}

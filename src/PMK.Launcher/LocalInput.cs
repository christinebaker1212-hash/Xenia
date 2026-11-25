using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using PMK.Compat.Input;

namespace PMK.Launcher
{
    /// <summary>
    /// Local controllers plus the keyboard. Controller N drives player N; the
    /// keyboard is merged into the slot chosen in the settings (player 1 by
    /// default), so a keyboard player and a controller player can share it.
    /// </summary>
    public sealed class LocalInput : IInputProvider
    {
        private readonly Settings settings;

        /// <summary>Optional scripted input for automated tests (slot 0).</summary>
        public Func<int, GamePadState?> Script { get; set; }
        public int Frame { get; private set; }

        public LocalInput(Settings settings)
        {
            this.settings = settings;
        }

        public void Latch(GamePadState[] slots)
        {
            for (int i = 0; i < 4; i++)
                slots[i] = ReadSlot(i);
            if (Script != null)
            {
                var s = Script(Frame);
                if (s.HasValue) slots[0] = s.Value;
            }
            Frame++;
        }

        /// <summary>Reads one local slot (used by the online provider too).</summary>
        public GamePadState ReadSlot(int slot)
        {
            var pad = Microsoft.Xna.Framework.Input.GamePad.GetState((PlayerIndex)slot);
            if (settings.KeyboardSlot != slot)
                return pad;
            var kb = Microsoft.Xna.Framework.Input.Keyboard.GetState();
            return Merge(pad, FromKeyboard(kb));
        }

        public GamePadState FromKeyboard(KeyboardState kb)
        {
            var buttons = new List<Buttons>();
            void Map(string action, Buttons b)
            {
                if (kb.IsKeyDown(settings.Binding(action))) buttons.Add(b);
            }
            Map("Up", Buttons.DPadUp);
            Map("Down", Buttons.DPadDown);
            Map("Left", Buttons.DPadLeft);
            Map("Right", Buttons.DPadRight);
            Map("A", Buttons.A);
            Map("B", Buttons.B);
            Map("X", Buttons.X);
            Map("Y", Buttons.Y);
            Map("Start", Buttons.Start);
            Map("Back", Buttons.Back);
            Map("LB", Buttons.LeftShoulder);
            Map("RB", Buttons.RightShoulder);
            float lt = kb.IsKeyDown(settings.Binding("LT")) ? 1f : 0f;
            float rt = kb.IsKeyDown(settings.Binding("RT")) ? 1f : 0f;
            return new GamePadState(Vector2.Zero, Vector2.Zero, lt, rt, buttons.ToArray());
        }

        public static GamePadState Merge(GamePadState pad, GamePadState kb)
        {
            if (!pad.IsConnected)
                return kb;
            var buttons = new List<Buttons>();
            foreach (Buttons b in AllButtons)
                if (pad.IsButtonDown(b) || kb.IsButtonDown(b)) buttons.Add(b);
            return new GamePadState(pad.ThumbSticks.Left, pad.ThumbSticks.Right,
                Math.Max(pad.Triggers.Left, kb.Triggers.Left), Math.Max(pad.Triggers.Right, kb.Triggers.Right),
                buttons.ToArray());
        }

        public static readonly Buttons[] AllButtons =
        {
            Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
            Buttons.Start, Buttons.Back, Buttons.LeftStick, Buttons.RightStick,
            Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.BigButton,
            Buttons.A, Buttons.B, Buttons.X, Buttons.Y,
        };

        public void SetVibration(int slot, float left, float right)
        {
            if (!settings.Vibration) { left = 0; right = 0; }
            Microsoft.Xna.Framework.Input.GamePad.SetVibration((PlayerIndex)slot, left, right);
        }
    }
}

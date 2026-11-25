using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace PMK.Launcher.UI
{
    public enum MenuKey { None, Up, Down, Left, Right, Accept, Back }

    /// <summary>Edge-triggered menu navigation from the keyboard and every controller, with key repeat.</summary>
    public sealed class MenuInput
    {
        private KeyboardState prevKb;
        private readonly GamePadState[] prevPads = new GamePadState[4];
        private MenuKey held;
        private int heldFrames;

        public KeyboardState Keyboard { get; private set; }
        public KeyboardState PreviousKeyboard => prevKb;

        public MenuKey Poll()
        {
            var kb = Microsoft.Xna.Framework.Input.Keyboard.GetState();
            Keyboard = kb;
            MenuKey pressed = MenuKey.None;
            MenuKey down = MenuKey.None;

            void Check(bool now, bool before, MenuKey key)
            {
                if (now) down = down == MenuKey.None ? key : down;
                if (now && !before && pressed == MenuKey.None) pressed = key;
            }

            Check(kb.IsKeyDown(Keys.Up), prevKb.IsKeyDown(Keys.Up), MenuKey.Up);
            Check(kb.IsKeyDown(Keys.Down), prevKb.IsKeyDown(Keys.Down), MenuKey.Down);
            Check(kb.IsKeyDown(Keys.Left), prevKb.IsKeyDown(Keys.Left), MenuKey.Left);
            Check(kb.IsKeyDown(Keys.Right), prevKb.IsKeyDown(Keys.Right), MenuKey.Right);
            Check(kb.IsKeyDown(Keys.Enter) || kb.IsKeyDown(Keys.Z), prevKb.IsKeyDown(Keys.Enter) || prevKb.IsKeyDown(Keys.Z), MenuKey.Accept);
            Check(kb.IsKeyDown(Keys.Escape) || kb.IsKeyDown(Keys.X), prevKb.IsKeyDown(Keys.Escape) || prevKb.IsKeyDown(Keys.X), MenuKey.Back);

            for (int i = 0; i < 4; i++)
            {
                var p = Microsoft.Xna.Framework.Input.GamePad.GetState((PlayerIndex)i);
                var q = prevPads[i];
                Check(Up(p), Up(q), MenuKey.Up);
                Check(Down(p), Down(q), MenuKey.Down);
                Check(Left(p), Left(q), MenuKey.Left);
                Check(Right(p), Right(q), MenuKey.Right);
                Check(p.IsButtonDown(Buttons.A) || p.IsButtonDown(Buttons.Start), q.IsButtonDown(Buttons.A) || q.IsButtonDown(Buttons.Start), MenuKey.Accept);
                Check(p.IsButtonDown(Buttons.B), q.IsButtonDown(Buttons.B), MenuKey.Back);
                prevPads[i] = p;
            }
            prevKb = kb;

            // Repeat for held directions.
            bool isDir = down == MenuKey.Up || down == MenuKey.Down || down == MenuKey.Left || down == MenuKey.Right;
            if (isDir && down == held)
            {
                heldFrames++;
                if (pressed == MenuKey.None && heldFrames > 24 && heldFrames % 5 == 0)
                    pressed = held;
            }
            else
            {
                held = isDir ? down : MenuKey.None;
                heldFrames = 0;
            }
            return pressed;
        }

        /// <summary>Discards the current state so a held button does not trigger the next screen.</summary>
        public void Swallow()
        {
            prevKb = Microsoft.Xna.Framework.Input.Keyboard.GetState();
            for (int i = 0; i < 4; i++) prevPads[i] = Microsoft.Xna.Framework.Input.GamePad.GetState((PlayerIndex)i);
        }

        public bool KeyPressed(Keys k) => Keyboard.IsKeyDown(k) && !prevKbBefore.IsKeyDown(k);
        private KeyboardState prevKbBefore;
        public void BeginFrame() => prevKbBefore = prevKb;

        private static bool Up(GamePadState s) => s.DPad.Up == ButtonState.Pressed || s.ThumbSticks.Left.Y > 0.6f;
        private static bool Down(GamePadState s) => s.DPad.Down == ButtonState.Pressed || s.ThumbSticks.Left.Y < -0.6f;
        private static bool Left(GamePadState s) => s.DPad.Left == ButtonState.Pressed || s.ThumbSticks.Left.X < -0.6f;
        private static bool Right(GamePadState s) => s.DPad.Right == ButtonState.Pressed || s.ThumbSticks.Left.X > 0.6f;
    }
}

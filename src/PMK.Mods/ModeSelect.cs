using Microsoft.Xna.Framework;
using AncHell;
using AncHell.GameSystem;
using AncHell.System;

namespace PMK.Mods
{
    /// <summary>
    /// Replaces the game's mode select (ARCADE, SURVIVE) with one that also
    /// offers ENDLESS and REMIX. ARCADE and SURVIVE keep the game's own
    /// sprites and behaviour; the new entries are drawn in matching lettering.
    /// REMIX is shown greyed out and the cursor skips it until it exists.
    /// </summary>
    public static class ModeSelect
    {
        public enum Mode { Arcade, Survive, Endless, Remix }

        private static readonly (Mode mode, string label, bool enabled)[] Entries =
        {
            (Mode.Arcade, null, true),     // game sprite
            (Mode.Survive, null, true),    // game sprite
            (Mode.Endless, "ENDLESS", true),
            (Mode.Remix, "REMIX", false),  // modded game: not available yet
        };

        // Same layout as the game's difficulty select, which also lists four entries.
        private static readonly Vector2 BasePos = new Vector2(256f, 116f);
        private const float Offset = 48f;

        // Pad bits the game uses for menu navigation (see GameStick).
        private const uint PadDown = 0x8000000;
        private const uint PadUp = 0x4000000;
        private const uint PadConfirm = 0x401000;

        private const int HellDifficulty = 3;

        private static readonly CharWork[] spriteEntries = new CharWork[2];
        private static int menuPos;

        public static Mode Selected => Entries[menuPos].mode;

        [Hook("AncHell.GameMode.GameModeSelect", "Init")]
        public static bool Init(Main main)
        {
            for (int i = 0; i < spriteEntries.Length; i++)
            {
                var cw = CWManager.GetWork();
                spriteEntries[i] = cw;
                if (cw == null) continue;
                cw.Init(Main.CHAR_KIND.SYS_GAMEMODE);
                cw.chrPos = EntryPos(i);
                CWManager.Anm_SetAnim_EFF(cw, i * 2, null);
            }
            menuPos = Main.GAME_isSURVIVEMODE ? 1 : Endless.Active ? 2 : 0;
            return false;
        }

        [Hook("AncHell.GameMode.GameModeSelect", "Release")]
        public static bool Release(Main main)
        {
            for (int i = 0; i < spriteEntries.Length; i++)
            {
                if (spriteEntries[i] != null) CWManager.ReleaseWork(spriteEntries[i]);
                spriteEntries[i] = null;
            }
            return false;
        }

        [Hook("AncHell.GameMode.GameModeSelect", "Update")]
        public static bool Update(Main main)
        {
            main.gameFont.Draw(192, 72, "SELECT GAME MODE");
            for (int i = 0; i < 4; i++)
            {
                if (!GameManager.PL_Active[i]) continue;
                uint pad = GameStick.intPadTrigger[i];
                if ((pad & PadDown) != 0) Move(+1);
                else if ((pad & PadUp) != 0) Move(-1);
                if ((pad & PadConfirm) != 0)
                {
                    Apply();
                    Confirm(main);
                    return false;
                }
                if ((Main.is4PLAYHELL_MODE_PAD || Main.is4PLAYHELL_MODE_KB) && i != 0) break;
            }
            Apply();
            for (int j = 0; j < spriteEntries.Length; j++)
            {
                if (spriteEntries[j] != null)
                    CWManager.Anm_SetAnim_EFF(spriteEntries[j], j == menuPos ? j * 2 + 1 : j * 2, null);
            }
            return false;
        }

        /// <summary>Draws the entries the game has no sprites for (runs before the game's sprites).</summary>
        [Hook("AncHell.GameMode.GameModeSelect", "Draw", HookKind.Postfix)]
        public static void Draw(Main main)
        {
            main.objSprite.Begin();
            for (int i = 0; i < Entries.Length; i++)
            {
                var (_, label, enabled) = Entries[i];
                if (label == null) continue;
                Vector2 center = EntryPos(i);
                if (i == menuPos)
                {
                    var box = MenuArt.SelectionBox;
                    main.objSprite.Draw(MenuArt.BoxTexture(main), TopLeft(center, box.Width, box.Height), box, Color.White);
                }
                var word = MenuArt.Word(main, label, disabled: !enabled);
                main.objSprite.Draw(word, TopLeft(center, word.Width, word.Height), Color.White);
            }
            main.objSprite.End();
        }

        private static void Move(int step)
        {
            SoundManager.PlaySE(SoundManager.SEList.SE01_Select, 5, 5);
            do
            {
                menuPos = (menuPos + step + Entries.Length) % Entries.Length;
            } while (!Entries[menuPos].enabled);
        }

        /// <summary>Tells the game (and Endless) which mode the cursor is on, as the original screen does every frame.</summary>
        private static void Apply()
        {
            Main.GAME_isSURVIVEMODE = Selected == Mode.Survive;
            Endless.SetActive(Selected == Mode.Endless);
        }

        private static void Confirm(Main main)
        {
            SoundManager.PlaySE(SoundManager.SEList.SE00_Entry, 5, 5);
            if (Selected == Mode.Endless)
            {
                // Endless always runs on the hardest difficulty: skip difficulty select.
                Main.GAME_Difficulty = HellDifficulty;
                Interface.SetStageNum(0, Main.GAME_Difficulty);
                main.ChangeGameMode(Main.GAMEMODE.BeforePlaying);
            }
            else
            {
                main.ChangeGameMode(Main.GAMEMODE.DifficultSelect);
            }
        }

        /// <summary>
        /// Backing out of the pre-game screen returns to difficulty select;
        /// Endless has none, so go straight back to mode select instead.
        /// </summary>
        [Hook("AncHell.GameMode.DifficultSelect", "Update")]
        public static bool DifficultSelectUpdate(Main main)
        {
            if (!Endless.Active) return true;
            main.ChangeGameMode(Main.GAMEMODE.GameModeSelect);
            return false;
        }

        private static Vector2 EntryPos(int index) => new Vector2(BasePos.X, BasePos.Y + Offset * index);

        private static Vector2 TopLeft(Vector2 center, int width, int height) =>
            new Vector2((int)(center.X - width / 2), (int)(center.Y - height / 2));
    }
}

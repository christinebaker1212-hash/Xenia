// Declarations of the game members PMK.Mods uses. Names, types and enum
// values must match the game exactly; ModLinker verifies them at install time
// and refuses to install mods that don't match. Bodies are never executed.
// Enums list only the members PMK.Mods needs (an empty enum is used where
// only the type itself matters).
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using AncHell.Action;
using AncHell.Stage;
using AncHell.System;

namespace AncHell
{
    public class Main
    {
        public enum GAMEMODE
        {
            BeforePlaying = 4,
            DifficultSelect = 12,
            GameModeSelect = 13,
        }

        public enum CHAR_KIND
        {
            ENE_MASTER_GOBLIN = 52,
            SYS_GAMEMODE = 82,
        }

        public static int GAME_Difficulty;
        public static bool GAME_isSURVIVEMODE;
        public static int MAX_STAGE_COUNT;
        public static bool is4PLAYHELL_MODE_PAD;
        public static bool is4PLAYHELL_MODE_KB;

        public PMK.Compat.Content.ContentManager objContent;
        public PMK.Compat.Graphics.SpriteBatch objSprite;
        public GameFont gameFont;

        public void ChangeGameMode(GAMEMODE newGameMode) => throw null;
    }

    public class GameFont
    {
        public void Draw(int x, int y, string message) => throw null;
    }
}

namespace AncHell.Action
{
    public class Act { }
}

namespace AncHell.GameSystem
{
    public class Interface
    {
        public static void SetStageNum(int StageNum, int Difficulty) => throw null;
    }
}

namespace AncHell.System
{
    public class CharWork
    {
        public int spAnmNum;
        public Vector2 chrPos;
        public virtual void Init(Main.CHAR_KIND _kind) => throw null;
    }

    public static class CWManager
    {
        public static CharWork GetWork() => throw null;
        public static void ReleaseWork(CharWork work) => throw null;
        public static void Anm_SetAnim_EFF(CharWork cw, int AnimNum, Act NewAct) => throw null;
    }

    public class GameManager
    {
        public enum STG_Wall { }
        public enum STG_Wall_Type { }
        public enum MonsterSpawnPoint { }

        public static bool[] PL_Active;
        public static int GM_CurrentStage;
        public static CharWork GOCW_0;
        public static CharWork GOCW_00;
        public static SoundManager.BGMList[] StageBGMList;

        public static void GameReset() => throw null;
        public static void StageReset() => throw null;
    }

    public static class GameStick
    {
        public static uint[] intPadTrigger;
    }

    public static class SoundManager
    {
        public enum BGMList { }

        public enum SEList
        {
            SE00_Entry = 0,
            SE01_Select = 1,
        }

        public static void PlaySE(SEList newSE, int StopPriority, int InterruptPriority) => throw null;
    }
}

namespace AncHell.Stage
{
    public class StgEvent
    {
        public int WaitTimer;
        public Main.CHAR_KIND kind;
        public int[] SpawnCount;
        public int SpawnDelay;
        public GameManager.MonsterSpawnPoint point;
        public int WaitMonsterCount;
        public float[] MultiplayIncreaseValue;
        public bool BGM_isChangeBGM;
        public bool BGM_isFadeOut;
        public float BGM_FadeOutTime;
        public SoundManager.BGMList BGM_NewBGM;
        public bool GOB_BALLOON_GetOut;
    }

    public class StageManager
    {
        public static List<List<StgEvent>> StgEventLists;
        public static List<int> StgMonsterCountList;
        public static List<GameManager.STG_Wall> StgWallList;
        public static List<GameManager.STG_Wall_Type> StgWallTypeList;
        public static List<int[]> StgMapObjList;
        public static List<Texture2D> StgTexList;

        public static int CountMonster(List<StgEvent> EventList) => throw null;
    }
}

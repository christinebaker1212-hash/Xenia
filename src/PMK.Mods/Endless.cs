using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using AncHell;
using AncHell.Action;
using AncHell.Stage;
using AncHell.System;

namespace PMK.Mods
{
    /// <summary>
    /// ENDLESS: the arcade stages on HELL, without an ending. After the last
    /// arcade stage the stages repeat in order, and every stage past the last
    /// one adds <see cref="ExtraMonstersPerStage"/> more monsters (stage 11 is
    /// stage 1 with 125% of its monsters, stage 12 is stage 2 with 150%, ...).
    /// The final boss is never duplicated.
    ///
    /// The game looks stages up by number in several places (waves, map,
    /// background, music), so instead of patching each lookup, Endless
    /// extends the stage tables on demand: entry N is a scaled copy of arcade
    /// stage ((N - 1) % 10) + 1. Entries 11-16 normally hold Survive and unused
    /// test stages; they are saved and put back when Endless ends.
    /// </summary>
    public static class Endless
    {
        public const int ArcadeStageCount = 10;

        /// <summary>Fraction of a stage's monsters added for each stage past the last arcade stage.</summary>
        public const float ExtraMonstersPerStage = 0.25f;

        public static bool Active { get; private set; }

        private static int savedMaxStageCount;
        private static int builtUpTo = ArcadeStageCount;
        private static Snapshot original;

        /// <summary>Monster multiplier for a stage number (1 for the arcade stages).</summary>
        public static float MonsterMultiplier(int stage) =>
            stage <= ArcadeStageCount ? 1f : 1f + ExtraMonstersPerStage * (stage - ArcadeStageCount);

        /// <summary>The arcade stage a stage number repeats.</summary>
        public static int BaseStage(int stage) => (stage - 1) % ArcadeStageCount + 1;

        /// <summary>Scales a wave's spawn counts. Markers (0 or negative) and the final boss stay as they are.</summary>
        public static int ScaleCount(int count, float multiplier, bool isFinalBoss) =>
            count <= 0 || isFinalBoss ? count : (int)Math.Round(count * multiplier, MidpointRounding.AwayFromZero);

        public static void SetActive(bool on)
        {
            if (on == Active) return;
            if (on)
            {
                savedMaxStageCount = Main.MAX_STAGE_COUNT;
                Main.MAX_STAGE_COUNT = int.MaxValue; // never reach the ending
            }
            else
            {
                Main.MAX_STAGE_COUNT = savedMaxStageCount;
                Restore();
            }
            Active = on;
        }

        /// <summary>Back at the title screen: leave Endless.</summary>
        [Hook("AncHell.System.GameManager", "GameReset", HookKind.Postfix)]
        public static void OnGameReset() => SetActive(false);

        /// <summary>Every stage start (including retries) goes through here before the stage tables are read.</summary>
        [Hook("AncHell.System.GameManager", "StageReset")]
        public static void OnStageReset()
        {
            if (Active && GameManager.GM_CurrentStage > ArcadeStageCount)
                EnsureStage(GameManager.GM_CurrentStage);
        }

        /// <summary>
        /// The "STAGE nn" banner has sprites for two digits only. Past stage 99
        /// it shows 99 (the HUD already caps at 99) instead of an invalid sprite.
        /// </summary>
        [Hook("AncHell.System.CWManager", "Anm_SetAnim_EFF", HookKind.Postfix)]
        public static void OnSetAnim(CharWork cw, int animNum, Act newAct)
        {
            if (GameManager.GM_CurrentStage > 99 && cw != null
                && (cw == GameManager.GOCW_0 || cw == GameManager.GOCW_00))
                cw.spAnmNum = 12 + 9;
        }

        private static void EnsureStage(int stage)
        {
            if (original == null)
            {
                original = Snapshot.Take();
                // Work on a copy so the saved table stays untouched.
                GameManager.StageBGMList = (SoundManager.BGMList[])GameManager.StageBGMList.Clone();
            }
            while (builtUpTo < stage)
            {
                builtUpTo++;
                Build(builtUpTo);
            }
        }

        private static void Build(int stage)
        {
            int baseStage = BaseStage(stage);
            float multiplier = MonsterMultiplier(stage);
            var events = new List<StgEvent>();
            foreach (var e in StageManager.StgEventLists[baseStage])
                events.Add(Scaled(e, multiplier));

            Put(StageManager.StgEventLists, stage, events);
            Put(StageManager.StgMonsterCountList, stage, StageManager.CountMonster(events));
            Put(StageManager.StgWallList, stage, StageManager.StgWallList[baseStage]);
            Put(StageManager.StgWallTypeList, stage, StageManager.StgWallTypeList[baseStage]);
            Put(StageManager.StgMapObjList, stage, StageManager.StgMapObjList[baseStage]);
            Put(StageManager.StgTexList, stage, StageManager.StgTexList[baseStage]);

            var bgm = GameManager.StageBGMList;
            if (bgm.Length <= stage) Array.Resize(ref bgm, Math.Max(stage + 1, bgm.Length * 2));
            bgm[stage] = bgm[baseStage];
            GameManager.StageBGMList = bgm;
        }

        private static StgEvent Scaled(StgEvent e, float multiplier)
        {
            bool finalBoss = e.kind == Main.CHAR_KIND.ENE_MASTER_GOBLIN;
            var copy = new StgEvent
            {
                WaitTimer = e.WaitTimer,
                kind = e.kind,
                SpawnCount = (int[])e.SpawnCount.Clone(),
                SpawnDelay = e.SpawnDelay,
                point = e.point,
                WaitMonsterCount = e.WaitMonsterCount,
                MultiplayIncreaseValue = (float[])e.MultiplayIncreaseValue.Clone(),
                BGM_isChangeBGM = e.BGM_isChangeBGM,
                BGM_isFadeOut = e.BGM_isFadeOut,
                BGM_FadeOutTime = e.BGM_FadeOutTime,
                BGM_NewBGM = e.BGM_NewBGM,
                GOB_BALLOON_GetOut = e.GOB_BALLOON_GetOut,
            };
            for (int d = 0; d < copy.SpawnCount.Length; d++)
                copy.SpawnCount[d] = ScaleCount(copy.SpawnCount[d], multiplier, finalBoss);
            return copy;
        }

        private static void Put<T>(List<T> list, int index, T value)
        {
            if (index < list.Count) list[index] = value;
            else
            {
                while (list.Count < index) list.Add(list[list.Count - 1]); // never happens: stages are built in order
                list.Add(value);
            }
        }

        private static void Restore()
        {
            if (original == null) return;
            original.Apply();
            original = null;
            builtUpTo = ArcadeStageCount;
        }

        /// <summary>The stage tables as the game built them.</summary>
        private sealed class Snapshot
        {
            private List<StgEvent>[] events;
            private int[] monsterCounts;
            private GameManager.STG_Wall[] walls;
            private GameManager.STG_Wall_Type[] wallTypes;
            private int[][] mapObjects;
            private Texture2D[] textures;
            private SoundManager.BGMList[] bgm;

            public static Snapshot Take() => new Snapshot
            {
                events = StageManager.StgEventLists.ToArray(),
                monsterCounts = StageManager.StgMonsterCountList.ToArray(),
                walls = StageManager.StgWallList.ToArray(),
                wallTypes = StageManager.StgWallTypeList.ToArray(),
                mapObjects = StageManager.StgMapObjList.ToArray(),
                textures = StageManager.StgTexList.ToArray(),
                bgm = GameManager.StageBGMList,
            };

            public void Apply()
            {
                Reset(StageManager.StgEventLists, events);
                Reset(StageManager.StgMonsterCountList, monsterCounts);
                Reset(StageManager.StgWallList, walls);
                Reset(StageManager.StgWallTypeList, wallTypes);
                Reset(StageManager.StgMapObjList, mapObjects);
                Reset(StageManager.StgTexList, textures);
                GameManager.StageBGMList = bgm;
            }

            private static void Reset<T>(List<T> list, T[] items)
            {
                list.Clear();
                list.AddRange(items);
            }
        }
    }
}

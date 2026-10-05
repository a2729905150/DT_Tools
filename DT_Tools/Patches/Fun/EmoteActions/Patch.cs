using System;
using System.Collections.Generic;
using DT_Tools.Core;
using HarmonyLib;
using Protocol;
using Spine;
using Spine.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DT_Tools.Patches.Fun.EmoteActions
{
    /// <summary>
    /// 未实装动作补丁集合（原 DT_EmoteActions v1.0.0 移植）：
    /// 1. 本地播放（Managers.Update Postfix）：本地玩家存活、可控制、非聊天/非表情界面、
    ///    待机站立（Idle/Run）且未在播动作时，检测 HiKey / ProposalKey / SwimKey 按下，
    ///    经 Player.PlayDeadlyTrickAnim（0.1.16b Player.cs:1346）播放对应骨骼动画
    ///    （13_Hi / 9_Proposal / 15_Swimming）；动画播完 / 玩家移动 / 状态变化时
    ///    自动 EndDeadlyTrickAnim（0.1.16b Player.cs:1352）还原。
    /// 2. 动作同步（SyncActions）：触发成功后经聊天通道广播一条零宽前缀指令
    ///    （如 "\u200B（挥手）"）；接收端 patch VoiceManager.EnqueueNormalChat
    ///    （0.1.16b VoiceManager.cs:1338，聊天接收入口）识别指令 → 拦截显示 +
    ///    对对应玩家（Managers.Player.Players[playerId]）播放同样动画并在到时/移动时还原。
    ///    没装本 mod 的玩家只会看到（挥手）等友好文本（零宽前缀不可见），房主无需开启。
    /// 键位全部走配置（KeyCode 枚举，None=关闭）；聊天输入框聚焦时跳过（防误触），
    /// 与游戏自身按键互不冲突（默认 H / Y / 无）。
    /// </summary>
    internal static class Patches
    {
        // ===== 本地播放状态 =====

        private static Player _actor;
        private static float _endTime;
        private static bool _acting;
        private static bool _warnedHi;
        private static bool _warnedProposal;
        private static bool _warnedSwim;

        // ===== 动作同步：指令格式与远端状态 =====

        /// <summary>指令前缀：零宽字符 + 全角左括号，对没装 mod 的玩家不可见。</summary>
        private const string CmdPrefix = "\u200B（";

        private const string CmdSuffix = "）";

        /// <summary>远端玩家正在播放的动作：playerId → 结束时间（unscaledTime）。</summary>
        private static readonly Dictionary<int, float> RemoteEndTimes = new Dictionary<int, float>();

        /// <summary>远端玩家播放时的警告去重。</summary>
        private static readonly Dictionary<int, bool> RemoteWarned = new Dictionary<int, bool>();

        // ===== 1. 本地播放 =====

        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class EmoteActionsUpdatePatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!Engine.Enabled<EmoteActionsFeature>())
                    {
                        return;
                    }

                    if (_acting)
                    {
                        TickEnd();
                    }

                    TickRemoteEnd();

                    Player me = Managers.Player != null ? Managers.Player.MyPlayer : null;
                    if (me == null || _acting)
                    {
                        return;
                    }

                    GameManagerEX game = Managers.Game;
                    if (game == null || !game.CanControl || game.IsChat || game.IsOpenEmote
                        || !game.IsAlive || BlockedByInputField())
                    {
                        return;
                    }

                    if (me.Moving && !EmoteActionsFeature.AllowWhileMoving)
                    {
                        return;
                    }

                    if (me.State != EPlayerState.Idle && me.State != EPlayerState.Run)
                    {
                        return;
                    }

                    if (EmoteActionsFeature.HiKey != KeyCode.None && Input.GetKeyDown(EmoteActionsFeature.HiKey))
                    {
                        TryPlay(me, "13_Hi", "挥手", ref _warnedHi);
                    }
                    else if (EmoteActionsFeature.ProposalKey != KeyCode.None && Input.GetKeyDown(EmoteActionsFeature.ProposalKey))
                    {
                        TryPlay(me, "9_Proposal", "求婚", ref _warnedProposal);
                    }
                    else if (EmoteActionsFeature.SwimKey != KeyCode.None && Input.GetKeyDown(EmoteActionsFeature.SwimKey))
                    {
                        TryPlay(me, "15_Swimming", "游泳", ref _warnedSwim);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error<EmoteActionsFeature>("[未实装动作] 按键检测异常：" + ex.Message);
                }
            }
        }

        /// <summary>本地播放：骨骼里没有该动画时只警告一次并跳过；成功后按 SyncActions 广播。</summary>
        private static void TryPlay(Player p, string animName, string friendlyName, ref bool warned)
        {
            SkeletonAnimation skeletonAnim = p.SkeletonAnim;
            if (skeletonAnim == null || skeletonAnim.skeleton == null || skeletonAnim.skeleton.Data == null)
            {
                return;
            }

            Animation anim = skeletonAnim.skeleton.Data.FindAnimation(animName);
            if (anim == null)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warn<EmoteActionsFeature>("[未实装动作] 当前角色骨骼里没有动画 " + animName + "，跳过。");
                }
                return;
            }

            p.PlayDeadlyTrickAnim(animName, false);
            _actor = p;
            _acting = true;
            _endTime = Time.unscaledTime + anim.Duration + 0.15f;
            Log.Info<EmoteActionsFeature>("[未实装动作] 播放 " + animName + "（时长 " + anim.Duration.ToString("F2") + "s）");

            if (EmoteActionsFeature.SyncActions)
            {
                BroadcastAction(friendlyName);
            }
        }

        /// <summary>本地动作收尾：对象失效 / 到时 / 玩家离开待机态（移动、切状态）即结束并还原。</summary>
        private static void TickEnd()
        {
            bool finish = false;
            Player actor = _actor;
            if (actor == null || actor.gameObject == null)
            {
                finish = true;
            }
            else if (Time.unscaledTime >= _endTime)
            {
                finish = true;
            }
            else if (actor.State != EPlayerState.Idle || actor.Moving)
            {
                finish = true;
            }

            if (finish)
            {
                _acting = false;
                _actor = null;
                if (actor != null && actor.gameObject != null)
                {
                    actor.EndDeadlyTrickAnim();
                }
            }
        }

        // ===== 2. 动作同步 =====

        /// <summary>广播动作指令：零宽前缀 + 友好文本，房主原生转发全房。</summary>
        private static void BroadcastAction(string friendlyName)
        {
            try
            {
                Managers.Voice?.SendChatMessage(CmdPrefix + friendlyName + CmdSuffix);
            }
            catch (Exception ex)
            {
                Log.Warn<EmoteActionsFeature>("[未实装动作] 同步广播失败：" + ex.Message);
            }
        }

        /// <summary>接收端：聊天接收入口识别动作指令 → 拦截显示 + 播放对应玩家动画。</summary>
        [HarmonyPatch(typeof(VoiceManager), "EnqueueNormalChat")]
        internal static class EmoteActionsReceivePatch
        {
            private static bool Prefix(int playerId, string message, bool isDeadByHost)
            {
                try
                {
                    if (!Engine.Enabled<EmoteActionsFeature>())
                    {
                        return true;
                    }

                    if (string.IsNullOrEmpty(message) || !message.StartsWith(CmdPrefix) || !message.EndsWith(CmdSuffix))
                    {
                        return true;
                    }

                    string friendlyName = message.Substring(CmdPrefix.Length, message.Length - CmdPrefix.Length - 1);
                    string animName = MapFriendlyToAnim(friendlyName);
                    if (animName == null)
                    {
                        // 指令格式但内容不认识：按普通聊天显示
                        return true;
                    }

                    // 自己发的广播：本地已在播，只拦截显示
                    if (Managers.Player != null && playerId == Managers.Player.MyPlayerID)
                    {
                        return false;
                    }

                    PlayRemote(playerId, animName);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Warn<EmoteActionsFeature>("[未实装动作] 同步接收异常：" + ex.Message);
                    return true;
                }
            }
        }

        /// <summary>友好文本 → 动画名。</summary>
        private static string MapFriendlyToAnim(string friendlyName)
        {
            switch (friendlyName)
            {
                case "挥手": return "13_Hi";
                case "求婚": return "9_Proposal";
                case "游泳": return "15_Swimming";
                default: return null;
            }
        }

        /// <summary>对远端玩家播放动作（不显示在聊天区）。</summary>
        private static void PlayRemote(int playerId, string animName)
        {
            Player p = null;
            if (Managers.Player != null && Managers.Player.Players != null)
            {
                Managers.Player.Players.TryGetValue(playerId, out p);
            }
            if (p == null || p.gameObject == null)
            {
                return;
            }

            SkeletonAnimation skeletonAnim = p.SkeletonAnim;
            if (skeletonAnim == null || skeletonAnim.skeleton == null || skeletonAnim.skeleton.Data == null)
            {
                return;
            }

            Animation anim = skeletonAnim.skeleton.Data.FindAnimation(animName);
            if (anim == null)
            {
                if (!RemoteWarned.TryGetValue(playerId, out bool warned) || !warned)
                {
                    RemoteWarned[playerId] = true;
                    Log.Warn<EmoteActionsFeature>("[未实装动作] 远端玩家 " + playerId + " 骨骼里没有动画 " + animName + "，跳过。");
                }
                return;
            }

            p.PlayDeadlyTrickAnim(animName, false);
            RemoteEndTimes[playerId] = Time.unscaledTime + anim.Duration + 0.15f;
            Log.Info<EmoteActionsFeature>("[未实装动作] 同步播放 " + playerId + " 的 " + animName);
        }

        /// <summary>远端动作收尾：到时 / 玩家移动 / 切状态 / 对象失效即还原并清理。</summary>
        private static void TickRemoteEnd()
        {
            if (RemoteEndTimes.Count == 0)
            {
                return;
            }

            List<int> expired = null;
            foreach (KeyValuePair<int, float> kv in RemoteEndTimes)
            {
                Player p = null;
                if (Managers.Player != null && Managers.Player.Players != null)
                {
                    Managers.Player.Players.TryGetValue(kv.Key, out p);
                }

                bool finish = p == null || p.gameObject == null
                    || Time.unscaledTime >= kv.Value
                    || p.State != EPlayerState.Idle
                    || p.Moving;

                if (finish)
                {
                    if (p != null && p.gameObject != null)
                    {
                        p.EndDeadlyTrickAnim();
                    }
                    if (expired == null)
                    {
                        expired = new List<int>();
                    }
                    expired.Add(kv.Key);
                }
            }

            if (expired != null)
            {
                foreach (int id in expired)
                {
                    RemoteEndTimes.Remove(id);
                }
            }
        }

        /// <summary>聊天输入框等 UI 输入聚焦时不触发（防误触）。</summary>
        private static bool BlockedByInputField()
        {
            EventSystem current = EventSystem.current;
            GameObject selected = current != null ? current.currentSelectedGameObject : null;
            if (selected == null)
            {
                return false;
            }
            return selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null;
        }
    }
}

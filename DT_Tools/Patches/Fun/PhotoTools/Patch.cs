using System;
using System.Collections.Generic;
using DT_Tools.Core;
using DT_Tools.Game;
using HarmonyLib;
using Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace DT_Tools.Patches.Fun.PhotoTools
{
    /// <summary>
    /// 拍照工具补丁集合（基于 0.1.16b PhotoManager）：
    /// 1. get_MaxFilm：UnlimitFilm 开启时胶卷上限改为 999；
    /// 2. TryShoot Postfix：每次拍照成功立即补满胶卷（RefillFilm → Film=MaxFilm），实现拍不完；
    /// 3. EncodeAdaptive Prefix：Filter 非 None 时在 JPEG 编码前对截图像素应用滤镜，
    ///    EvidenceStamp 开启时再叠加证物水印（谁拍的 · 游戏内时间 · 所在房间），
    ///    发送给全房的照片即为处理后的效果（EncodeAdaptive 是拍照编码唯一入口，0.1.16b PhotoManager.cs:600）；
    /// 4. CanStayInPhotoMode Postfix：AllowTrialPhoto 开启时任意阶段（庭审/生存等）放行，
    ///    GhostCamera 开启时死亡（幽灵）也放行，其余条件保持原版一致；
    /// 5. CanEnterPhotoMode Postfix：幽灵放行（跳过待机态检查）并自动补满胶卷；
    /// 6. Managers.Update Postfix：非调查阶段按 TrialPhotoKey 直接进入拍照（界面挡按钮时兜底）。
    /// </summary>
    internal static class Patches
    {
        /// <summary>拍立得边框宽度（像素，基于 800x600 截图）。</summary>
        private const int PolaroidBorder = 22;

        /// <summary>暗角强度：1=边缘完全变黑，0.4=轻微。</summary>
        private const float VignetteStrength = 0.55f;

        // ===== 1. 胶卷上限 =====

        [HarmonyPatch(typeof(PhotoManager), "MaxFilm", MethodType.Getter)]
        internal static class PhotoMaxFilmPatch
        {
            private static void Postfix(ref int __result)
            {
                if (!Engine.Enabled<PhotoToolsFeature>() || !PhotoToolsFeature.UnlimitFilm)
                {
                    return;
                }
                __result = 999;
            }
        }

        // ===== 2. 拍完自动补满 =====

        [HarmonyPatch(typeof(PhotoManager), "TryShoot")]
        internal static class PhotoRefillPatch
        {
            private static void Postfix(PhotoManager __instance, bool __result)
            {
                try
                {
                    if (!Engine.Enabled<PhotoToolsFeature>() || !PhotoToolsFeature.UnlimitFilm || !__result)
                    {
                        return;
                    }
                    __instance.RefillFilm();
                }
                catch (Exception ex)
                {
                    Log.Warn<PhotoToolsFeature>("[拍照工具] 补满胶卷异常：" + ex.Message);
                }
            }
        }

        // ===== 3. 照片滤镜 + 证物水印 =====

        [HarmonyPatch(typeof(PhotoManager), "EncodeAdaptive")]
        internal static class PhotoFilterPatch
        {
            private static void Prefix(Texture2D tex)
            {
                try
                {
                    if (!Engine.Enabled<PhotoToolsFeature>() || tex == null)
                    {
                        return;
                    }
                    if (PhotoToolsFeature.Filter != PhotoFilterType.None)
                    {
                        ApplyFilter(tex, PhotoToolsFeature.Filter);
                    }
                    if (PhotoToolsFeature.EvidenceStamp)
                    {
                        ApplyEvidenceStamp(tex);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn<PhotoToolsFeature>("[拍照工具] 照片处理异常：" + ex.Message);
                }
            }
        }

        // ===== 4. 任意阶段 + 幽灵放行（持续判定） =====

        /// <summary>
        /// CanStayInPhotoMode（0.1.16b PhotoManager.cs:224）Postfix：原版要求
        /// Managers.Game.State == EGameState.Detective 且存活才允许拍照。
        /// AllowTrialPhoto 开启时任意阶段（庭审/生存等）按原版其余条件（存活或幽灵、
        /// 非旁观/可控制/非聊天/非表情/非加载/非平板）重新判定放行。
        /// </summary>
        [HarmonyPatch(typeof(PhotoManager), "CanStayInPhotoMode")]
        internal static class PhotoTrialStayPatch
        {
            private static void Postfix(ref bool __result)
            {
                try
                {
                    if (__result || !Engine.Enabled<PhotoToolsFeature>() || !PhotoToolsFeature.AllowTrialPhoto)
                    {
                        return;
                    }
                    if (Managers.Game == null)
                    {
                        return;
                    }
                    if (!Managers.Game.IsAlive && !PhotoToolsFeature.GhostCamera)
                    {
                        return;
                    }
                    if (Managers.Game.IsSpectator || !Managers.Game.CanControl)
                    {
                        return;
                    }
                    if (Managers.Game.IsChat || Managers.Game.IsOpenEmote)
                    {
                        return;
                    }
                    if (Managers.UI == null || Managers.UI.IsLoading || Managers.UI.KeyCount > 0)
                    {
                        return;
                    }
                    if (Managers.Tablet?.Tablet != null && Managers.Tablet.Tablet.IsOpen)
                    {
                        return;
                    }
                    __result = true;
                }
                catch (Exception ex)
                {
                    Log.Warn<PhotoToolsFeature>("[拍照工具] 拍照判定异常：" + ex.Message);
                }
            }
        }

        // ===== 5. 幽灵放行（进入判定）+ 自动补胶卷 =====

        /// <summary>
        /// CanEnterPhotoMode（0.1.16b PhotoManager.cs:195）Postfix：幽灵死亡态下
        /// State 不是待机/移动/交互会被原版挡掉，GhostCamera 开启时跳过该检查；
        /// 死亡后胶卷可能已耗尽，放行时自动补满（RefillFilm）。
        /// </summary>
        [HarmonyPatch(typeof(PhotoManager), "CanEnterPhotoMode")]
        internal static class PhotoGhostEnterPatch
        {
            private static void Postfix(PhotoManager __instance, ref bool __result)
            {
                try
                {
                    if (__result || !Engine.Enabled<PhotoToolsFeature>() || !PhotoToolsFeature.GhostCamera)
                    {
                        return;
                    }
                    if (Managers.Game == null || Managers.Game.IsAlive)
                    {
                        return;
                    }
                    if (Traverse.Create(__instance).Field("_frame").GetValue() == null
                        || Traverse.Create(__instance).Field("_overlay").GetValue() == null)
                    {
                        return;
                    }
                    // 复用 CanStayInPhotoMode（已被 AllowTrialPhoto/GhostCamera 放行）
                    if (!__instance.CanStayInPhotoMode())
                    {
                        return;
                    }
                    if (__instance.Film <= 0)
                    {
                        __instance.RefillFilm();
                    }
                    __result = true;
                }
                catch (Exception ex)
                {
                    Log.Warn<PhotoToolsFeature>("[拍照工具] 幽灵拍照判定异常：" + ex.Message);
                }
            }
        }

        // ===== 6. 非调查阶段拍照快捷键 =====

        /// <summary>
        /// Managers.Update（0.1.16b Managers.cs:325）Postfix：任意阶段（庭审/生存/幽灵）
        /// 按 TrialPhotoKey 直接进入拍照模式（阶段界面可能挡住拍照按钮，快捷键兜底），
        /// 与按钮入口同一检查（CanEnterPhotoMode）。
        /// </summary>
        [HarmonyPatch(typeof(Managers), "Update")]
        internal static class PhotoTrialKeyPatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!Engine.Enabled<PhotoToolsFeature>() || !PhotoToolsFeature.AllowTrialPhoto
                        || PhotoToolsFeature.TrialPhotoKey == KeyCode.None)
                    {
                        return;
                    }
                    if (Managers.Photo == null || Managers.Photo.IsPhotoMode || !Input.GetKeyDown(PhotoToolsFeature.TrialPhotoKey))
                    {
                        return;
                    }
                    if (Managers.Photo.CanEnterPhotoMode())
                    {
                        Managers.Photo.SetPhotoMode(true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn<PhotoToolsFeature>("[拍照工具] 拍照快捷键异常：" + ex.Message);
                }
            }
        }

        // ===== 滤镜 =====

        /// <summary>对截图应用滤镜（就地修改像素）。</summary>
        private static void ApplyFilter(Texture2D tex, PhotoFilterType filter)
        {
            Color32[] pixels = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;

            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                float dy = 1f - Mathf.Abs(y - h * 0.5f) / (h * 0.5f); // 1=中间, 0=上下边缘

                for (int x = 0; x < w; x++)
                {
                    int i = rowBase + x;
                    Color32 c = pixels[i];
                    int r = c.r;
                    int g = c.g;
                    int b = c.b;

                    switch (filter)
                    {
                        case PhotoFilterType.BlackWhite:
                        {
                            int gray = (int)(r * 0.299f + g * 0.587f + b * 0.114f);
                            r = gray;
                            g = gray;
                            b = gray;
                            break;
                        }
                        case PhotoFilterType.Retro:
                            r = Mathf.Min(255, (int)(r * 1.12f) + 18);
                            g = Mathf.Min(255, (int)(g * 1.02f) + 6);
                            b = (int)(b * 0.82f);
                            break;
                        case PhotoFilterType.Cool:
                            r = (int)(r * 0.82f);
                            b = Mathf.Min(255, (int)(b * 1.15f) + 12);
                            break;
                        case PhotoFilterType.Invert:
                            r = 255 - r;
                            g = 255 - g;
                            b = 255 - b;
                            break;
                        case PhotoFilterType.Polaroid:
                            // 白边框 + 轻微提亮
                            if (x < PolaroidBorder || y < PolaroidBorder
                                || x >= w - PolaroidBorder || y >= h - PolaroidBorder)
                            {
                                r = 255;
                                g = 255;
                                b = 255;
                            }
                            else
                            {
                                r = Mathf.Min(255, (int)(r * 1.06f) + 8);
                                g = Mathf.Min(255, (int)(g * 1.06f) + 8);
                                b = Mathf.Min(255, (int)(b * 1.06f) + 8);
                            }
                            break;
                        case PhotoFilterType.Vignette:
                        {
                            float dx = 1f - Mathf.Abs(x - w * 0.5f) / (w * 0.5f);
                            float falloff = Mathf.Clamp01((dx + dy) * 0.5f);
                            float mul = 1f - VignetteStrength * (1f - falloff);
                            r = (int)(r * mul);
                            g = (int)(g * mul);
                            b = (int)(b * mul);
                            break;
                        }
                    }

                    pixels[i] = new Color32((byte)r, (byte)g, (byte)b, c.a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        // ===== 证物水印 =====

        /// <summary>水印底条高度（像素）。</summary>
        private const int StampBarHeight = 26;

        /// <summary>水印字体大小（像素）。</summary>
        private const int StampFontSize = 18;

        /// <summary>缓存的水印字体（跨拍照复用，避免每张重建）。</summary>
        private static Font _stampFont;

        /// <summary>在截图左下角叠加「谁拍的 · 时间 · 房间」证物水印。</summary>
        private static void ApplyEvidenceStamp(Texture2D tex)
        {
            string text = BuildStampText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (_stampFont == null)
            {
                _stampFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial", "DejaVu Sans" }, StampFontSize);
            }
            if (_stampFont == null)
            {
                return;
            }

            _stampFont.RequestCharactersInTexture(text);
            Texture2D fontTex = _stampFont.material?.mainTexture as Texture2D;
            if (fontTex == null)
            {
                return;
            }

            TextGenerator gen = new TextGenerator();
            TextGenerationSettings settings = new TextGenerationSettings
            {
                font = _stampFont,
                fontSize = StampFontSize,
                fontStyle = FontStyle.Normal,
                color = Color.white,
                richText = false,
                scaleFactor = 1f,
                textAnchor = TextAnchor.LowerLeft,
                pivot = Vector2.zero,
                lineSpacing = 1f,
                generateOutOfBounds = false,
                verticalOverflow = VerticalWrapMode.Overflow,
                horizontalOverflow = HorizontalWrapMode.Overflow,
                resizeTextForBestFit = false
            };
            gen.PopulateWithErrors(text, settings, null);

            if (gen.vertexCount == 0)
            {
                return;
            }

            // 计算文本包围盒（顶点坐标像素空间，原点左下）
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var verts = gen.verts;
            for (int i = 0; i < gen.vertexCount; i++)
            {
                Vector3 pos = verts[i].position;
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y < minY) minY = pos.y;
                if (pos.y > maxY) maxY = pos.y;
            }

            int textW = Mathf.CeilToInt(maxX - minX);
            int textH = Mathf.CeilToInt(maxY - minY);
            if (textW <= 0 || textH <= 0)
            {
                return;
            }

            int padX = 8;
            int barW = textW + padX * 2;
            int barH = Mathf.Max(StampBarHeight, textH + 6);
            int originX = 6;
            int originY = 6;

            Color32[] pixels = tex.GetPixels32();
            int w = tex.width;
            int h = tex.height;

            // 半透明黑底条
            for (int y = originY; y < originY + barH && y < h; y++)
            {
                for (int x = originX; x < originX + barW && x < w; x++)
                {
                    Color32 c = pixels[y * w + x];
                    pixels[y * w + x] = new Color32((byte)(c.r * 0.35f), (byte)(c.g * 0.35f), (byte)(c.b * 0.35f), c.a);
                }
            }

            // 逐字形把字体纹理像素画上去（白字）
            int fontW = fontTex.width;
            int fontH = fontTex.height;
            Color32[] fontPixels = fontTex.GetPixels32();

            for (int i = 0; i < gen.vertexCount; i += 4)
            {
                if (i + 3 >= gen.vertexCount)
                {
                    break;
                }
                UIVertex v0 = verts[i];
                UIVertex v1 = verts[i + 1];
                UIVertex v2 = verts[i + 2];
                UIVertex v3 = verts[i + 3];

                float x0 = Mathf.Min(v0.position.x, Mathf.Min(v1.position.x, Mathf.Min(v2.position.x, v3.position.x)));
                float x1 = Mathf.Max(v0.position.x, Mathf.Max(v1.position.x, Mathf.Max(v2.position.x, v3.position.x)));
                float y0 = Mathf.Min(v0.position.y, Mathf.Min(v1.position.y, Mathf.Min(v2.position.y, v3.position.y)));
                float y1 = Mathf.Max(v0.position.y, Mathf.Max(v1.position.y, Mathf.Max(v2.position.y, v3.position.y)));

                float u0 = Mathf.Min(v0.uv0.x, Mathf.Min(v1.uv0.x, Mathf.Min(v2.uv0.x, v3.uv0.x)));
                float u1 = Mathf.Max(v0.uv0.x, Mathf.Max(v1.uv0.x, Mathf.Max(v2.uv0.x, v3.uv0.x)));
                float vv0 = Mathf.Min(v0.uv0.y, Mathf.Min(v1.uv0.y, Mathf.Min(v2.uv0.y, v3.uv0.y)));
                float vv1 = Mathf.Max(v0.uv0.y, Mathf.Max(v1.uv0.y, Mathf.Max(v2.uv0.y, v3.uv0.y)));

                int quadW = Mathf.Max(1, Mathf.CeilToInt(x1 - x0));
                int quadH = Mathf.Max(1, Mathf.CeilToInt(y1 - y0));

                for (int sy = 0; sy < quadH; sy++)
                {
                    float tY = quadH <= 1 ? 0.5f : (float)sy / quadH;
                    int fy = Mathf.Clamp(Mathf.FloorToInt((vv0 + (vv1 - vv0) * tY) * fontH), 0, fontH - 1);
                    int py = originY + Mathf.RoundToInt(y0 - minY) + sy;
                    if (py < 0 || py >= h)
                    {
                        continue;
                    }
                    for (int sx = 0; sx < quadW; sx++)
                    {
                        float tX = quadW <= 1 ? 0.5f : (float)sx / quadW;
                        int fx = Mathf.Clamp(Mathf.FloorToInt((u0 + (u1 - u0) * tX) * fontW), 0, fontW - 1);
                        int px = originX + padX + Mathf.RoundToInt(x0 - minX) + sx;
                        if (px < 0 || px >= w)
                        {
                            continue;
                        }
                        Color32 fc = fontPixels[fy * fontW + fx];
                        if (fc.a > 32)
                        {
                            pixels[py * w + px] = new Color32(255, 255, 255, 255);
                        }
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        /// <summary>生成水印文本：谁拍的 · 游戏内时间 · 所在房间。</summary>
        private static string BuildStampText()
        {
            try
            {
                string name = Managers.Player?.MyPlayerName;
                if (string.IsNullOrEmpty(name))
                {
                    name = "?";
                }

                string time = "?";
                if (Managers.Game != null)
                {
                    int totalMin = Managers.Game.SurvivalTime;
                    int hour = totalMin / 60;
                    int minute = totalMin % 60;
                    time = hour + ":" + minute.ToString("00");
                }

                string room = "?";
                if (Managers.Player?.MyPlayer?.PublicInfo?.Pos != null)
                {
                    room = RoomLabel.FromPos(Managers.Player.MyPlayer.PublicInfo.Pos).localized;
                }

                return name + " · " + time + " · " + room;
            }
            catch (Exception ex)
            {
                Log.Warn<PhotoToolsFeature>("[拍照工具] 水印文本生成异常：" + ex.Message);
                return string.Empty;
            }
        }
    }
}

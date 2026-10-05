using DT_Tools.Core.Attributes;
using UnityEngine;

namespace DT_Tools.Patches.Fun.PhotoTools
{
    /// <summary>照片滤镜类型（拍照时对截图应用，处理后的照片全房可见）。</summary>
    public enum PhotoFilterType
    {
        None = 0,
        BlackWhite,
        Retro,
        Cool,
        Invert,
        Polaroid,
        Vignette
    }

    /// <summary>
    /// 拍照工具（原版拍照限 3 张胶卷、仅调查阶段可拍，本功能解开限制并可给照片加料）：
    /// 1. UnlimitFilm：拍照胶卷上限改为 999 张，每次拍完自动补满，想拍多少拍多少；
    /// 2. Filter：拍照时对截图应用滤镜（黑白/复古/冷色/反转/拍立得边框/暗角），
    ///    处理发生在本地 JPEG 编码前，发送给全房的照片就是 P 完的效果，
    ///    接收方无需安装本 mod（照片是游戏原生 C_CHAT_PHOTO 全房广播）；
    /// 3. AllowTrialPhoto + TrialPhotoKey：庭审/生存等非调查阶段也能进入拍照模式
    ///    （原版仅调查阶段可拍），并支持用快捷键直接进入（阶段界面可能挡住拍照按钮）；
    /// 4. GhostCamera：死亡后（幽灵视角）也能进入拍照模式，记录凶手动向交给队友；
    /// 5. EvidenceStamp：拍照时在照片角落盖上「谁拍的 · 游戏内时间 · 所在房间」证物水印。
    /// 发送防刷限速（约 5 秒一张）与单张 160KB 上限为游戏原生，保留不变。
    /// </summary>
    [PatchFeature(
        "拍照工具：拍照不限次数（胶卷 999 张，拍完自动补满）；可选照片滤镜（黑白/复古/冷色/反转/拍立得/暗角）；庭审/生存等非调查阶段可拍照；幽灵相机（死后也能拍）；证物水印（谁拍的·时间·房间）。处理后的照片全房可见，接收方无需装 mod。",
        defaultEnabled: false,
        side: FeatureSide.Client,
        Author = "花语")]
    public sealed class PhotoToolsFeature
    {
        [Config("拍照不限次数：胶卷上限改为 999，每次拍完自动补满。")]
        public static bool UnlimitFilm = false;

        [Config("照片滤镜：None=原版；BlackWhite=黑白；Retro=复古泛黄；Cool=冷色；Invert=反转负片；Polaroid=拍立得白边框；Vignette=暗角。")]
        public static PhotoFilterType Filter = PhotoFilterType.None;

        [Config("允许庭审/生存等非调查阶段进入拍照模式（原版仅调查阶段可拍）。")]
        public static bool AllowTrialPhoto = false;

        [Config("非调查阶段拍照快捷键（庭审/生存阶段界面可能挡住拍照按钮时用这个直接进拍照）。设为 None 关闭。")]
        public static KeyCode TrialPhotoKey = KeyCode.None;

        [Config("幽灵相机：死亡后（幽灵视角）也能进入拍照模式（原版要求存活）。")]
        public static bool GhostCamera = false;

        [Config("证物水印：拍照时在照片角落盖上「谁拍的 · 游戏内时间 · 所在房间」。")]
        public static bool EvidenceStamp = false;
    }
}

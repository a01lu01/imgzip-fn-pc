namespace ImgZip.Core;

/// <summary>无来源启动（双击图标 / 再点一次快捷方式）时，应该对已有窗口做什么。</summary>
public enum RecallAction
{
    /// <summary>没有窗口可用，调用方只能新建一个。</summary>
    Open,
    /// <summary>有空白草稿：只把它叫到前面，别的窗口一概不碰。</summary>
    FocusDraft,
    /// <summary>没有空白草稿：把已有窗口整体还原并置前，而不是新开一个空壳。</summary>
    RevealAll,
}

/// <summary>
/// 无来源启动的取舍。抽成纯函数是为了能测——真正的窗口操作在 App 里做，
/// 但「该做哪一件」是这个应用里唯一容易搞错的地方，值得单独钉住。
/// </summary>
public static class RecallPolicy
{
    /// <summary>
    /// <paramref name="windowCount"/> 为 0 时返回 <see cref="RecallAction.Open"/>：这是首次启动，
    /// 没有可叫回来的东西。除此之外一定有窗口可操作。
    /// </summary>
    /// <remarks>
    /// 早先的判据是「找不到空白草稿就新建」，结果是：用户压完三批图（三个窗口都有来源）
    /// 再双击图标，得到的是第四个空窗口，原来的三个还最小化着。用户要的是回来看结果，
    /// 不是再来一个空壳，所以这一支现在是 <see cref="RecallAction.RevealAll"/>。
    /// </remarks>
    public static RecallAction Decide(int windowCount, bool hasBlankDraft) =>
        windowCount == 0 ? RecallAction.Open
        : hasBlankDraft ? RecallAction.FocusDraft
        : RecallAction.RevealAll;
}

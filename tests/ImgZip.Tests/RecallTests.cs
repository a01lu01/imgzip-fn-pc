using ImgZip.Core;

static class RecallTests
{
    public static Task RunAsync(Func<string, Func<Task>, Task> test) => test("recall policy chooses who to bring back", () =>
    {
        // 无来源启动（双击图标）该做什么。三个分支里只有一处容易搞错，
        // 而那正好是过去出过问题的地方：没有空白草稿时应把已有窗口叫回来，
        // 而不是新开一个空壳。
        Check(RecallPolicy.Decide(windowCount: 0, hasBlankDraft: false) == RecallAction.Open,
            "First launch must open a window; there is nothing to recall");
        Check(RecallPolicy.Decide(windowCount: 0, hasBlankDraft: true) == RecallAction.Open,
            "A blank draft cannot exist without a window; treat it as a fresh start");

        Check(RecallPolicy.Decide(windowCount: 1, hasBlankDraft: true) == RecallAction.FocusDraft,
            "A blank draft should be focused rather than duplicated");
        Check(RecallPolicy.Decide(windowCount: 3, hasBlankDraft: true) == RecallAction.FocusDraft,
            "A blank draft wins even when other windows are busy");

        // 这一条是修复的核心：以前这里是「新建空窗口」。
        Check(RecallPolicy.Decide(windowCount: 1, hasBlankDraft: false) == RecallAction.RevealAll,
            "A single occupied window should be revealed, not replaced by an empty one");
        Check(RecallPolicy.Decide(windowCount: 3, hasBlankDraft: false) == RecallAction.RevealAll,
            "Three occupied windows should all be revealed; opening a fourth empty one is the bug");
        return Task.CompletedTask;
    });

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

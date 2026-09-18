using MegaCrit.Sts2.Core.Localization;

namespace HomuraLog.UI;

internal static class HomuraText
{
    public static string HandPosition(int position) => Chinese ? $"手牌第 {position} 张" : $"Hand #{position}";

    // The game switches its own localization independently of Godot's project locale.
    // Reading LocManager keeps our labels in sync after changing language and reloading a run.
    internal static string Language => LocManager.Instance?.Language ?? "eng";
    internal static bool Chinese => Language is "zhs" or "zht"
        || Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    public static string Title => Chinese ? "世界线记录" : "Timeline Log";
    public static string Hide => Chinese ? "收起" : "Collapse";
    public static string Show => Chinese ? "世界线" : "Timeline";
    public static string Current => Chinese ? "当前路径" : "Current path";
    public static string Next => Chinese ? "已探索的下一步" : "Explored next actions";
    public static string Tree => Chinese ? "世界线分支" : "Timeline branches";
    public static string Decision => Chinese ? "决策" : "Decision";
    public static string VisitsColumn => Chinese ? "次数" : "Visits";
    public static string Result => Chinese ? "结果" : "Result";
    public static string Details => Chinese ? "节点状态" : "Node state";
    public static string Root => Chinese ? "战斗入口" : "Combat entry";
    public static string None => Chinese ? "尚无" : "None";
    public static string Nodes(int count) => Chinese ? $"已记录 {count - 1} 个决策节点" : $"{count - 1} decision nodes recorded";
    public static string Visits(int count) => Chinese ? $"走过 {count} 次" : $"visited {count}x";
    public static string Disabled => Chinese ? "多人战斗中已禁用" : "Disabled in multiplayer";
    public static string Hp => Chinese ? "生命" : "HP";
    public static string Energy => Chinese ? "能量" : "Energy";
    public static string Block => Chinese ? "格挡" : "Block";
    public static string Intent => Chinese ? "意图" : "Intent";
    public static string EnemyHp => Chinese ? "敌方" : "Enemies";
    public static string EndTurn => Chinese ? "结束回合" : "End turn";
    public static string Skip => Chinese ? "跳过选择" : "Skip choice";
    public static string Choice => Chinese ? "选择" : "Choice";
    public static string FullGraph => Chinese ? "世界线大图" : "Timeline graph";
    public static string ResetView => Chinese ? "重置视图" : "Reset view";
    public static string CompactFullGraph => Chinese ? "大图" : "Large view";
    public static string CompactResetView => Chinese ? "重置" : "Reset";
    public static string JumpHere => Chinese ? "跳到此世界线" : "Jump to this timeline";
    public static string DeleteNode => Chinese ? "删除节点及后续分支" : "Delete node and descendants";
    public static string ConfirmJump => Chinese ? "再次点击确认跳转" : "Click again to confirm jump";
    public static string JumpRootUnavailable => Chinese ? "战斗入口无需回放" : "The combat entry needs no replay";
    public static string GraphHelp => Chinese ? "滚轮缩放 · 拖动空白处平移 · 点击节点查看详情" : "Wheel to zoom · drag the background to pan · click a node for details";
    public static string LargeTreeHint(int shown, int total) => Chinese
        ? $"为保持流畅，当前显示 {shown}/{total} 个节点；完整记录未被合并。"
        : $"Showing {shown}/{total} nodes for performance; the full record remains exact.";
    public static string OmittedActions(int count) => Chinese ? $"… 省略 {count} 步 …" : $"… {count} steps omitted …";
    public static string MoreBranches(int count) => Chinese ? $"另有 {count} 条分支 · 大图查看" : $"{count} more branches · open large view";
    public static string Forwarding(int count) => Chinese ? $"正在从当前位置前进 · {count} 个记录步骤" : $"Advancing from current position · {count} recorded steps";
    public static string ForwardStep(int current, int total, string source) => Chinese
        ? $"正在前进 {current}/{total}：{source}" : $"Advancing {current}/{total}: {source}";
    public static string ForwardReached => Chinese ? "已到达所选世界线。" : "Reached the selected timeline.";
    public static string ForwardFailed(string reason) => Chinese ? $"无法继续前进：{reason}" : $"Could not continue: {reason}";
    public static string ReplayBusy => Chinese ? "已有世界线跳转正在进行。" : "A timeline jump is already in progress.";
    public static string OutcomeVictory => Chinese ? "胜利" : "Win";
    public static string OutcomeDefeat => Chinese ? "失败" : "Loss";
    public static string OutcomeAborted => Chinese ? "已回溯" : "SL";
}

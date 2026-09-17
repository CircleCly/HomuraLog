using Godot;

namespace HomuraLog.UI;

internal static class HomuraText
{
    public static string HandPosition(int position) => Chinese ? $"手牌第 {position} 张" : $"hand #{position} from left";

    private static bool Chinese => TranslationServer.GetLocale().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
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
    public static string FullGraph => Chinese ? "全屏世界线" : "Fullscreen graph";
    public static string ResetView => Chinese ? "重置视图" : "Reset view";
    public static string Fullscreen => Chinese ? "全屏" : "Fullscreen";
    public static string ExitFullscreen => Chinese ? "退出全屏" : "Exit fullscreen";
    public static string JumpHere => Chinese ? "跳到此世界线" : "Jump to this timeline";
    public static string ConfirmJump => Chinese ? "再次点击确认重载并回放" : "Click again to reload and replay";
    public static string JumpRootUnavailable => Chinese ? "战斗入口无需回放" : "The combat entry needs no replay";
    public static string GraphHelp => Chinese ? "滚轮缩放 · 拖动空白处平移 · 点击节点查看详情" : "Wheel to zoom · drag the background to pan · click a node for details";
    public static string LargeTreeHint(int shown, int total) => Chinese
        ? $"为保持流畅，当前显示 {shown}/{total} 个节点；完整记录未被合并。"
        : $"Showing {shown}/{total} nodes for performance; the full record remains exact.";
}

using System.Collections.ObjectModel;

namespace ConvertTools.App.ViewModels;

/// <summary>顶部 TAB 项。Key 用于 View 侧解析对应视图。</summary>
public sealed record NavTab(string Key, string Title);

/// <summary>壳 VM：顶部 TAB 菜单项。convertTools 原有功能后续按 NavTab 追加即可。</summary>
public sealed class ShellViewModel
{
    public ObservableCollection<NavTab> Tabs { get; } = new()
    {
        new NavTab("home", "首页"),
        new NavTab("publish", "素材发布"),
        new NavTab("transcode", "转码"),
        new NavTab("cost_report", "成本报告"),
        new NavTab("project_info", "项目信息"),
        new NavTab("settings", "设置"),
    };
}

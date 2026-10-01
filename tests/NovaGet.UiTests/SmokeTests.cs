using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace NovaGet.UiTests;

/// <summary>Only one NovaGet runs at a time, so the UI tests run one after another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "UI";
}

/// <summary>
/// Section 23 FlaUI smoke tests: the installed-style app (a separate process) found and driven through UI Automation,
/// the way a screen reader or a test robot sees it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SmokeTests
{
    private static readonly string[] Menus = ["Tasks", "File", "Downloads", "View", "Help"];

    private static readonly string[] ToolbarButtons =
    [
        "Add URL", "Resume", "Stop", "Stop All", "Delete", "Delete Completed", "Options", "Scheduler", "Start Queue",
        "Stop Queue", "Grabber", "Tell a Friend",
    ];

    [Fact]
    public void Main_window_shows_menus_toolbar_categories_and_list()
    {
        using var session = AppSession.Start();

        Assert.Equal(Menus, session.MenuBar.Items.Select(i => i.Name));
        foreach (var name in ToolbarButtons)
        {
            Assert.NotNull(session.ToolbarButton(name));
        }

        var tree = session.MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        Assert.Equal("Categories", tree?.Name);
        Assert.Contains(tree!.AsTree().Items, i => i.Name == "All Downloads");
        Assert.NotNull(session.MainWindow.FindFirstDescendant(cf => cf.ByName("Downloads").And(cf.ByControlType(ControlType.DataGrid))));
    }

    [Fact]
    public void Options_open_from_the_toolbar_with_all_ten_tabs()
    {
        using var session = AppSession.Start();

        session.ToolbarButton("Options").Invoke();
        var options = session.WaitForWindow("Options");
        var tabs = options.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tab))?.AsTab() ?? throw new InvalidOperationException("No tabs.");
        Assert.Equal(
            ["General", "File Types", "Save To", "Downloads", "Connection", "Proxy/Socks", "Site Logins", "Dial Up/VPN", "Sounds", "Advanced"],
            tabs.TabItems.Select(t => t.Name));

        foreach (var tab in tabs.TabItems)
        {
            tab.Select();
            Assert.True(tab.IsSelected, tab.Name);
        }

        Button(options, "Cancel").Invoke();
        session.WaitUntilClosed("Options");
    }

    [Fact]
    public void Add_new_download_opens_from_the_Tasks_menu()
    {
        using var session = AppSession.Start();

        session.InvokeMenu("Tasks", "Add new download...");
        var dialog = session.WaitForWindow("Enter new address to download");
        Assert.NotNull(dialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox)));
        Button(dialog, "Cancel").Invoke();
        session.WaitUntilClosed("Enter new address to download");
    }

    [Fact]
    public void Scheduler_and_About_open_and_close()
    {
        using var session = AppSession.Start();

        session.InvokeMenu("Downloads", "Scheduler...");
        var scheduler = session.WaitForWindow("Scheduler");
        Assert.NotNull(scheduler.FindFirstDescendant(cf => cf.ByName("Main download queue")));
        Button(scheduler, "Close").Invoke();
        session.WaitUntilClosed("Scheduler");

        session.InvokeMenu("Help", "About NovaGet");
        var about = session.WaitForWindow("About NovaGet");
        Assert.NotNull(about.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)));
        Button(about, "OK").Invoke();
        session.WaitUntilClosed("About NovaGet");
    }

    [Fact]
    public void Exit_closes_the_app()
    {
        using var session = AppSession.Start();

        session.InvokeMenu("Tasks", "Exit");

        Assert.True(AppSession.WaitForExit(session.App, TimeSpan.FromSeconds(20)), "NovaGet did not exit");
    }

    private static Button Button(Window window, string name) =>
        Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))), TimeSpan.FromSeconds(10), throwOnTimeout: false)
            .Result?.AsButton() ?? throw new InvalidOperationException($"No button '{name}' in '{window.Title}'.");
}

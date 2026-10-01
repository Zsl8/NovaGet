using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace NovaGet.UiTests;

/// <summary>
/// The real NovaGet.exe (the App project's build output) copied to a temp folder with portable.flag, so it keeps its
/// settings and database there, started and driven through UI Automation. Disposing exits it (killing it if needed).
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string> s_appFolder = new(CopyApp);

    private AppSession(Application app, UIA3Automation automation, Window main)
    {
        App = app;
        Automation = automation;
        MainWindow = main;
    }

    public Application App { get; }

    public UIA3Automation Automation { get; }

    public Window MainWindow { get; }

    public static AppSession Start()
    {
        var folder = s_appFolder.Value;
        var app = Application.Launch(new ProcessStartInfo(Path.Combine(folder, "NovaGet.exe")) { WorkingDirectory = folder });
        var automation = new UIA3Automation();
        try
        {
            var main = app.GetMainWindow(automation, s_wait) ?? throw new InvalidOperationException("The main window did not appear.");
            return new AppSession(app, automation, main);
        }
        catch
        {
            automation.Dispose();
            Kill(app);
            throw;
        }
    }

    /// <summary>A window of the app with this title (dialogs are owned top-level windows), waiting for it to appear.</summary>
    public Window WaitForWindow(string title)
    {
        var found = Retry.WhileNull(
            () => App.GetAllTopLevelWindows(Automation).FirstOrDefault(w => w.Title == title),
            s_wait, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
        return found.Result ?? throw new InvalidOperationException(
            $"No window '{title}'; open: {string.Join(", ", App.GetAllTopLevelWindows(Automation).Select(w => $"'{w.Title}'"))}");
    }

    public void WaitUntilClosed(string title)
    {
        var closed = Retry.WhileTrue(
            () => App.GetAllTopLevelWindows(Automation).Any(w => w.Title == title),
            s_wait, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
        Assert.True(closed.Success, $"'{title}' is still open");
    }

    public Menu MenuBar =>
        MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))?.AsMenu() ?? throw new InvalidOperationException("No menu bar.");

    /// <summary>Opens a top-level menu and invokes one of its items (names as shown, without access-key underscores).</summary>
    public void InvokeMenu(string menu, string item)
    {
        var top = MenuBar.Items.First(i => i.Name == menu);
        top.Expand();
        var entry = Retry.WhileNull(() => top.Items.FirstOrDefault(i => i.Name == item), s_wait, throwOnTimeout: false).Result
            ?? throw new InvalidOperationException($"No '{item}' in {menu}: {string.Join(", ", top.Items.Select(i => i.Name))}");
        entry.Invoke();
    }

    public Button ToolbarButton(string name) =>
        MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton()
        ?? throw new InvalidOperationException($"No toolbar button '{name}'.");

    public void Dispose()
    {
        try
        {
            if (!App.HasExited)
            {
                InvokeMenu("Tasks", "Exit");
                App.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(1));
                if (!WaitForExit(App, TimeSpan.FromSeconds(20)))
                {
                    Kill(App);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            Kill(App);
        }
        finally
        {
            Automation.Dispose();
            App.Dispose();
        }
    }

    public static bool WaitForExit(Application app, TimeSpan timeout)
    {
        using var process = Process.GetProcessById(app.ProcessId);
        return process.WaitForExit((int)timeout.TotalMilliseconds);
    }

    private static void Kill(Application app)
    {
        try
        {
            app.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>The App project's output for this configuration, copied once per test run with portable.flag added.</summary>
    private static string CopyApp()
    {
        // tests/NovaGet.UiTests/bin/<Configuration>/<tfm>/ → src/NovaGet.App/bin/<Configuration>/<tfm>/
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = output.Name;
        var configuration = output.Parent!.Name;
        var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var source = Path.Combine(root, "src", "NovaGet.App", "bin", configuration, tfm);
        if (!File.Exists(Path.Combine(source, "NovaGet.exe")))
        {
            throw new FileNotFoundException($"Build NovaGet.App ({configuration}) first: {source}");
        }

        var target = Path.Combine(Path.GetTempPath(), "novaget-ui-tests", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        File.WriteAllText(Path.Combine(target, "portable.flag"), string.Empty);
        return target;
    }
}

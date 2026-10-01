using System.Diagnostics;
using System.Globalization;
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

    /// <summary>
    /// The app's windows: top-level ones, and the dialogs they own (UI Automation lists an owned window under its
    /// owner, not under the desktop).
    /// </summary>
    public IEnumerable<Window> Windows()
    {
        foreach (var window in App.GetAllTopLevelWindows(Automation))
        {
            yield return window;
            foreach (var owned in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)))
            {
                yield return owned.AsWindow();
            }
        }
    }

    /// <summary>A window of the app with this title, waiting for it to appear.</summary>
    public Window WaitForWindow(string title)
    {
        var found = Retry.WhileNull(
            () => Windows().FirstOrDefault(w => w.Title == title),
            s_wait, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
        if (found.Result is { } window)
        {
            return window;
        }

        throw new InvalidOperationException($"No window '{title}'. {Describe()}");
    }

    /// <summary>What the app shows right now (for failures): its windows, their buttons, and a screen capture in CI.</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Process exited: {App.HasExited}. Windows:");
        foreach (var window in Windows())
        {
            var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).Select(b => $"{b.Name}{(b.IsEnabled ? string.Empty : " (disabled)")}");
            text.Append(CultureInfo.InvariantCulture, $" '{window.Title}' [modal: {window.IsModal}; buttons: {string.Join(", ", buttons)}]");
        }

        if (Environment.GetEnvironmentVariable("NOVAGET_SCREENSHOTS") is { Length: > 0 } folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                FlaUI.Core.Capturing.Capture.Screen().ToFile(Path.Combine(folder, $"ui-failure-{DateTime.UtcNow:HHmmssfff}.png"));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                text.Append(CultureInfo.InvariantCulture, $" (screen capture failed: {ex.Message})");
            }
        }

        return text.ToString();
    }

    public void WaitUntilClosed(string title)
    {
        var closed = Retry.WhileTrue(
            () => Windows().Any(w => w.Title == title),
            s_wait, TimeSpan.FromMilliseconds(200), throwOnTimeout: false);
        Assert.True(closed.Success, $"'{title}' is still open");
    }

    /// <summary>The app's menu bar (not the title bar's "System" menu, which is a menu bar too).</summary>
    public Menu MenuBar =>
        MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuBar))
            .FirstOrDefault(m => m.Name != "System" && m.FindFirstChild(cf => cf.ByControlType(ControlType.MenuItem)) is not null)?.AsMenu()
        ?? throw new InvalidOperationException("No menu bar.");

    /// <summary>Opens a top-level menu and invokes one of its items (names as shown, without access-key underscores).</summary>
    public void InvokeMenu(string menu, string item)
    {
        var top = MenuBar.Items.FirstOrDefault(i => i.Name == menu)
            ?? throw new InvalidOperationException($"No menu '{menu}': {string.Join(", ", MenuBar.Items.Select(i => i.Name))}");
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

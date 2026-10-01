using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovaGet.App.Localization;
using NovaGet.App.Services;
using NovaGet.App.ViewModels;
using NovaGet.App.ViewModels.Scheduler;
using NovaGet.App.Views;
using NovaGet.App.Views.Dialogs;
using NovaGet.Core.Abstractions;
using NovaGet.Core.Engine;
using NovaGet.Core.Services;
using NovaGet.Core.Services.Queues;
using NovaGet.Core.Settings;
using Xunit.Abstractions;

namespace NovaGet.App.Tests;

/// <summary>
/// Section 17: the dark theme and Arabic right-to-left layout. Windows are rendered off screen; in the dark theme no
/// control may still paint the light classic look. With NOVAGET_SCREENSHOTS set, the renders are saved there as PNGs.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemeTests(WpfFixture wpf, ITestOutputHelper output)
{
    [Theory]
    [InlineData(AppTheme.Light, false, false, false, false)]
    [InlineData(AppTheme.Light, true, false, false, true)]
    [InlineData(AppTheme.Dark, false, false, true, true)]
    [InlineData(AppTheme.System, false, false, false, false)]
    [InlineData(AppTheme.System, true, false, true, true)]
    [InlineData(AppTheme.Dark, true, true, false, false)]
    public void Theme_follows_the_setting_windows_mode_and_high_contrast(AppTheme theme, bool windowsDark, bool highContrast, bool darkContent, bool darkTitleBars)
    {
        Assert.Equal(darkContent, ThemeService.UseDarkTheme(theme, windowsDark, highContrast));
        Assert.Equal(darkTitleBars, ThemeService.UseDarkTitleBars(theme, windowsDark, highContrast));
    }

    [Fact]
    public void Dark_theme_leaves_no_light_controls()
    {
        using var app = new AppHost();
        app.Get<IDownloadService>().Add(new DownloadRequest { Url = "https://example.com/a.zip", FileName = "a.zip", Size = 4096 });
        var settings = app.Get<ISettingsService>();
        var theme = app.Get<ThemeService>();
        settings.Update(s => s.Ui.Theme = AppTheme.Dark);
        var offenders = new List<string>();
        try
        {
            wpf.Run(() =>
            {
                theme.Apply();
                Assert.True(theme.IsDark);
                foreach (var (name, window) in Windows(app))
                {
                    ShowOffScreen(window);
                    var pages = window is OptionsDialog options ? options.Tabs.Items.Count : 1;
                    for (var page = 0; page < pages; page++)
                    {
                        if (window is OptionsDialog dialog)
                        {
                            dialog.Tabs.SelectedIndex = page;
                            window.UpdateLayout();
                        }

                        var label = pages > 1 ? $"{name}[{page}]" : name;
                        var background = BrightShare(window, window.ActualWidth, window.ActualHeight);
                        output.WriteLine($"{label}: {background:P0} light pixels");
                        if (background > 0.25)
                        {
                            offenders.Add($"{label}: {background:P0} of the window is light");
                        }

                        offenders.AddRange(LightControls(window).Select(c => $"{label}: {c}"));
                        Screenshot(window, $"dark-{label}");
                    }

                    if (window is MainWindow main)
                    {
                        offenders.AddRange(CheckMenu(main));
                    }

                    CloseOrHide(window);
                }
            });
        }
        finally
        {
            settings.Update(s => s.Ui.Theme = AppTheme.Light);
            wpf.Run(theme.Apply);
        }

        Assert.True(offenders.Count == 0, "Still light in the dark theme:" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct()));
    }

    [Fact]
    public void Switching_back_to_light_removes_the_dark_theme()
    {
        using var app = new AppHost();
        var settings = app.Get<ISettingsService>();
        var theme = app.Get<ThemeService>();
        try
        {
            settings.Update(s => s.Ui.Theme = AppTheme.Dark);
            wpf.Run(theme.Apply);
            wpf.Run(() => Assert.True(theme.IsDark));
            settings.Update(s => s.Ui.Theme = AppTheme.Light);
            wpf.Run(theme.Apply);
            wpf.Run(() =>
            {
                Assert.False(theme.IsDark);
                Assert.DoesNotContain(Application.Current.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal) == true);
            });
        }
        finally
        {
            settings.Update(s => s.Ui.Theme = AppTheme.Light);
            wpf.Run(theme.Apply);
        }
    }

    [Fact]
    public void Arabic_windows_load_right_to_left()
    {
        using var app = new AppHost();
        app.Get<IDownloadService>().Add(new DownloadRequest { Url = "https://example.com/a.zip", FileName = "a.zip", Size = 4096 });
        try
        {
            wpf.Run(() =>
            {
                Localizer.Initialize("ar", null);
                Assert.True(Localizer.IsRightToLeft);
                foreach (var (name, window) in Windows(app))
                {
                    ShowOffScreen(window);
                    Assert.Equal(FlowDirection.RightToLeft, window.FlowDirection);
                    Assert.True(window.ActualWidth > 0, name);
                    Screenshot(window, $"ar-{name}");
                    CloseOrHide(window);
                }

                // Every dialog with its Arabic texts (format placeholders included).
                foreach (var name in DialogSmokeTests.Dialogs.Cast<object[]>().Select(d => (string)d[0]))
                {
                    var dialog = DialogSmokeTests.Create(name);
                    ShowOffScreen(dialog);
                    Assert.Equal(FlowDirection.RightToLeft, dialog.FlowDirection);
                    dialog.Close();
                }
            });
        }
        finally
        {
            wpf.Run(() => Localizer.Initialize("en", null));
        }
    }

    [Fact]
    public void Icons_stay_unmirrored_in_right_to_left_windows()
    {
        wpf.Run(() =>
        {
            var image = new Image { Width = 16, Height = 16 };
            var window = new Window { FlowDirection = FlowDirection.RightToLeft, Content = new StackPanel { Children = { image } }, Width = 100, Height = 100 };
            ShowOffScreen(window);
            Assert.Equal(FlowDirection.LeftToRight, image.FlowDirection);
            window.Close();
        });
    }

    /// <summary>The main window, the Options dialog and the scheduler, plus a few download dialogs.</summary>
    private static IEnumerable<(string Name, Window Window)> Windows(AppHost app)
    {
        yield return ("main", app.Get<MainWindow>());
        yield return ("options", new OptionsDialog(app.Get<OptionsService>(), app.Get<SettingsPackageService>(), app.Get<SoundService>(), app.Get<IDialogService>(), app.Paths, _ => { }));
        yield return ("scheduler", new SchedulerWindow(
            new SchedulerViewModel(app.Get<IQueueRepository>(), app.Get<IDownloadService>(), app.Get<IDownloadEngine>(), app.Get<IQueueManager>(),
                app.Get<IAppController>(), app.Get<IDialogService>(), _ => null),
            app.Get<ISettingsService>()));
        foreach (var name in new[] { "FileInfo", "Properties", "AddUrl", "Batch", "GrabberWizard", "StreamQuality", "Complete" })
        {
            yield return (name, DialogSmokeTests.Create(name));
        }
    }

    private static void ShowOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = 0;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
    }

    private static void CloseOrHide(Window window)
    {
        if (window is MainWindow)
        {
            window.Hide(); // the main window only hides
        }
        else
        {
            window.Close();
        }
    }

    /// <summary>Visible controls that are mostly light (text alone never covers half a control).</summary>
    private static IEnumerable<string> LightControls(Window window)
    {
        foreach (var control in Descendants(window).OfType<Control>())
        {
            if (control is Window || !control.IsVisible || control.ActualWidth < 6 || control.ActualHeight < 6 || Descendants(control).OfType<Image>().Any())
            {
                continue;
            }

            var share = BrightShare(control, control.ActualWidth, control.ActualHeight);
            if (share > 0.5)
            {
                var name = string.IsNullOrEmpty(control.Name) ? string.Empty : $" '{control.Name}'";
                yield return $"{control.GetType().Name}{name} ({share:P0} light)";
            }
        }
    }

    /// <summary>The main window's File menu, opened: its popup must be dark too.</summary>
    private List<string> CheckMenu(MainWindow window)
    {
        var menu = Descendants(window).OfType<Menu>().First();
        var file = (MenuItem)menu.Items[0];
        file.IsSubmenuOpen = true;
        window.UpdateLayout();
        var popup = file.Template.FindName("PART_Popup", file) as System.Windows.Controls.Primitives.Popup;
        var results = new List<string>();
        if (popup?.Child is FrameworkElement content)
        {
            content.UpdateLayout();
            var share = BrightShare(content, content.ActualWidth, content.ActualHeight);
            output.WriteLine($"main File menu: {share:P0} light pixels");
            if (share > 0.25)
            {
                results.Add($"main File menu: {share:P0} light");
            }

            Screenshot(content, "dark-main-menu");
        }
        else
        {
            results.Add("main File menu: no popup found");
        }

        file.IsSubmenuOpen = false;
        return results;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static RenderTargetBitmap Render(Visual visual, double width, double height)
    {
        var w = Math.Max(1, (int)Math.Ceiling(width));
        var h = Math.Max(1, (int)Math.Ceiling(height));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var brush = new VisualBrush(visual)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    /// <summary>Share of the opaque pixels that are light (relative luminance above 0.6).</summary>
    private static double BrightShare(Visual visual, double width, double height)
    {
        var bitmap = Render(visual, width, height);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int opaque = 0, bright = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 250)
            {
                continue;
            }

            opaque++;
            var luminance = ((0.0722 * pixels[i]) + (0.7152 * pixels[i + 1]) + (0.2126 * pixels[i + 2])) / 255;
            if (luminance > 0.6)
            {
                bright++;
            }
        }

        return opaque == 0 ? 0 : (double)bright / opaque;
    }

    private static void Screenshot(FrameworkElement element, string name)
    {
        var folder = Environment.GetEnvironmentVariable("NOVAGET_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(element, element.ActualWidth, element.ActualHeight)));
        using var stream = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(stream);
    }
}

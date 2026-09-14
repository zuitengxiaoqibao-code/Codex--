using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Core = CodexBackup.Core;

namespace CodexBackup.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] is "--self-test" or "--scan-report")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exit = await Task.Run(() => Diagnostics.RunAsync(e.Args[0], e.Args[1]));
            Shutdown(exit); return;
        }
        var window = new MainWindow();
        if (e.Args.Length >= 2 && e.Args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            var output = Path.GetFullPath(e.Args[1]);
            try
            {
                window.Show(); window.UpdateLayout();
                var expected = new[] { "WelcomePage", "HomePage", "BackupPage", "RestorePage", "CheckPage", "BusyPanel", "ResultPage", "BackupContentTabs", "SourceSelectionPanel", "SessionSelectionPanel", "CleanupSelectionPanel", "SourcesGrid", "SourceFilterBox", "SourceSearchBox", "SessionsGrid", "SessionFilterBox", "SessionSearchBox", "CleanupGrid", "RunCleanupButton", "BackupFindingsList", "SourceSummaryText", "SessionSummaryText", "CleanupSummaryText" };
                var missing = expected.Where(name => window.FindName(name) is null).ToArray();
                string? screenshot = null;
                string? backupScreenshot = null;
                try
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    screenshot = Path.ChangeExtension(output, ".png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var stream = File.Create(screenshot); encoder.Save(stream);
                }
                catch { screenshot = null; }
                var navigation = new List<string>();
                ((CheckBox)window.FindName("RiskAcknowledgement")).IsChecked = true;
                void Click(string handler, string page)
                {
                    typeof(MainWindow).GetMethod(handler, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                    window.UpdateLayout();
                    if (((FrameworkElement)window.FindName(page)).Visibility != Visibility.Visible) throw new InvalidOperationException("Navigation failed: " + page);
                    navigation.Add(page);
                }
                Click("WelcomeContinue_Click", "HomePage");
                Click("OpenBackup_Click", "BackupPage");
                window.UpdateLayout();
                var tabs = (TabControl)window.FindName("BackupContentTabs");
                tabs.SelectedIndex = 1; window.UpdateLayout();
                if (((FrameworkElement)window.FindName("SourceSelectionPanel")).Visibility != Visibility.Visible) throw new InvalidOperationException("Source type tab failed");
                tabs.SelectedIndex = 7; window.UpdateLayout();
                if (((FrameworkElement)window.FindName("CleanupSelectionPanel")).Visibility != Visibility.Visible) throw new InvalidOperationException("Cleanup tab failed");
                tabs.SelectedIndex = 0; window.UpdateLayout();
                if (((FrameworkElement)window.FindName("SessionSelectionPanel")).Visibility != Visibility.Visible) throw new InvalidOperationException("Session tab failed");
                var bindingFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var sourceItems = (ObservableCollection<Core.SourceItem>)typeof(MainWindow).GetField("sources", bindingFlags)!.GetValue(window)!;
                var sessionItems = (ObservableCollection<SessionGroupRow>)typeof(MainWindow).GetField("sessionRows", bindingFlags)!.GetValue(window)!;
                sourceItems.Clear(); sessionItems.Clear();
                sourceItems.Add(new Core.SourceItem { Id = "core", Kind = Core.SourceKind.Core, Path = @"C:\synthetic\core", Required = true, Selected = true, IsDirectory = true });
                sourceItems.Add(new Core.SourceItem { Id = "project", Kind = Core.SourceKind.Project, Path = @"C:\synthetic\project", Selected = true, IsDirectory = true });
                var stressReferences = Enumerable.Range(0, 1000).Select(i => new Core.SessionReference { Id = "stress-" + i, CorePath = @"C:\synthetic\core", ProjectPath = @"C:\synthetic\project", TranscriptPath = @"C:\synthetic\core\sessions\" + i + ".jsonl", Selected = true }).ToList();
                foreach (var row in SessionGroupRow.Create(stressReferences)) sessionItems.Add(row);
                typeof(MainWindow).GetMethod("RebuildSelectionCoordinator", bindingFlags)!.Invoke(window, null);
                var setSelection = typeof(MainWindow).GetMethod("SetSessionSelection", bindingFlags)!;
                var allRows = sessionItems.ToList();
                var batchTimer = Stopwatch.StartNew();
                setSelection.Invoke(window, [allRows, false, true]);
                setSelection.Invoke(window, [allRows, true, true]);
                batchTimer.Stop();
                var singleTimer = Stopwatch.StartNew();
                setSelection.Invoke(window, [new List<SessionGroupRow> { allRows[0] }, false, true]);
                setSelection.Invoke(window, [new List<SessionGroupRow> { allRows[0] }, true, true]);
                singleTimer.Stop();
                var selectionResponsive = batchTimer.Elapsed < TimeSpan.FromSeconds(2) && singleTimer.Elapsed < TimeSpan.FromMilliseconds(500);
                sourceItems.Clear(); sessionItems.Clear();
                try
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    backupScreenshot = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".backup.png");
                    using var stream = File.Create(backupScreenshot); encoder.Save(stream);
                }
                catch { backupScreenshot = null; }
                Click("BackHome_Click", "HomePage");
                Click("OpenRestore_Click", "RestorePage");
                Click("BackHome_Click", "HomePage");
                Click("OpenCheck_Click", "CheckPage");
                var report = new { initialized = true, namedControlsValid = missing.Length == 0, missing, screenshot, backupScreenshot, navigation, selectionResponsive, selectionStress = new { sessions = 1000, batchMilliseconds = batchTimer.Elapsed.TotalMilliseconds, singleMilliseconds = singleTimer.Elapsed.TotalMilliseconds }, limitation = "验证窗口、静态渲染、入口导航和千条会话选择响应；不代替文件选择对话框、完整向导及新系统人工验收。" };
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(missing.Length == 0 && selectionResponsive ? 0 : 2);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { initialized = false, error = ex.GetType().Name, message = ex.Message }));
                Shutdown(1);
            }
            return;
        }
        window.Show();
    }
}

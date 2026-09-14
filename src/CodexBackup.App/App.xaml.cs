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
                var expected = new[] { "WelcomePage", "HomePage", "BackupPage", "RestorePage", "CheckPage", "BusyPanel", "ResultPage", "AdvancedScanExpander", "AdvancedBackupExpander", "ManualSelectionExpander", "RecommendedSelectionSummaryText", "OfficialAuditSummaryText", "BackupContentTabs", "SourceSelectionPanel", "SessionSelectionPanel", "ProjectSessionSelectionPanel", "ProjectSessionGroupsList", "ProjectSessionFilterBox", "ProjectSessionSearchBox", "IndividualSessionSelectionPanel", "CleanupSelectionPanel", "SourcesGrid", "SourceFilterBox", "SourceSearchBox", "SessionsGrid", "SessionFilterBox", "SessionSearchBox", "CleanupGrid", "RunCleanupButton", "BackupFindingsList", "SourceSummaryText", "SessionSummaryText", "CleanupSummaryText" };
                var missing = expected.Where(name => window.FindName(name) is null).ToArray();
                var advancedSectionsCollapsed = new[] { "AdvancedScanExpander", "AdvancedBackupExpander", "ManualSelectionExpander" }
                    .All(name => window.FindName(name) is Expander { IsExpanded: false });
                string? screenshot = null;
                string? backupScreenshot = null;
                string? manualScreenshot = null;
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
                var projectSessionViewDefault = window.FindName("ProjectSessionSelectionPanel") is FrameworkElement { Visibility: Visibility.Visible }
                    && window.FindName("IndividualSessionSelectionPanel") is FrameworkElement { Visibility: Visibility.Collapsed };
                try
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    backupScreenshot = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".backup.png");
                    using var stream = File.Create(backupScreenshot); encoder.Save(stream);
                }
                catch { backupScreenshot = null; }
                ((Expander)window.FindName("ManualSelectionExpander")).IsExpanded = true;
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
                typeof(MainWindow).GetField("scan", bindingFlags)!.SetValue(window, new Core.ScanResult
                {
                    Items = sourceItems.ToList(),
                    Sessions = stressReferences,
                    Findings =
                    [
                        new(Core.FindingLevel.Warning, "official-unsupported-config", "synthetic unsupported"),
                        new(Core.FindingLevel.Warning, "official-deprecated-config", "synthetic deprecated")
                    ]
                });
                typeof(MainWindow).GetMethod("RebuildSelectionCoordinator", bindingFlags)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("RebuildProjectSessionGroups", bindingFlags)!.Invoke(window, null);
                var projectGroups = (ObservableCollection<ProjectSessionGroupRow>)typeof(MainWindow).GetField("projectSessionGroups", bindingFlags)!.GetValue(window)!;
                var projectGroupingValid = projectGroups.Count == 1 && projectGroups[0].SessionCount == 1000 && projectGroups[0].SelectionState == true;
                typeof(MainWindow).GetMethod("UpdateSimpleSummaries", bindingFlags)!.Invoke(window, null);
                var simpleSummaryValid = ((TextBlock)window.FindName("RecommendedSelectionSummaryText")).Text.Contains("1000 / 1000", StringComparison.Ordinal)
                    && ((TextBlock)window.FindName("OfficialAuditSummaryText")).Text.Contains("已不支持 1 项", StringComparison.Ordinal)
                    && ((TextBlock)window.FindName("OfficialAuditSummaryText")).Text.Contains("不会自动删除或改写", StringComparison.Ordinal);
                var setSelection = typeof(MainWindow).GetMethod("SetSessionSelection", bindingFlags)!;
                var sessionSelectionClick = typeof(MainWindow).GetMethod("SessionSelection_Click", bindingFlags)!;
                var sourceSelectionClick = typeof(MainWindow).GetMethod("SourceSelection_Click", bindingFlags)!;
                var coverageCounter = typeof(MainWindow).GetField("coverageEvaluationCount", bindingFlags)!;
                var allRows = sessionItems.ToList();
                var coverageBeforeSelection = (int)coverageCounter.GetValue(window)!;
                var projectSelectionClick = typeof(MainWindow).GetMethod("ProjectSessionSelection_Click", bindingFlags)!;
                var projectTimer = Stopwatch.StartNew();
                var projectCheckBox = new CheckBox { DataContext = projectGroups[0], IsChecked = true };
                projectSelectionClick.Invoke(window, [projectCheckBox, new RoutedEventArgs()]);
                projectCheckBox.IsChecked = false;
                projectSelectionClick.Invoke(window, [projectCheckBox, new RoutedEventArgs()]);
                projectTimer.Stop();
                var batchTimer = Stopwatch.StartNew();
                setSelection.Invoke(window, [allRows, false, true]);
                setSelection.Invoke(window, [allRows, true, true]);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                batchTimer.Stop();
                var selectionCheckBox = new CheckBox { DataContext = allRows[0], IsChecked = false };
                var singleTimer = Stopwatch.StartNew();
                sessionSelectionClick.Invoke(window, [selectionCheckBox, new RoutedEventArgs()]);
                selectionCheckBox.IsChecked = true;
                sessionSelectionClick.Invoke(window, [selectionCheckBox, new RoutedEventArgs()]);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                singleTimer.Stop();
                var sourceCheckBox = new CheckBox { DataContext = sourceItems[1], IsChecked = false };
                var sourceTimer = Stopwatch.StartNew();
                sourceSelectionClick.Invoke(window, [sourceCheckBox, new RoutedEventArgs()]);
                sourceCheckBox.IsChecked = true;
                sourceSelectionClick.Invoke(window, [sourceCheckBox, new RoutedEventArgs()]);
                setSelection.Invoke(window, [allRows, true, true]);
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                sourceTimer.Stop();
                var selectionResponsive = batchTimer.Elapsed < TimeSpan.FromSeconds(2)
                    && projectTimer.Elapsed < TimeSpan.FromSeconds(2)
                    && singleTimer.Elapsed < TimeSpan.FromMilliseconds(500)
                    && sourceTimer.Elapsed < TimeSpan.FromSeconds(1);
                var selectionScheduledCoverage = (int)coverageCounter.GetValue(window)! != coverageBeforeSelection;
                try
                {
                    ((FrameworkElement)window.FindName("ProjectSessionGroupsList")).BringIntoView();
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    manualScreenshot = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + ".manual.png");
                    using var stream = File.Create(manualScreenshot); encoder.Save(stream);
                }
                catch { manualScreenshot = null; }
                sourceItems.Clear(); sessionItems.Clear();
                Click("BackHome_Click", "HomePage");
                Click("OpenRestore_Click", "RestorePage");
                Click("BackHome_Click", "HomePage");
                Click("OpenCheck_Click", "CheckPage");
                var report = new { initialized = true, namedControlsValid = missing.Length == 0, advancedSectionsCollapsed, projectSessionViewDefault, projectGroupingValid, simpleSummaryValid, missing, screenshot, backupScreenshot, manualScreenshot, navigation, selectionResponsive, selectionScheduledCoverage, selectionStress = new { sessions = 1000, projectGroupMilliseconds = projectTimer.Elapsed.TotalMilliseconds, batchMilliseconds = batchTimer.Elapsed.TotalMilliseconds, singleMilliseconds = singleTimer.Elapsed.TotalMilliseconds, sharedSourceMilliseconds = sourceTimer.Elapsed.TotalMilliseconds }, limitation = "验证窗口、静态渲染、入口导航、项目目录分组和千条会话选择响应；不代替文件选择对话框、完整向导及新系统人工验收。" };
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(missing.Length == 0 && advancedSectionsCollapsed && projectSessionViewDefault && projectGroupingValid && simpleSummaryValid && selectionResponsive && !selectionScheduledCoverage ? 0 : 2);
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

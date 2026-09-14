using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexBackup.Core;

namespace CodexBackup.App;

public partial class MainWindow : Window
{
    private readonly DiscoveryService discovery = new();
    private readonly BackupEngine backupEngine = new();
    private readonly PackageVerifier verifier = new();
    private readonly RestoreEngine restoreEngine = new();
    private readonly ObservableCollection<SourceItem> sources = [];
    private readonly ObservableCollection<FindingGroup> findingGroups = [];
    private readonly ObservableCollection<SessionGroupRow> sessionRows = [];
    private readonly ObservableCollection<CleanupCandidate> cleanupCandidates = [];
    private readonly ObservableCollection<MappingRow> mappings = [];
    private readonly ICollectionView sourcesView;
    private readonly ICollectionView sessionView;
    private readonly ICollectionView cleanupView;
    private CancellationTokenSource? operationCts;
    private ScanResult? scan;
    private VerifiedPackage? verifiedPackage;
    private RestorePreview? restorePreview;
    private string? previewFingerprint;
    private string? resultPath;
    private string? lastVerifiedBackupPath;
    private readonly List<string> additionalRoots = [];
    private readonly Dictionary<string,string> pathReplacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,bool> baseRequired = [];
    private readonly Dictionary<string, SourceItem> sourcesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionGroupRow> sessionRowsById = new(StringComparer.OrdinalIgnoreCase);
    private SelectionCoordinator? selectionCoordinator;
    private bool syncingSelection;
    private int coverageEvaluationCount;

    public MainWindow()
    {
        InitializeComponent();
        sourcesView = CollectionViewSource.GetDefaultView(sources);
        sourcesView.Filter = SourceFilter;
        sourcesView.SortDescriptions.Add(new SortDescription(nameof(SourceItem.PriorityRank), ListSortDirection.Ascending));
        sourcesView.SortDescriptions.Add(new SortDescription(nameof(SourceItem.Kind), ListSortDirection.Ascending));
        sourcesView.SortDescriptions.Add(new SortDescription(nameof(SourceItem.Name), ListSortDirection.Ascending));
        SourcesGrid.ItemsSource = sourcesView;
        sessionView = CollectionViewSource.GetDefaultView(sessionRows);
        sessionView.Filter = SessionFilter;
        SessionsGrid.ItemsSource = sessionView;
        cleanupView = CollectionViewSource.GetDefaultView(cleanupCandidates);
        CleanupGrid.ItemsSource = cleanupView;
        BackupFindingsList.ItemsSource = findingGroups;
        MappingsGrid.ItemsSource = mappings;
        ProfilePathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        BackupDestinationBox.Text = "";
        MappingsGrid.CellEditEnding += (_, _) => { restorePreview = null; previewFingerprint = null; };
        Closing += Window_Closing;
    }

    private void ShowPage(FrameworkElement page)
    {
        foreach (var candidate in new[] { WelcomePage, HomePage, BackupPage, RestorePage, CheckPage, ResultPage }) candidate.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void WelcomeContinue_Click(object sender, RoutedEventArgs e)
    {
        if (RiskAcknowledgement.IsChecked != true) { MessageBox.Show(this, "请先确认已阅读风险与验收边界。", "需要确认", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ShowPage(HomePage);
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e) => ShowPage(BackupPage);
    private void OpenRestore_Click(object sender, RoutedEventArgs e) => ShowPage(RestorePage);
    private void OpenCheck_Click(object sender, RoutedEventArgs e) => ShowPage(CheckPage);
    private void BackHome_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    private void SourceFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sourcesView is null) return;
        sourcesView.Refresh(); UpdateSourceSummary();
    }

    private bool SourceFilter(object value)
    {
        if (value is not SourceItem item) return false;
        var search = SourceSearchBox?.Text?.Trim() ?? "";
        if (search.Length > 0 && !string.Join(" ", item.Name, item.Path, item.Reason, item.DiscoveredBy).Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        var statusMatches = SourceFilterBox?.SelectedIndex switch
        {
            1 => item.Required,
            2 => item.Selected,
            3 => !item.Exists,
            4 => item.HasProblem,
            _ => true
        };
        if (!statusMatches) return false;
        return BackupContentTabs?.SelectedIndex switch
        {
            1 => item.Kind == SourceKind.Project,
            2 => item.Kind == SourceKind.Memory,
            3 => item.Kind is SourceKind.Core or SourceKind.Session or SourceKind.Environment,
            4 => item.Kind == SourceKind.Skill,
            5 => item.Kind is SourceKind.Plugin or SourceKind.Tool,
            6 => item.Kind is SourceKind.Application or SourceKind.Custom,
            _ => false
        };
    }

    private void BackupContentTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceSelectionPanel is null || SessionSelectionPanel is null || CleanupSelectionPanel is null || sourcesView is null) return;
        var index = BackupContentTabs.SelectedIndex;
        SessionSelectionPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanupSelectionPanel.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        SourceSelectionPanel.Visibility = index is >= 1 and <= 6 ? Visibility.Visible : Visibility.Collapsed;
        if (SourceSelectionPanel.Visibility == Visibility.Visible) sourcesView.Refresh();
        UpdateSourceSummary();
    }

    private void SessionFilterChanged(object sender, RoutedEventArgs e)
    {
        if (sessionView is null) return;
        sessionView.Refresh();
        UpdateSessionSummary();
    }

    private bool SessionFilter(object value)
    {
        if (value is not SessionGroupRow row) return false;
        var search = SessionSearchBox?.Text?.Trim() ?? "";
        if (search.Length > 0 && !string.Join(" ", row.Id, row.Title, row.ProjectPath, row.TranscriptPath).Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        return SessionFilterBox?.SelectedIndex switch
        {
            1 => row.IsActive,
            2 => row.IsArchived,
            3 => row.HasUnknownLifecycle,
            4 => row.HasMissingLink,
            5 => row.HasArchivedResidue,
            6 => row.Selected,
            _ => true
        };
    }

    private void SelectVisibleSessions_Click(object sender, RoutedEventArgs e) => SetVisibleSessions(true);
    private void ClearVisibleSessions_Click(object sender, RoutedEventArgs e) => SetVisibleSessions(false);
    private void SelectAllActiveSessions_Click(object sender, RoutedEventArgs e) => SetSessionsBy(row => row.IsActive, true);
    private void SelectAllArchivedSessions_Click(object sender, RoutedEventArgs e) => SetSessionsBy(row => row.IsArchived, true);
    private void SelectCompleteSessions_Click(object sender, RoutedEventArgs e) => SetSessionsBy(row => !row.HasMissingLink, true);
    private void SelectProblemSessions_Click(object sender, RoutedEventArgs e) => SetSessionsBy(row => row.HasMissingLink || row.HasArchivedResidue, true);

    private void SelectVisibleSources_Click(object sender, RoutedEventArgs e) => SetVisibleSources(true);
    private void ClearVisibleSources_Click(object sender, RoutedEventArgs e) => SetVisibleSources(false);
    private void SelectRequiredSources_Click(object sender, RoutedEventArgs e)
    {
        foreach (var source in sources) source.Selected = source.Required;
        ReconcileSessionsAfterSourceChange();
        RefreshSelectionUi();
    }
    private void SelectProjectSources_Click(object sender, RoutedEventArgs e) => SetSourcesBy(source => source.Kind is SourceKind.Project or SourceKind.Session or SourceKind.Memory, true);

    private void SelectCleanupCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in cleanupCandidates.Where(candidate => candidate.SafeToQuarantine && !candidate.ContainsSource)) candidate.Selected = true;
        cleanupView.Refresh(); UpdateCleanupSummary();
    }

    private void ClearCleanupCandidates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var candidate in cleanupCandidates) candidate.Selected = false;
        cleanupView.Refresh(); UpdateCleanupSummary();
    }

    private void SetVisibleSources(bool selected)
    {
        foreach (var source in sourcesView.Cast<object>().OfType<SourceItem>())
            if (selected || !source.Required) source.Selected = selected;
        ReconcileSessionsAfterSourceChange();
        RefreshSelectionUi();
    }

    private void SetSourcesBy(Func<SourceItem, bool> predicate, bool selected)
    {
        foreach (var source in sources.Where(predicate))
            if (selected || !source.Required) source.Selected = selected;
        ReconcileSessionsAfterSourceChange();
        RefreshSelectionUi();
    }

    private void SetVisibleSessions(bool selected)
    {
        var visible = sessionView.Cast<object>().OfType<SessionGroupRow>().ToList();
        SetSessionSelection(visible, selected);
    }

    private void SetSessionsBy(Func<SessionGroupRow, bool> predicate, bool selected)
    {
        SetSessionSelection(sessionRows.Where(predicate).ToList(), selected);
    }

    private void SessionSelection_Click(object sender, RoutedEventArgs e)
    {
        if (syncingSelection || sender is not CheckBox { DataContext: SessionGroupRow row } checkBox) return;
        SetSessionSelection([row], checkBox.IsChecked == true);
    }

    private void SourceSelection_Click(object sender, RoutedEventArgs e)
    {
        if (syncingSelection || sender is not CheckBox { DataContext: SourceItem item } checkBox) return;
        if (item.Required)
        {
            item.Selected = true;
            checkBox.IsChecked = true;
            return;
        }
        item.Selected = checkBox.IsChecked == true;
        if (!item.Selected)
        {
            var relatedRows = (selectionCoordinator?.SessionsForSource(item.Id) ?? [])
                .Select(id => sessionRowsById.GetValueOrDefault(id))
                .Where(row => row is { Selected: true })
                .Cast<SessionGroupRow>()
                .ToList();
            SetSessionSelection(relatedRows, false, false);
        }
        RefreshSelectionUi();
    }

    private void CleanupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: CleanupCandidate candidate } checkBox) return;
        if (!candidate.SafeToQuarantine)
        {
            candidate.Selected = false;
            checkBox.IsChecked = false;
            return;
        }
        candidate.Selected = checkBox.IsChecked == true;
        UpdateCleanupSummary();
    }

    private void SetSessionSelection(IReadOnlyCollection<SessionGroupRow> rows, bool selected, bool refresh = true)
    {
        if (syncingSelection) return;
        syncingSelection = true;
        try
        {
            foreach (var row in rows)
            {
                row.Selected = selected;
                foreach (var reference in row.References) reference.Selected = selected;
                selectionCoordinator?.ApplySessionSelection(row.Id, selected);
            }
        }
        finally { syncingSelection = false; }
        if (refresh) RefreshSelectionUi();
    }

    private void RebuildSelectionCoordinator()
    {
        sourcesById.Clear();
        foreach (var source in sources) sourcesById[source.Id] = source;
        sessionRowsById.Clear();
        foreach (var row in sessionRows) sessionRowsById[row.Id] = row;
        selectionCoordinator = SelectionCoordinator.Build(
            sessionRows.Select(row => new SessionSelectionGroup(row.Id, row.References)),
            sources,
            pathReplacements);
    }

    private void ReconcileSessionsAfterSourceChange()
    {
        if (selectionCoordinator is null) return;
        syncingSelection = true;
        try
        {
            foreach (var row in sessionRows.Where(row => row.Selected).ToList())
            {
                var missing = selectionCoordinator.SourcesForSession(row.Id)
                    .Select(id => sourcesById.GetValueOrDefault(id))
                    .Any(source => source is { Selected: false });
                if (!missing) continue;
                row.Selected = false;
                foreach (var reference in row.References) reference.Selected = false;
            }
            selectionCoordinator.Reconcile(sessionRows.Where(row => row.Selected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        finally { syncingSelection = false; }
    }

    private void RefreshSelectionUi()
    {
        if (SourceFilterBox?.SelectedIndex == 2) sourcesView.Refresh();
        if (SessionFilterBox?.SelectedIndex == 6) sessionView.Refresh();
        UpdateSourceSummary();
        UpdateSessionSummary();
        UpdateTabHeaders();
        MarkCoveragePending();
    }

    private void MarkCoveragePending()
    {
        if (scan is null || CoverageSummaryText is null || PreflightStatusText is null) return;
        CoverageSummaryText.Text = $"已选择 {sessionRows.Count(row => row.Selected)} / {sessionRows.Count} 个独立会话。选择已记录；这里不会扫描磁盘。点击“重新检查是否齐全”或“开始备份”时再统一核对。";
        PreflightStatusText.Text = "重装判定：选择已更改，等待重新检查";
    }

    private void UpdateSourceSummary()
    {
        if (SourceSummaryText is null) return;
        var visible = sourcesView.Cast<object>().OfType<SourceItem>().ToList();
        SourceSummaryText.Text = $"显示 {visible.Count} / {sources.Count} 项；必须备份 {sources.Count(x => x.Required)} 项；找不到 {sources.Count(x => !x.Exists)} 项。双击或筛选不会改变备份范围，只有“保存”勾选会改变范围。";
    }

    private void UpdateSessionSummary()
    {
        if (SessionSummaryText is null) return;
        var visible = sessionView.Cast<object>().OfType<SessionGroupRow>().ToList();
        SessionSummaryText.Text = $"显示 {visible.Count} / {sessionRows.Count} 个独立会话；已选择 {sessionRows.Count(x => x.Selected)} 个；活动 {sessionRows.Count(x => x.IsActive)}，归档 {sessionRows.Count(x => x.IsArchived)}，状态未知 {sessionRows.Count(x => x.HasUnknownLifecycle)}。"
            + (sessionRows.Any(x => x.HasArchivedResidue) ? " 紫色提示表示归档会话仍保留项目文件。" : "");
    }

    private void UpdateCleanupSummary()
    {
        if (CleanupSummaryText is null) return;
        var safe = cleanupCandidates.Count(candidate => candidate.SafeToQuarantine);
        var blocked = cleanupCandidates.Count - safe;
        var selected = cleanupCandidates.Count(candidate => candidate.Selected && candidate.SafeToQuarantine);
        var ready = cleanupCandidates.Count(candidate => candidate.Selected && candidate.CanQuarantine);
        var projects = cleanupCandidates.Count(candidate => candidate.ContainsSource);
        var generated = cleanupCandidates.Count - projects;
        CleanupSummaryText.Text = $"发现 {projects} 个完整归档项目和 {generated} 个可重建目录；可安排 {safe} 个，活动会话仍在使用 {blocked} 个，已安排 {selected} 个，其中 {ready} 个已随本次备份校验。项目根目录属于红色高风险项，必须逐项选择；会话主数据和记忆库不会成为清理候选。";
        if (RunCleanupButton is not null) RunCleanupButton.IsEnabled = !string.IsNullOrWhiteSpace(lastVerifiedBackupPath) && ready > 0;
        UpdateTabHeaders();
    }

    private void UpdateTabHeaders()
    {
        if (SessionsTab is null) return;
        static string Count(IEnumerable<SourceItem> values)
        {
            var list = values.ToList();
            return $"{list.Count(item => item.Selected)}/{list.Count}";
        }
        SessionsTab.Header = $"会话 {sessionRows.Count(row => row.Selected)}/{sessionRows.Count}";
        ProjectsTab.Header = "项目 " + Count(sources.Where(item => item.Kind == SourceKind.Project));
        MemoriesTab.Header = "记忆 " + Count(sources.Where(item => item.Kind == SourceKind.Memory));
        PersonalDataTab.Header = "会话数据/配置 " + Count(sources.Where(item => item.Kind is SourceKind.Core or SourceKind.Session or SourceKind.Environment));
        SkillsTab.Header = "技能 " + Count(sources.Where(item => item.Kind == SourceKind.Skill));
        ToolsTab.Header = "插件工具 " + Count(sources.Where(item => item.Kind is SourceKind.Plugin or SourceKind.Tool));
        OtherTab.Header = "其他 " + Count(sources.Where(item => item.Kind is SourceKind.Application or SourceKind.Custom));
        CleanupTab.Header = $"清理 {cleanupCandidates.Count(candidate => candidate.Selected)}/{cleanupCandidates.Count}";
    }

    private static string? PickFolder(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void BrowseProfile_Click(object sender, RoutedEventArgs e) { var path = PickFolder("选择要扫描的用户配置目录", ProfilePathBox.Text); if (path is not null) ProfilePathBox.Text = path; }
    private void BrowseBackupDestination_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择备份保存目录", BackupDestinationBox.Text); if (path is null) return;
        BackupDestinationBox.Text = path;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            var targetDisks = WindowsEnvironment.GetDiskNumbers(path);
            var overlap = sources.Where(s => s.Selected && s.Exists).Any(s => WindowsEnvironment.GetDiskNumbers(s.Path).Intersect(targetDisks).Any());
            DestinationInfoText.Text = WindowsEnvironment.DescribeVolume(path) + (targetDisks.Count == 0 ? "。物理磁盘关系未知，请核对外置介质。" : overlap ? "。注意：与至少一个来源共用物理硬盘，不能防护该硬盘损坏。" : "。已识别目标磁盘；请保留独立副本。") + " 加密保护状态未确认。";
        }
        catch { DestinationInfoText.Text = "无法读取目标卷信息；备份预检会阻止不支持或空间不足的目标。请自行确认物理介质。"; }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var profile = ProfilePathBox.Text.Trim();
        if (!Directory.Exists(profile)) { MessageBox.Show(this, "用户配置目录不存在或不可访问。", "无法扫描", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        await RunBusyAsync("正在扫描 Codex 数据位置…", async (progress, ct) =>
        {
            scan = await Task.Run(() => discovery.ScanAsync(profile, progress, ct, additionalRoots.ToArray()), ct);
            var discoveredCleanup = await Task.Run(() => CleanupService.FindCandidates(scan.Sessions, ct), ct);
            lastVerifiedBackupPath = null;
            RunCleanupButton.IsEnabled = false;
            sources.Clear(); foreach (var item in scan.Items) sources.Add(item);
            baseRequired.Clear(); foreach (var item in sources) baseRequired[item.Id] = item.Required;
            foreach (var replacement in pathReplacements.Values.Distinct(StringComparer.OrdinalIgnoreCase)) if (Directory.Exists(replacement) || File.Exists(replacement)) AddManual(replacement, Directory.Exists(replacement));
            sessionRows.Clear();
            foreach (var group in SessionGroupRow.Create(scan.Sessions)) sessionRows.Add(group);
            RebuildSelectionCoordinator();
            cleanupCandidates.Clear();
            foreach (var candidate in discoveredCleanup) cleanupCandidates.Add(candidate);
            UpdateCleanupSummary();
            sessionView.Refresh();
            ApplyBackupMode(true); RefreshCoverage();
        });
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("添加自定义备份文件夹"); if (path is null) return;
        AddManual(path, true);
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "添加自定义备份文件", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog() == true) AddManual(dialog.FileName, false);
    }

    private void AddManual(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        if (sources.Any(x => string.Equals(Path.GetFullPath(x.Path), full, StringComparison.OrdinalIgnoreCase))) return;
        sources.Add(new SourceItem { Name = Path.GetFileName(full), Path = full, Kind = SourceKind.Custom, Selected = true, Exists = true, IsDirectory = directory, Reason = "用户手动加入", DiscoveredBy = "手动选择", LastModifiedUtc = directory ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full) });
        RebuildSelectionCoordinator();
        sourcesView.Refresh(); UpdateSourceSummary();
    }

    private void AddScanLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择实际数据目录，或包含它的上级目录（可以是 D、E 等任意本地盘）");
        if (path is null) return;
        if (!additionalRoots.Contains(path, StringComparer.OrdinalIgnoreCase)) additionalRoots.Add(path);
        AdditionalRootsText.Text = "额外扫描位置：" + string.Join("；", additionalRoots);
        Scan_Click(sender, e);
    }
    private void ClearScanLocations_Click(object sender, RoutedEventArgs e)
    {
        additionalRoots.Clear(); AdditionalRootsText.Text = "已清除额外位置；自动配置扫描仍覆盖各个本地盘符。"; Scan_Click(sender, e);
    }
    private void BackupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourcesGrid is null) return;
        ApplyBackupMode(); RefreshCoverage();
    }
    private void EncryptionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EncryptionPasswordBox is null) return;
        EncryptionPasswordBox.Visibility = EncryptionModeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (EncryptionModeBox.SelectedIndex != 1) EncryptionPasswordBox.Clear();
    }
    private void ApplyBackupMode(bool resetOptionalDefaults = false)
    {
        foreach (var item in sources)
        {
            var relocated = pathReplacements.ContainsKey(item.Path);
            item.Required = !relocated && BackupScopePolicy.MustPreserve(item, baseRequired.GetValueOrDefault(item.Id));
            if (relocated) item.Selected = false;
            else if (item.Required) item.Selected = true;
            else if (resetOptionalDefaults) item.Selected = BackupScopePolicy.SelectByDefault(item, baseRequired.GetValueOrDefault(item.Id));
            item.Reason = relocated ? "原位置已搬走，将从指定的新位置保存，并在恢复时重连路径"
                : item.Required && item.Kind == SourceKind.Core ? "会话索引、对话正文和个人设置必须保存；程序、顶层日志、缓存、临时运行状态和登录令牌自动排除"
                : item.Required ? "重装后无法自动重建，必须保存"
                : !item.Exists ? "原文件未找到；可选项不会阻止迁移"
                : BackupScopePolicy.Explanation(item);
        }
        RebuildSelectionCoordinator();
        sourcesView.Refresh();
        UpdateTabHeaders();
    }
    private BackupRequest CurrentBackupRequest(bool includePreflight = true)
    {
        var request = new BackupRequest
        {
            Sources = sources.ToList(), DestinationDirectory = BackupDestinationBox.Text.Trim(), CompleteMigration = BackupModeBox.SelectedIndex == 0,
            Sessions = scan?.Sessions.Where(s => s.Selected).ToList() ?? [], DiscoveredSessionCount = scan?.SessionAssociationCount ?? 0, DiscoveryFindings = scan?.Findings.ToList() ?? [],
            CoverageNotes = scan?.Findings.Select(UserGuidance.Explain).ToList() ?? [], PathReplacements = new(pathReplacements, StringComparer.OrdinalIgnoreCase),
            SourceCodexVersion = scan?.CodexVersion ?? "未知", EnvironmentManifest = scan?.EnvironmentManifest ?? new(),
            EncryptionPassword = EncryptionModeBox.SelectedIndex == 1 ? EncryptionPasswordBox.Password : null
        };
        if (includePreflight && scan is not null) request.Preflight = PreflightReport.Build(scan, request);
        return request;
    }
    private void RefreshCoverage()
    {
        coverageEvaluationCount++;
        if (scan is null || CoverageSummaryText is null) return;
        var request = CurrentBackupRequest(false);
        var gaps = MigrationCoverage.Evaluate(request);
        var drives = sources.Where(s => s.Exists).Select(s => Path.GetPathRoot(s.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
        var preflight = PreflightReport.Build(scan, request, gaps);
        CoverageSummaryText.Text = $"独立会话 {scan.UniqueSessionCount} 个，已选择 {sessionRows.Count(x => x.Selected)} 个；关联记录 {scan.SessionAssociationCount} 条；项目位置 {scan.ProjectLocationCount} 个。涉及磁盘：{string.Join("、", drives)}。\n" +
            (BackupModeBox.SelectedIndex != 0 ? "当前是自选 / 抢救模式，结果不会标记为完整迁移。" : gaps.Count == 0 ? "本次扫描的会话与项目已选齐。备份时还会按真实文件清单再次核对。" : $"还有 {gaps.Count} 项需要处理，暂不能制作完整迁移包。");
        PreflightStatusText.Text = preflight.Status switch
        {
            PreflightStatus.Ready => "重装判定：可以重装（仍需完成备份包校验和新系统人工验收）",
            PreflightStatus.Blocked => $"重装判定：需要处理后再重装（还有 {gaps.Count} 项必须处理）",
            _ => "重装判定：仅可抢救（当前选择允许部分保存，不能保证完整迁移）"
        };
        findingGroups.Clear();
        var visibleFindings = gaps.Concat(scan.Findings.Where(f => f.Code != "known-location-missing" && (!f.Code.Contains("missing") || BackupModeBox.SelectedIndex != 0)))
            .DistinctBy(f => (f.Code, f.Path)).Take(300).ToList();
        foreach (var group in FindingGroup.Create(visibleFindings)) findingGroups.Add(group);
        if (gaps.Count > 300) findingGroups.Add(FindingGroup.ForOverflow(gaps.Count - 300));
        TechnicalDetailsBox.Text = string.Join(Environment.NewLine, scan.Findings.Concat(gaps).Select(f => $"{f.Code}: {f.Message} {f.Path}"));
        UpdateSourceSummary(); UpdateSessionSummary();
    }
    private void CheckCoverage_Click(object sender, RoutedEventArgs e)
    {
        SourcesGrid.CommitEdit(DataGridEditingUnit.Cell, true); SourcesGrid.CommitEdit(DataGridEditingUnit.Row, true); RefreshCoverage();
    }
    private void LocateMissing_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesGrid.SelectedItem is not SourceItem item) { MessageBox.Show(this, "请先在来源列表中点选原来的项目或会话位置。", "定位原文件"); return; }
        string? path;
        if (item.IsDirectory) path = PickFolder("选择这个项目或数据目录现在的位置");
        else { var dialog = new OpenFileDialog { Title = "选择原会话文件现在的位置", CheckFileExists = true }; path = dialog.ShowDialog() == true ? dialog.FileName : null; }
        if (path is null) return;
        if (path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "新旧位置相同。请重新扫描确认文件是否可读取。"); return; }
        pathReplacements[item.Path] = path; AddManual(path, Directory.Exists(path));
        var located = sources.First(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); located.Kind = item.Kind;
        if (Directory.Exists(path) && !additionalRoots.Contains(path, StringComparer.OrdinalIgnoreCase)) { additionalRoots.Add(path); AdditionalRootsText.Text = "额外扫描位置：" + string.Join("；", additionalRoots); Scan_Click(sender, e); }
        else { ApplyBackupMode(); RefreshCoverage(); }
    }

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        SourcesGrid.CommitEdit(DataGridEditingUnit.Cell, true); SourcesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (scan is null) { MessageBox.Show(this, "请先完成扫描。", "缺少扫描结果"); return; }
        RefreshCoverage();
        var request = CurrentBackupRequest();
        if (MigrationCoverage.Evaluate(request).Count > 0) { MessageBox.Show(this, "会话与源码还没有核对齐全。请先处理列表里的缺失位置或扫描问题；完整迁移不会跳过这些内容。", "暂不能完整备份", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (EncryptionModeBox.SelectedIndex == 1 && (string.IsNullOrWhiteSpace(EncryptionPasswordBox.Password) || EncryptionPasswordBox.Password.Length < 8)) { MessageBox.Show(this, "密码保护需要至少 8 个字符。密码遗失后无法恢复，请妥善保存。", "密码不完整", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (BackupReviewCheck.IsChecked != true) { MessageBox.Show(this, "请确认备份包的保存位置，以及是否使用密码保护。", "需要确认"); return; }
        var destination = BackupDestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination)) { MessageBox.Show(this, "请选择备份保存目录。", "缺少目标"); return; }
        if (IsCurrentProcessLocation(destination)) { MessageBox.Show(this, "保存目录与本程序运行目录重叠，目标含义不明确。请选择独立目录。", "路径冲突", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, $"将备份 {sources.Count(s => s.Selected)} 项，另有 {sources.Count(s => !s.Selected)} 项未独立选择。\n\n目标：{destination}\n\n精确空间、文件类型和运行程序将在写入前再次检查。备份包含原始配置，可能含凭据。确认开始？", "最终备份确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBusyAsync("正在创建并逐文件校验备份…", async (progress, ct) =>
        {
            var result = await Task.Run(() => backupEngine.BackupAsync(request, progress, ct), ct);
            lastVerifiedBackupPath = result.PackagePath;
            foreach (var candidate in cleanupCandidates)
                candidate.IncludedInVerifiedBackup = result.Manifest.Roots.Any(root => root.IsDirectory && SafeContains(root.OriginalPath, candidate.CandidatePath));
            cleanupView.Refresh(); UpdateCleanupSummary();
            RunCleanupButton.IsEnabled = cleanupCandidates.Any(candidate => candidate.Selected && candidate.CanQuarantine);
            var readyCleanup = cleanupCandidates.Count(candidate => candidate.Selected && candidate.CanQuarantine);
            var resultNotes = result.Manifest.CoverageNotes.ToList();
            if (readyCleanup > 0) resultNotes.Insert(0, $"已安排 {readyCleanup} 个归档项目的可重建目录。需要清理时返回“备份”→“清理”页签，再点击“执行已选隔离清理”；程序不会自动永久删除。 ");
            ShowResult(result.Manifest.CompleteMigration ? "会话与项目迁移包已创建" : "自选 / 抢救备份已创建（不是完整迁移）", $"文件：{result.Manifest.FileCount:N0}，大小：{FormatBytes(result.Manifest.TotalBytes)}，会话关联：{result.Manifest.Sessions.Count}。文件与关联检查不代替新系统上登录和项目运行验收。", result.PackagePath, resultNotes);
        });
    }

    private async void RunCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(lastVerifiedBackupPath) || (!Directory.Exists(lastVerifiedBackupPath) && !File.Exists(lastVerifiedBackupPath)))
        {
            MessageBox.Show(this, "必须先在本次运行中完成备份并通过校验，才能隔离清理候选。", "尚未完成备份校验", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var selected = cleanupCandidates.Where(candidate => candidate.Selected && candidate.CanQuarantine).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "尚未选择可隔离的清理候选。", "没有清理项"); return; }
        if (CleanupConfirmCheck.IsChecked != true)
        {
            MessageBox.Show(this, "请先确认这些目录只会移动到同盘隔离区，并记住隔离日志位置。", "需要确认", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var details = string.Join("\n", selected.Take(8).Select(candidate => candidate.CandidatePath));
        if (selected.Count > 8) details += $"\n另有 {selected.Count - 8} 项";
        if (MessageBox.Show(this, $"将把以下可重建目录移动到同盘 .codex-backup-quarantine 隔离区，不会永久删除：\n\n{details}\n\n确认执行？", "隔离清理确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBusyAsync("正在隔离已选构建产物…", async (_, ct) =>
        {
            var result = await Task.Run(() => CleanupService.QuarantineAsync(selected, ct), ct);
            var remaining = await Task.Run(() => CleanupService.FindCandidates(scan!.Sessions, ct), ct);
            cleanupCandidates.Clear();
            foreach (var candidate in remaining) cleanupCandidates.Add(candidate);
            UpdateCleanupSummary();
            var message = $"已隔离 {result.QuarantinedPaths.Count} 项；失败 {result.FailedPaths.Count} 项。\n隔离日志：{result.JournalPath}";
            if (result.FailedPaths.Count > 0) message += "\n\n" + string.Join("\n", result.FailedPaths.Take(6));
            MessageBox.Show(this, message, result.FailedPaths.Count == 0 ? "隔离完成" : "部分隔离完成", MessageBoxButton.OK, result.FailedPaths.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private static bool SafeContains(string parent, string child)
    {
        try { return PathSafety.Contains(parent, child); } catch (Exception ex) when (ex is BackupException or ArgumentException) { return false; }
    }

    private async void RestoreCleanup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择隔离清理日志", Filter = "清理日志 (cleanup-journal.json)|cleanup-journal.json|JSON 日志 (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show(this, "程序只会还原日志中仍在隔离区的目录；若原位置已经存在，将停止且不会覆盖。确认继续？", "还原隔离目录", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await RunBusyAsync("正在按隔离日志还原…", async (_, ct) =>
        {
            var restored = await Task.Run(() => CleanupService.RestoreAsync(dialog.FileName, ct), ct);
            MessageBox.Show(this, $"已还原 {restored.Count} 个目录。请重新扫描并检查项目。", "还原完成", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void ChooseRestorePackage_Click(object sender, RoutedEventArgs e)
    {
        string? path = null;
        var fileDialog = new OpenFileDialog { Title = "选择密码保护备份文件（取消后可选择普通备份目录）", Filter = "密码保护备份 (*.codexenc)|*.codexenc|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (fileDialog.ShowDialog() == true) path = fileDialog.FileName;
        else path = PickFolder("选择包含 COMPLETE.json 的普通备份包目录");
        if (path is null) return;
        RestorePackageBox.Text = path;
        if (EncryptedPackage.IsEncryptedFile(path) && string.IsNullOrWhiteSpace(RestoreEncryptionPasswordBox.Password))
        {
            verifiedPackage = null; RestorePreviewList.Items.Clear();
            RestoreCoverageText.Text = "这是密码保护备份包。请输入密码后点击“验证”；密码错误或遗失时不会写入目标。";
            return;
        }
        await VerifySelectedRestorePackageAsync();
    }

    private async void VerifyRestorePackage_Click(object sender, RoutedEventArgs e) => await VerifySelectedRestorePackageAsync();

    private async Task VerifySelectedRestorePackageAsync()
    {
        var path = RestorePackageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) { MessageBox.Show(this, "请先选择备份包文件或目录。", "缺少备份包"); return; }
        var encrypted = EncryptedPackage.IsEncryptedFile(path);
        if (encrypted && string.IsNullOrWhiteSpace(RestoreEncryptionPasswordBox.Password)) { MessageBox.Show(this, "这是密码保护备份包，请先输入密码。", "需要密码"); return; }
        await RunBusyAsync("正在完整校验备份包…", async (progress, ct) =>
        {
            var package = encrypted
                ? await Task.Run(() => verifier.VerifyAsync(path, RestoreEncryptionPasswordBox.Password, progress, ct), ct)
                : await Task.Run(() => verifier.VerifyAsync(path, progress, ct), ct);
            EncryptedPackage.CleanupExtractedPackage(package.PackagePath);
            verifiedPackage = encrypted ? package with { PackagePath = path } : package;
            mappings.Clear();
            var isolatedBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex-Restored", verifiedPackage.Manifest.BackupId);
            foreach (var root in verifiedPackage.Manifest.Roots)
                mappings.Add(new MappingRow(root.Id, root.OriginalPath, Path.Combine(isolatedBase, SafeName(root.Name, root.Id) + "-" + root.Id[..8]), root.Kind));
            var firstCore = mappings.FirstOrDefault(x => x.Kind == SourceKind.Core); if (firstCore is not null) firstCore.IsPrimary = true;
            PrimaryCoreList.ItemsSource = mappings.Where(x => x.Kind == SourceKind.Core).ToList();
            SourceCodexVersionText.Text = verifiedPackage.Manifest.SourceCodexVersion;
            TargetCodexVersionBox.Text = "";
            restorePreview = null; previewFingerprint = null; RestorePreviewList.Items.Clear();
            RestorePreviewList.Items.Add($"备份包完整性通过：{verifiedPackage.Manifest.FileCount:N0} 个文件，{FormatBytes(verifiedPackage.Manifest.TotalBytes)}。");
            RestorePreviewList.Items.Add("尚未执行恢复预演；文件校验不代表应用层验收通过。");
            RestoreCoverageText.Text = verifiedPackage.Manifest.CompleteMigration ? $"这是完整迁移包，含 {verifiedPackage.Manifest.Sessions.Count} 条会话关联（独立会话数需结合清单核对）。请选择原布局或新的文件夹，同时恢复会话和源码。" : "这是旧版或自选 / 抢救包，缺少完整关联证明。可以解出文件，但不能保证会话对应源码齐全；建议回原电脑用新版重新备份。";
            RequireCompleteCheck.IsChecked = verifiedPackage.Manifest.CompleteMigration;
            IsolatedCheck.IsChecked = true;
        });
    }

    private void RestoreOptionChanged(object sender, RoutedEventArgs e) { ClearRestorePreview("恢复选项已更改，请重新预演。"); }
    private void PrimaryCore_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: MappingRow selected }) return;
        foreach (var row in mappings.Where(x => x.Kind == SourceKind.Core)) row.IsPrimary = row == selected;
        MappingsGrid.Items.Refresh();
        ClearRestorePreview("主会话数据目录已更改，请重新预演；其他会话数据目录会单独保存，不会合并数据库。");
    }

    private void MapCoreToRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (verifiedPackage is null) { MessageBox.Show(this, "请先选择并验证备份包。", "缺少备份包"); return; }
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var allCore = mappings.Where(x => x.Kind == SourceKind.Core).ToList();
        if (allCore.Count == 0) { MessageBox.Show(this, "此备份包不含 Codex 会话主数据目录。", "没有会话主数据"); return; }
        var selectedCore = allCore.Where(x => x.Selected).ToList();
        if (allCore.Count > 1 && selectedCore.Count != 1)
        {
            MessageBox.Show(this, "备份包包含多个 Codex 会话数据目录。请在表格中只勾选一个主目录，再执行本机映射。", "需要明确选择", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var core = allCore.Count == 1 ? allCore[0] : selectedCore[0];
        core.Selected = true; foreach (var row in allCore) row.IsPrimary = row == core;
        try { core.TargetPath = RuntimeCodexHome(); }
        catch (Exception ex) when (ex is BackupException or ArgumentException or NotSupportedException)
        { MessageBox.Show(this, "本机 CODEX_HOME 不是有效的绝对本地路径，请检查环境变量或手动填写。", "无法自动映射", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        MappingsGrid.Items.Refresh();
        PrimaryCoreList.Items.Refresh();
        IsolatedCheck.IsChecked = false;
        ClearRestorePreview("已将所选核心映射到本机 Codex 数据目录。其他根仍使用表格中的隔离目标；配置、凭据、技能、插件和自动化不会自动启用，恢复后必须重新登录并审核。");
    }

    private static string RuntimeCodexHome()
    {
        foreach (var value in new[]
        {
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.Machine)
        })
            if (!string.IsNullOrWhiteSpace(value)) return PathSafety.Full(value);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private void ClearRestorePreview(string message)
    {
        restorePreview = null; previewFingerprint = null;
        if (RestorePreviewList is not null) { RestorePreviewList.Items.Clear(); RestorePreviewList.Items.Add(message); }
    }

    private RestoreRequest BuildRestoreRequest() => new()
    {
        PackagePath = RestorePackageBox.Text,
        Isolated = IsolatedCheck.IsChecked == true,
        ReplaceExisting = ReplaceExistingCheck.IsChecked == true,
        RequireCompleteMigration = RequireCompleteCheck.IsChecked == true,
        Mappings = mappings.Where(x => x.Selected).Select(x => new RestoreMapping { RootId = x.RootId, TargetPath = x.TargetPath.Trim() }).ToList(),
        PrimaryCoreRootId = mappings.FirstOrDefault(x => x.Selected && x.Kind == SourceKind.Core && x.IsPrimary)?.RootId,
        TargetCodexVersion = string.IsNullOrWhiteSpace(TargetCodexVersionBox.Text) || TargetCodexVersionBox.Text.Trim().Equals("未知", StringComparison.OrdinalIgnoreCase) ? null : TargetCodexVersionBox.Text.Trim(),
        EncryptionPassword = EncryptedPackage.IsEncryptedFile(RestorePackageBox.Text) ? RestoreEncryptionPasswordBox.Password : null
    };

    private void SetPlannedLayout(string? newBase, bool original)
    {
        if (verifiedPackage is null) { MessageBox.Show(this, "请先选择备份包。"); return; }
        try
        {
            var planned = RestorePlanner.CreateMappings(verifiedPackage.Manifest, newBase, RuntimeCodexHome(), original);
            mappings.Clear();
            foreach (var mapping in planned) { var root = verifiedPackage.Manifest.Roots.Single(r => r.Id == mapping.RootId); mappings.Add(new MappingRow(root.Id, root.OriginalPath, mapping.TargetPath, root.Kind) { IsPrimary = root.Kind == SourceKind.Core && mapping.RootId == planned.FirstOrDefault(x => verifiedPackage.Manifest.Roots.Single(r => r.Id == x.RootId).Kind == SourceKind.Core)?.RootId }); }
            PrimaryCoreList.ItemsSource = mappings.Where(x => x.Kind == SourceKind.Core).ToList();
            IsolatedCheck.IsChecked = false;
            RequireCompleteCheck.IsChecked = true;
            ClearRestorePreview("已一起设置会话、源码和记忆的位置。请检查路径；文件已存在时需要明确启用替换，随后重新预演。");
            if (verifiedPackage.Manifest.Roots.Count(r => r.Kind == SourceKind.Core) > 1) RestorePreviewList.Items.Add("备份中有多套 Codex 数据。第一套映射到当前程序目录，其他套保存在独立数据目录；不会合并会话数据库。需要使用其他套时，请在该数据目录下单独配置 CODEX_HOME。请核对表格中哪一套是你的主数据。");
        }
        catch (Exception ex) { MessageBox.Show(this, UserGuidance.ExplainException(ex), "无法设置恢复位置"); }
    }
    private void OriginalLayout_Click(object sender, RoutedEventArgs e) => SetPlannedLayout(null, true);
    private void NewLayout_Click(object sender, RoutedEventArgs e) { var folder = PickFolder("选择新系统保存项目和数据的总目录"); if (folder is not null) SetPlannedLayout(folder, false); }
    private void IsolatedLayout_Click(object sender, RoutedEventArgs e)
    {
        if (verifiedPackage is null) { MessageBox.Show(this, "请先选择备份包。"); return; }
        var isolatedBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex-Restored", verifiedPackage.Manifest.BackupId);
        mappings.Clear(); foreach (var root in verifiedPackage.Manifest.Roots) mappings.Add(new MappingRow(root.Id, root.OriginalPath, Path.Combine(isolatedBase, SafeName(root.Name, root.Id) + "-" + root.Id[..8]), root.Kind));
        PrimaryCoreList.ItemsSource = mappings.Where(x => x.Kind == SourceKind.Core).ToList();
        IsolatedCheck.IsChecked = true; RequireCompleteCheck.IsChecked = false; ClearRestorePreview("此操作只解出文件供核对，不接入 Codex，不作为可直接使用的迁移结果。");
    }

    private async void PreviewRestore_Click(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (verifiedPackage is null) { MessageBox.Show(this, "请先选择并验证备份包。", "缺少备份包"); return; }
        if (!mappings.Any(x => x.Selected)) { MessageBox.Show(this, "请至少勾选一个要恢复的根。", "尚未选择"); return; }
        if (mappings.Any(x => x.Selected && string.IsNullOrWhiteSpace(x.TargetPath))) { MessageBox.Show(this, "每个已选来源都必须设置恢复目标。", "映射不完整"); return; }
        var request = BuildRestoreRequest();
        await RunBusyAsync("正在预演恢复路径与冲突…", async (_, ct) =>
        {
            restorePreview = await Task.Run(() => restoreEngine.PreviewAsync(request, ct), ct);
            previewFingerprint = System.Text.Json.JsonSerializer.Serialize(request);
            RestorePreviewList.Items.Clear();
            foreach (var item in restorePreview.Items) RestorePreviewList.Items.Add($"{item.SourceName} → {item.TargetPath} · {item.Action}");
            foreach (var finding in restorePreview.Findings) RestorePreviewList.Items.Add(FormatFinding(finding));
            RestorePreviewList.Items.Add(restorePreview.CanProceed ? "预演未发现阻断项；仍需确认后执行。" : "存在阻断项，不能执行恢复。");
        });
    }

    private async void ExecuteRestore_Click(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true); MappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!mappings.Any(x => x.Selected) || mappings.Any(x => x.Selected && string.IsNullOrWhiteSpace(x.TargetPath))) { MessageBox.Show(this, "请至少选择一个根，并为所有已选根设置恢复目标。", "映射不完整"); return; }
        var request = BuildRestoreRequest();
        if (restorePreview is null) { MessageBox.Show(this, "请先执行恢复预演。", "需要预演"); return; }
        if (previewFingerprint != System.Text.Json.JsonSerializer.Serialize(request)) { restorePreview = null; MessageBox.Show(this, "恢复路径或选项已经变化，请重新预演。", "需要重新预演"); return; }
        if (!restorePreview.CanProceed) { MessageBox.Show(this, "预演存在阻断项，不能恢复。", "已阻断", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (ReplaceExistingCheck.IsChecked == true && MessageBox.Show(this, "替换已有内容会修改目标，并创建回滚日志。确认按当前映射执行？", "确认替换", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(this, "即将按预演结果写入文件。请确认 Codex 及相关工具均已关闭。", "执行恢复", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBusyAsync("正在恢复并写入回滚日志…", async (progress, ct) =>
        {
            var result = await Task.Run(() => restoreEngine.RestoreAsync(request, progress, ct), ct);
            var acceptance = result.Acceptance;
            var status = acceptance?.StructuralStatus == RestoreStructuralStatus.Passed ? "结构验收通过" : "结构验收未通过，先处理阻断项";
            var details = result.Notes.ToList();
            if (acceptance is not null)
            {
                details.Add($"结构验收：{status}；应用验收：待人工检查（不会自动登录、启用插件或运行项目）。");
                details.AddRange(acceptance.Checks.Select(c => $"[{c.Level}] {c.Message}"));
                details.AddRange(acceptance.DisabledIntegrations.Select(x => $"已保存但保持停用：{x.SourcePath}；人工处理：{x.ManualReviewAction}"));
            }
            ShowResult("恢复文件已写入，等待人工验收", $"已恢复 {result.RestoredPaths.Count} 个根路径。{status}。回滚日志已保存；请启动 Codex 手动检查登录、会话、项目、技能与记忆。", result.JournalPath, details);
        });
    }

    private async void CheckEnvironment_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("正在只读扫描当前环境…", async (progress, ct) =>
        {
            var result = await Task.Run(() => discovery.ScanAsync(null, progress, ct), ct);
            CheckResultsList.Items.Clear(); CheckResultsList.Items.Add($"发现 {result.Items.Count} 个来源；独立会话 {result.UniqueSessionCount} 个，关联记录 {result.SessionAssociationCount} 条，项目位置 {result.ProjectLocationCount} 个；Codex 版本：{result.CodexVersion}。覆盖范围外的内容仍为未知。");
            foreach (var finding in result.Findings) CheckResultsList.Items.Add(FormatFinding(finding));
            var artifacts = TemporaryArtifactManager.List(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            if (artifacts.Count > 0) CheckResultsList.Items.Add($"发现 {artifacts.Count} 个恢复辅助目录或未完成备份；未通过验收前不要删除，可在技术记录中逐项核对。");
        });
    }

    private async void VerifyPackage_Click(object sender, RoutedEventArgs e)
    {
        string? path = null;
        var fileDialog = new OpenFileDialog { Title = "选择密码保护备份文件（取消后可选择普通备份目录）", Filter = "密码保护备份 (*.codexenc)|*.codexenc|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (fileDialog.ShowDialog() == true) path = fileDialog.FileName; else path = PickFolder("选择要完整校验的普通备份包");
        if (path is null) return; CheckPackageBox.Text = path;
        if (EncryptedPackage.IsEncryptedFile(path) && string.IsNullOrWhiteSpace(RestoreEncryptionPasswordBox.Password)) { CheckResultsList.Items.Clear(); CheckResultsList.Items.Add("这是密码保护备份。请在恢复页输入密码，再回到这里重新检查。"); return; }
        await RunBusyAsync("正在读取清单并校验全部载荷…", async (progress, ct) =>
        {
            var package = EncryptedPackage.IsEncryptedFile(path)
                ? await Task.Run(() => verifier.VerifyAsync(path, RestoreEncryptionPasswordBox.Password, progress, ct), ct)
                : await Task.Run(() => verifier.VerifyAsync(path, progress, ct), ct);
            EncryptedPackage.CleanupExtractedPackage(package.PackagePath);
            CheckResultsList.Items.Clear();
            CheckResultsList.Items.Add($"文件完整性校验通过：{package.Manifest.FileCount:N0} 个文件，{FormatBytes(package.Manifest.TotalBytes)}。");
            CheckResultsList.Items.Add(package.Manifest.CompleteMigration ? $"清单包含 {package.Manifest.Sessions.Count} 个会话与项目关联，已核对对应文件存在。" : "该包不是经新版核对的完整迁移包，不能证明源码与会话齐全。");
            CheckResultsList.Items.Add("Codex 应用层验收仍待在目标系统手动完成。");
        });
    }

    private void AcceptancePending_Click(object sender, RoutedEventArgs e)
    {
        CheckResultsList.Items.Add("待人工验收：启动 Codex，确认登录、会话、项目打开、技能加载、记忆读取和外围工具连接。此项尚未通过。");
    }

    private void ChooseJournal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择恢复回滚日志", Filter = "JSON 日志 (*.json)|*.json|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog() == true) JournalPathBox.Text = dialog.FileName;
    }

    private async void RunRollback_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(JournalPathBox.Text)) { MessageBox.Show(this, "请选择有效的回滚日志。", "日志无效"); return; }
        if (RollbackConfirmCheck.IsChecked != true) { MessageBox.Show(this, "请先确认回滚的修改风险。", "需要确认"); return; }
        if (MessageBox.Show(this, "回滚会按日志修改当前文件。确认继续？", "最终确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var journal = JournalPathBox.Text;
        await RunBusyAsync("正在按日志回滚…", async (_, ct) =>
        {
            await Task.Run(() => restoreEngine.RollbackAsync(journal, ct), ct);
            ShowResult("回滚操作已完成", "已完成日志中的回滚步骤。请检查目标路径与 Codex 运行状态。", Path.GetDirectoryName(journal), []);
        });
    }

    private async Task RunBusyAsync(string message, Func<IProgress<OperationProgress>, CancellationToken, Task> action)
    {
        if (operationCts is not null) return;
        operationCts = new CancellationTokenSource(); BusyText.Text = message; BusyStats.Text = ""; BusyPanel.Visibility = Visibility.Visible;
        foreach (var page in new[] { WelcomePage, HomePage, BackupPage, RestorePage, CheckPage, ResultPage }) page.IsEnabled = false;
        var progress = new Progress<OperationProgress>(p => { BusyText.Text = p.Message; BusyStats.Text = p.TotalBytes is > 0 ? $"{p.Files:N0} 个文件 · {FormatBytes(p.Bytes)} / {FormatBytes(p.TotalBytes.Value)}" : $"{p.Phase} · {p.Files:N0} 个文件"; });
        try { await action(progress, operationCts.Token); StatusText.Text = "操作结束"; }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消；未完成结果不会被视为成功"; MessageBox.Show(this, "操作已取消。备份中的未完成目录不能用于正式恢复；恢复过程中已完成的步骤请通过日志检查或回滚。", "已取消", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { StatusText.Text = "操作失败"; MessageBox.Show(this, UserMessage(ex), "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { operationCts.Dispose(); operationCts = null; BusyPanel.Visibility = Visibility.Collapsed; foreach (var page in new[] { WelcomePage, HomePage, BackupPage, RestorePage, CheckPage, ResultPage }) page.IsEnabled = true; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { BusyText.Text = "正在请求安全取消…"; operationCts?.Cancel(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (operationCts is null) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "操作仍在进行。要请求安全取消吗？窗口会保持打开，直到操作响应取消。", "操作进行中", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) operationCts.Cancel();
    }

    private void ShowResult(string title, string summary, string? path, IEnumerable<string> details)
    {
        resultPath = path; ResultTitle.Text = title; ResultSummary.Text = summary; ResultDetails.Items.Clear(); foreach (var detail in details) ResultDetails.Items.Add(detail);
        OpenReportButton.IsEnabled = !string.IsNullOrWhiteSpace(path); ShowPage(ResultPage);
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(resultPath)) return;
        var target = Directory.Exists(resultPath) ? resultPath : Path.GetDirectoryName(resultPath);
        if (target is not null && Directory.Exists(target)) Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
    }

    private static bool IsCurrentProcessLocation(string path)
    {
        try
        {
            var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var app = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return target.StartsWith(app, StringComparison.OrdinalIgnoreCase) || app.StartsWith(target, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static string SafeName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars(); var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FormatFinding(Finding f) => UserGuidance.Explain(f);
    private static string FormatBytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N2} GiB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N2} MiB" : $"{bytes / 1024d:N1} KiB";
    private static string UserMessage(Exception ex) => UserGuidance.ExplainException(ex);
}

public sealed class SessionGroupRow : INotifyPropertyChanged
{
    private bool selected;

    private SessionGroupRow(string id, IReadOnlyList<SessionReference> references)
    {
        Id = id;
        References = references;
        selected = references.All(reference => reference.Selected);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public IReadOnlyList<SessionReference> References { get; }
    public string Title => References.Select(reference => reference.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? "未命名会话";
    public string ProjectPath => JoinPaths(References.Select(reference => reference.ProjectPath));
    public string TranscriptPath => JoinPaths(References.Select(reference => reference.TranscriptPath));
    public int AssociationCount => References.Count;
    public DateTimeOffset? LastActivityUtc => References.Max(reference => reference.LastActivityUtc);
    public bool IsActive => References.Any(reference => reference.Lifecycle == SessionLifecycle.Active);
    public bool IsArchived => !IsActive && References.Any(reference => reference.Lifecycle == SessionLifecycle.Archived);
    public bool HasUnknownLifecycle => References.Any(reference => reference.Lifecycle == SessionLifecycle.Unknown) || IsActive && IsArchived;
    public bool HasMissingLink => References.Any(reference => reference.HasMissingProject || reference.HasMissingTranscript);
    public bool HasArchivedResidue => References.Any(reference => reference.HasProjectResidue);
    public string LifecycleText => IsActive && IsArchived ? "活动 + 归档" : IsActive ? "活动目录" : IsArchived ? "已归档" : "状态未知";
    public string StatusText => HasMissingLink ? "缺少关联文件" : HasArchivedResidue ? "归档会话仍保留项目" : "关联完整";
    public string LinkText => HasMissingLink ? "请补齐项目或对话文件" : HasArchivedResidue ? "项目文件仍在本机，可一并保存" : "会话与项目已关联";
    public Brush LifecycleBrush => IsActive ? Brushes.ForestGreen : IsArchived ? Brushes.MediumPurple : Brushes.DimGray;
    public Brush StatusBrush => HasMissingLink ? Brushes.Firebrick : HasArchivedResidue ? Brushes.MediumPurple : Brushes.ForestGreen;
    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
        }
    }

    public static IEnumerable<SessionGroupRow> Create(IEnumerable<SessionReference> references) => references
        .GroupBy(reference => string.IsNullOrWhiteSpace(reference.Id) ? reference.TranscriptPath : reference.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => new SessionGroupRow(group.Key, group.ToList()))
        .OrderByDescending(row => row.LastActivityUtc ?? DateTimeOffset.MinValue)
        .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase);

    private static string JoinPaths(IEnumerable<string> paths)
    {
        var values = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return values.Count <= 2 ? string.Join("\n", values) : string.Join("\n", values.Take(2)) + $"\n（另有 {values.Count - 2} 个位置）";
    }
}

public sealed class FindingRow
{
    public FindingRow(Finding finding)
    {
        Finding = finding;
        PriorityText = finding.Level switch { FindingLevel.Blocker => "必须先处理", FindingLevel.Warning => "需要核对", _ => "说明" };
        Category = CategoryFor(finding.Code);
        Reason = finding.Message;
        Impact = finding.Level switch
        {
            FindingLevel.Blocker => "会阻止完整迁移，当前结果不能支持重装。",
            FindingLevel.Warning => "可能漏掉部分内容，需要在来源列表中确认。",
            _ => "不会阻止备份，但恢复后需要按说明复核。"
        };
        Action = UserGuidance.Explain(finding).Split("\n处理：", StringSplitOptions.None).LastOrDefault() ?? "按位置和说明处理后重新检查。";
    }

    public Finding Finding { get; }
    public FindingLevel Level => Finding.Level;
    public string PriorityText { get; }
    public string Category { get; }
    public string Reason { get; }
    public string Impact { get; }
    public string Action { get; }
    public string Path => Finding.Path ?? "";
    public Brush AccentBrush => Level switch { FindingLevel.Blocker => Brushes.Firebrick, FindingLevel.Warning => Brushes.DarkGoldenrod, _ => Brushes.RoyalBlue };

    private static string CategoryFor(string code) => code switch
    {
        var value when value.Contains("session", StringComparison.OrdinalIgnoreCase) || value.Contains("project", StringComparison.OrdinalIgnoreCase) => "会话与项目关联",
        var value when value.Contains("config", StringComparison.OrdinalIgnoreCase) || value.Contains("path", StringComparison.OrdinalIgnoreCase) || value.Contains("sqlite", StringComparison.OrdinalIgnoreCase) => "配置与路径",
        var value when value.Contains("active") || value.Contains("wal", StringComparison.OrdinalIgnoreCase) || value.Contains("writer", StringComparison.OrdinalIgnoreCase) => "运行状态",
        var value when value.Contains("git", StringComparison.OrdinalIgnoreCase) => "项目版本依赖",
        var value when value.Contains("msix", StringComparison.OrdinalIgnoreCase) || value.Contains("version", StringComparison.OrdinalIgnoreCase) => "安装与版本",
        var value when value.Contains("coverage", StringComparison.OrdinalIgnoreCase) || value.Contains("boundary", StringComparison.OrdinalIgnoreCase) => "扫描边界",
        _ => "其他检查"
    };
}

public sealed class FindingGroup
{
    private FindingGroup(FindingLevel level, string category, IReadOnlyList<FindingRow> items, string? customHeader = null)
    {
        Level = level;
        Category = category;
        Items = items;
        Header = customHeader ?? $"{LevelText} · {category}（{items.Count}）";
    }

    public FindingLevel Level { get; }
    public string Category { get; }
    public IReadOnlyList<FindingRow> Items { get; }
    public string Header { get; }
    public string LevelText => Level switch { FindingLevel.Blocker => "必须先处理", FindingLevel.Warning => "需要核对", _ => "说明" };
    public Brush AccentBrush => Level switch { FindingLevel.Blocker => Brushes.Firebrick, FindingLevel.Warning => Brushes.DarkGoldenrod, _ => Brushes.RoyalBlue };

    public static IEnumerable<FindingGroup> Create(IEnumerable<Finding> findings) => findings
        .Select(finding => new FindingRow(finding))
        .GroupBy(row => (row.Level, row.Category))
        .OrderBy(group => group.Key.Level switch { FindingLevel.Blocker => 0, FindingLevel.Warning => 1, _ => 2 })
        .ThenBy(group => group.Key.Category, StringComparer.Ordinal)
        .Select(group => new FindingGroup(group.Key.Level, group.Key.Category, group.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToList()));

    public static FindingGroup ForOverflow(int count) => new(FindingLevel.Warning, "扫描边界", [new FindingRow(new Finding(FindingLevel.Warning, "finding-overflow", $"还有 {count} 项检查未在摘要中展开，请使用筛选或技术记录查看。"))], "需要核对 · 还有未展开的检查");
}

public sealed class MappingRow
{
    public MappingRow(string rootId, string originalPath, string targetPath, SourceKind kind)
    {
        RootId = rootId; OriginalPath = originalPath; TargetPath = targetPath; Kind = kind;
    }
    public string RootId { get; }
    public string OriginalPath { get; }
    public string TargetPath { get; set; }
    public SourceKind Kind { get; }
    public string KindText => SourceKindChineseConverter.ToChinese(Kind);
    public string Display => $"{KindText} · {OriginalPath}";
    public bool Selected { get; set; } = true;
    public bool IsPrimary { get; set; }
}

public sealed class SourceKindChineseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is SourceKind kind ? ToChinese(kind) : "未知";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    public static string ToChinese(SourceKind kind) => kind switch
    {
        SourceKind.Core => "会话主数据", SourceKind.Project => "项目", SourceKind.Memory => "记忆",
        SourceKind.Skill => "技能", SourceKind.Plugin => "插件", SourceKind.Tool => "工具",
        SourceKind.Application => "应用", SourceKind.Environment => "环境", SourceKind.Custom => "自定义", SourceKind.Session => "会话文件",
        _ => "未知"
    };
}

public sealed class SourcePriorityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "必须备份" => Brushes.Firebrick,
        "建议备份" => Brushes.DarkGoldenrod,
        _ => Brushes.DimGray
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SourceStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "找不到" => Brushes.Firebrick,
        "已选择" => Brushes.ForestGreen,
        _ => Brushes.DimGray
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

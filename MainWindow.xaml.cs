using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScintillaNET;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace LiteList;

public sealed class PreviewBlockPresenter : Border
{
    public static readonly DependencyProperty BlockProperty = DependencyProperty.Register(
        nameof(Block), typeof(PreviewBlockVM), typeof(PreviewBlockPresenter),
        new PropertyMetadata(null, OnBlockChanged));

    private PreviewBlockVM? _subscribedVm;

    public PreviewBlockVM? Block
    {
        get => (PreviewBlockVM?)GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public PreviewBlockPresenter()
    {
        MinHeight = 20;
        Loaded += (_, _) =>
        {
            AttachVm(Block);
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            DetachVm();
            Child = null;
        };
    }

    private static void OnBlockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PreviewBlockPresenter p)
        {
            p.AttachVm(e.NewValue as PreviewBlockVM);
            if (p.IsLoaded) p.Rebuild();
        }
    }

    private void AttachVm(PreviewBlockVM? vm)
    {
        if (ReferenceEquals(_subscribedVm, vm)) return;
        DetachVm();
        _subscribedVm = vm;
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += Vm_PropertyChanged;
        ApplyActiveBorderState();
    }

    private void DetachVm()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= Vm_PropertyChanged;
            _subscribedVm = null;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewBlockVM.IsSearchActive))
            ApplyActiveBorderState();
    }

    private void ApplyActiveBorderState()
    {
        if (_subscribedVm?.IsSearchActive == true)
        {
            BorderBrush = ThemeHelper.GetBrush("AccentBrush");
            BorderThickness = new Thickness(3, 0, 0, 0);
            Padding = new Thickness(8, 2, 4, 2);
            Background = ThemeHelper.GetBrush("ActiveBg");
            CornerRadius = new CornerRadius(3);
        }
        else
        {
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            Background = Brushes.Transparent;
            CornerRadius = new CornerRadius(0);
        }
    }

    public void Rebuild()
    {
        ApplyActiveBorderState();
        if (Block != null && Window.GetWindow(this) is MainWindow mw)
            Child = mw.BuildPreviewBlockElement(Block);
        else
            Child = null;
    }
}

public partial class MainWindow : Window
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Info("LiteList starting...");

        AppDomain.CurrentDomain.UnhandledException += (_, eArgs) =>
        {
            AppLogger.Error("AppDomain.UnhandledException", eArgs.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, eArgs) =>
        {
            AppLogger.Error("TaskScheduler.UnobservedTaskException", eArgs.Exception);
            eArgs.SetObserved();
        };

        var app = new Application();
        app.DispatcherUnhandledException += (_, eArgs) =>
        {
            AppLogger.Error("DispatcherUnhandledException", eArgs.Exception);
            MessageBox.Show(
                $"发生异常：{eArgs.Exception.Message}\n\n详细调试信息已写入日志：\n{AppLogger.LogFilePath}",
                "LiteList", MessageBoxButton.OK, MessageBoxImage.Error);
            eArgs.Handled = true;
        };
        ThemeHelper.ApplySystemTheme(app.Resources);

        string? initDb = null;
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("-", StringComparison.Ordinal))
            initDb = Path.GetFullPath(args[0].Trim());

        var win = new MainWindow(initDb);
        app.Run(win);
    }

    private readonly DatabaseService _db = new();
    private readonly FileArchiveService _files = new();
    private readonly MarkdownScintilla _scintilla = new();
    private readonly VirtualizedMarkdownPreview _previewEngine;

    private readonly DispatcherTimer _idleSaveTimer;
    private readonly DispatcherTimer _foldAndTocTimer;
    private readonly string? _initialDbPath;

    private AppSettings _settings = new();
    private EditorViewMode _preferredViewMode = EditorViewMode.Preview;
    private EditorViewMode _viewMode = EditorViewMode.Preview;
    private readonly Dictionary<long, TreeNodeVM> _nodeLookup = new();
    public ObservableCollection<TreeNodeVM> RootNodes { get; } = new();

    private long _selectedNodeId;
    private string _currentContent = "";      // 纯内存权威正文（惰性加载到 Scintilla）
    private bool _editorTextLoaded;
    private int _cachedLineDigits = 2;
    private bool _isDirty;
    private bool _suppressEditorEvents;
    private bool _suppressSelectionGuard;
    private bool _isModalOpen;

    private readonly List<(int Start, int Length)> _localFindMatches = new();
    private int _localFindIndex = -1;
    private bool _localFindCacheStale;

    private List<SearchHit> _globalHits = new();
    private int _globalHitIndex = -1;
    private string _activeGlobalKeyword = "";
    private bool _suppressGlobalHitSync;

    private Point _dragStartPoint;
    private TreeNodeVM? _draggingNode;
    private TreeNodeVM? _contextTargetNode;

    public MainWindow(string? initialDbPath)
    {
        _initialDbPath = initialDbPath;
        InitializeComponent();
        OutlineTree.ItemsSource = RootNodes;

        _previewEngine = new VirtualizedMarkdownPreview(_files);
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;

        _previewEngine.HeadingFoldToggled += _ =>
        {
            PreviewList.ItemsSource = null;
            _previewEngine.RebuildVisibleBlocks();
            PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
        };
        _previewEngine.TaskCheckboxToggled += PreviewEngine_TaskCheckboxToggled;
        _previewEngine.ImageClicked += ShowImageLightbox;
        _previewEngine.AttachmentClicked += OpenAttachmentFile;
        _previewEngine.AttachmentSaveAsRequested += SaveAttachmentAsFile;
        _previewEngine.ExternalUrlClicked += OpenExternalUrl;
        _previewEngine.StatusToast += msg => TxtSaveStatus.Text = msg;

        SetupScintillaHost();

        _idleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idleSaveTimer.Tick += (_, _) =>
        {
            _idleSaveTimer.Stop();
            if (_settings.SaveMode == SaveMode.IdleAutoSave && _isDirty) SaveCurrentNode();
        };

        _foldAndTocTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _foldAndTocTimer.Tick += (_, _) =>
        {
            _foldAndTocTimer.Stop();
            if (_selectedNodeId <= 0 || _viewMode != EditorViewMode.Edit || !_editorTextLoaded) return;
            var sw = Stopwatch.StartNew();
            _scintilla.RebuildFoldLevelsFromText(_scintilla.Text);
            if (_settings.ShowRightToc) RefreshRightTocTree();
            if (sw.ElapsedMilliseconds > 50)
                AppLogger.Info($"FoldAndTocTick elapsed={sw.ElapsedMilliseconds}ms");
        };

        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Loaded += MainWindow_Loaded;
    }

    internal UIElement BuildPreviewBlockElement(PreviewBlockVM vm)
    {
        return _previewEngine.BuildBlockElement(
            vm,
            _settings.FontSize,
            GlobalSearchPanel.Visibility == Visibility.Visible ? _activeGlobalKeyword : null);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        var targetDb = _initialDbPath ?? _db.ResolveDefaultDbPath(_files.AppBaseDir);
        try
        {
            OpenDatabase(targetDb);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"MainWindow_Loaded OpenDatabase failed: {targetDb}", ex);
            ShowModalMessage($"无法打开笔记库「{targetDb}」：{ex.Message}\n\n将为你创建或选择其他笔记库。",
                "打开笔记库失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            var fallback = _db.ResolveDefaultDbPath(_files.AppBaseDir);
            if (!string.Equals(fallback, targetDb, StringComparison.OrdinalIgnoreCase))
            {
                try { OpenDatabase(fallback); return; }
                catch (Exception fex) { AppLogger.Error($"Fallback OpenDatabase failed: {fallback}", fex); }
            }
            BtnOpenDb_Click(this, new RoutedEventArgs());
        }
    }

    private void SystemEvents_UserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Application.Current == null) return;
            if (ThemeHelper.ApplySystemTheme(Application.Current.Resources))
            {
                _scintilla.ApplyThemeAndFont(_settings.FontSize);
                _scintilla.ContextMenuStrip = BuildEditorWinFormsMenu();
                if (_viewMode == EditorViewMode.Preview) RefreshPreviewFromMemory();
            }
        }));
    }

    private MessageBoxResult ShowModalMessage(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        _isModalOpen = true;
        try { return MessageBox.Show(this, text, caption, buttons, icon); }
        finally { _isModalOpen = false; }
    }

    private void SetupScintillaHost()
    {
        EditorHost.Child = _scintilla;
        _scintilla.AllowDrop = true;
        _scintilla.ContextMenuStrip = BuildEditorWinFormsMenu();
        _scintilla.TextChanged += Scintilla_TextChanged;
        _scintilla.UpdateUI += (_, e) =>
        {
            if ((e.Change & (UpdateChange.Selection | UpdateChange.Content)) != 0)
                UpdateCursorStatus();
        };
        _scintilla.PasteIntercepted += Scintilla_PasteIntercepted;
        _scintilla.ShortcutPressed += Scintilla_ShortcutPressed;
        _scintilla.CtrlMouseWheelZoom += delta => Dispatcher.BeginInvoke(new Action(() => ChangeFontSize(delta)));
        _scintilla.DragEnter += (_, e) =>
        {
            if (e.Data != null && e.Data.GetDataPresent(WF.DataFormats.FileDrop))
                e.Effect = WF.DragDropEffects.Copy;
        };
        _scintilla.DragDrop += (_, e) =>
        {
            if (e.Data != null && e.Data.GetData(WF.DataFormats.FileDrop) is string[] paths)
                ImportFilesAndInsert(paths);
        };
    }

    private WF.ContextMenuStrip BuildEditorWinFormsMenu()
    {
        var menu = new WF.ContextMenuStrip();
        if (ThemeHelper.IsDarkMode)
        {
            menu.BackColor = SD.Color.FromArgb(36, 36, 40);
            menu.ForeColor = SD.Color.FromArgb(228, 228, 231);
        }
        void Add(string text, Action act) => menu.Items.Add(text, null, (_, _) => Dispatcher.BeginInvoke(act));
        Add("撤销 (Ctrl+Z)", () => _scintilla.Undo());
        Add("重做 (Ctrl+Y)", () => _scintilla.Redo());
        menu.Items.Add(new WF.ToolStripSeparator());
        Add("剪切 (Ctrl+X)", () => _scintilla.Cut());
        Add("复制 (Ctrl+C)", () => _scintilla.Copy());
        Add("粘贴（自动归档图片/附件）", () => _scintilla.Paste());
        Add("全选 (Ctrl+A)", () => _scintilla.SelectAll());
        menu.Items.Add(new WF.ToolStripSeparator());
        Add("加粗 (Ctrl+B)", () => WrapSel("**", "**", "加粗文本"));
        Add("斜体 (Ctrl+I)", () => WrapSel("*", "*", "斜体文本"));
        Add("插入代码块", InsertCodeBlockSnippet);
        Add("插入待办 - [ ]", () => ToggleLinePrefix("- [ ] "));
        Add("插入表格", () => InsertSnippet("\r\n| 列1 | 列2 | 列3 |\r\n| --- | --- | --- |\r\n| 内容 | 内容 | 内容 |\r\n"));
        Add("插入图片 / 附件...", OpenFileAndInsertDialog);
        menu.Items.Add(new WF.ToolStripSeparator());
        Add("查找与替换 (Ctrl+F)", () => OpenFindBar(false));
        Add("折叠全部标题与缩进块", () => { _scintilla.RebuildFoldLevelsFromText(GetCurrentText()); _scintilla.FoldAll(FoldAction.Contract); });
        Add("展开全部折叠", () => _scintilla.FoldAll(FoldAction.Expand));
        return menu;
    }

    private string GetCurrentText() => _editorTextLoaded ? _scintilla.Text : _currentContent;

    private void EnsureEditorLoaded()
    {
        if (_editorTextLoaded || _selectedNodeId <= 0) return;
        var sw = Stopwatch.StartNew();
        _suppressEditorEvents = true;
        try
        {
            _scintilla.Text = _currentContent;
            _scintilla.EmptyUndoBuffer();
            _scintilla.UpdateLineNumberMarginWidth();
            _cachedLineDigits = Math.Max(2, _scintilla.Lines.Count.ToString().Length);
            _scintilla.RebuildFoldLevelsFromText(_currentContent);
            _editorTextLoaded = true;
        }
        finally
        {
            _suppressEditorEvents = false;
        }
        UpdateCursorStatus();
        AppLogger.Info($"EnsureEditorLoaded node={_selectedNodeId}, chars={_currentContent.Length}, lines={_scintilla.Lines.Count}, elapsed={sw.ElapsedMilliseconds}ms");
    }

    // ===== 数据库打开、状态重置与树构建 =====

    private void ResetWorkspaceTransientState()
    {
        _idleSaveTimer.Stop();
        _foldAndTocTimer.Stop();
        _selectedNodeId = 0;
        _currentContent = "";
        _editorTextLoaded = false;
        _isDirty = false;
        _contextTargetNode = null;

        GlobalSearchPanel.Visibility = Visibility.Collapsed;
        _globalHits.Clear();
        _globalHitIndex = -1;
        _activeGlobalKeyword = "";
        LstGlobalHits.ItemsSource = null;
        TxtGlobalSearch.Text = "";
        TxtGlobalSearchCount.Text = "请输入关键词";

        FindBar.Visibility = Visibility.Collapsed;
        _localFindMatches.Clear();
        _localFindIndex = -1;
        _localFindCacheStale = false;
        TxtFindCount.Text = "0/0";
        _scintilla.ClearSearchHighlights();
    }

    private void OpenDatabase(string dbPath)
    {
        var sw = Stopwatch.StartNew();
        ResetWorkspaceTransientState();

        _db.OpenOrCreate(dbPath);
        _files.CurrentDbDir = Path.GetDirectoryName(_db.CurrentDbPath);
        TxtDbBadge.Text = Path.GetFileName(_db.CurrentDbPath);
        TxtDbBadge.ToolTip = _db.CurrentDbPath;
        Title = $"LiteList - {TxtDbBadge.Text}";
        TxtFilesDirInfo.Text = $"附件目录: {_files.GetTargetFilesDir()}";

        _settings = _db.LoadSettings();
        ApplySettingsToUI();
        ReloadTree(selectId: null);
        AppLogger.Info($"OpenDatabase path={_db.CurrentDbPath}, nodes={_nodeLookup.Count}, elapsed={sw.ElapsedMilliseconds}ms");
    }

    private void ApplySettingsToUI()
    {
        _scintilla.ApplyThemeAndFont(_settings.FontSize);
        ApplyLeftVisibility(_settings.ShowLeftTree);
        ApplyRightVisibility(_settings.ShowRightToc);
        if (_viewMode == EditorViewMode.Preview) RefreshPreviewFromMemory();
    }

    private void ApplyLeftVisibility(bool show)
    {
        LeftSidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ColLeftTree.Width = show ? new GridLength(260) : new GridLength(0);
        ColLeftSplit.Width = show ? new GridLength(4) : new GridLength(0);
    }

    private void ApplyRightVisibility(bool show)
    {
        RightTocPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ColRightToc.Width = show ? new GridLength(220) : new GridLength(0);
        ColRightSplit.Width = show ? new GridLength(4) : new GridLength(0);
        if (show && _selectedNodeId > 0) RefreshRightTocTree();
    }

    private void ReloadTree(long? selectId)
    {
        var records = _db.GetTree();
        RootNodes.Clear();
        _nodeLookup.Clear();

        foreach (var r in records)
        {
            var vm = new TreeNodeVM
            {
                Id = r.Id, ParentId = r.ParentId, Title = r.Title,
                SortOrder = r.SortOrder, IsExpanded = r.IsExpanded
            };
            _nodeLookup[vm.Id] = vm;
        }
        foreach (var vm in _nodeLookup.Values)
        {
            if (vm.ParentId > 0 && _nodeLookup.TryGetValue(vm.ParentId, out var p)) p.Children.Add(vm);
            else RootNodes.Add(vm);
        }

        long target = selectId ?? (RootNodes.Count > 0 ? RootNodes[0].Id : 0);
        if (target > 0 && _nodeLookup.ContainsKey(target)) SelectTreeNode(target);
        else ShowEmptyState();
    }

    private void ShowEmptyState()
    {
        _idleSaveTimer.Stop();
        _foldAndTocTimer.Stop();
        _selectedNodeId = 0;
        _currentContent = "";
        _editorTextLoaded = false;
        SetDirtyState(false);

        EmptyOverlay.Visibility = Visibility.Visible;
        EditorHeaderBar.Visibility = Visibility.Collapsed;
        FormatToolBar.Visibility = Visibility.Collapsed;
        FindBar.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Collapsed;
        PreviewList.Visibility = Visibility.Collapsed;
        RightTocTree.ItemsSource = null;

        TxtCursorInfo.Text = "行 0, 列 0";
        TxtDocStats.Text = "0 字 · 0 行";
    }

    private void SelectTreeNode(long nodeId)
    {
        if (!_nodeLookup.TryGetValue(nodeId, out var target)) return;
        var cur = target;
        var visited = new HashSet<long> { nodeId };
        int guard = 0;
        while (cur.ParentId > 0 && guard++ < 100 && _nodeLookup.TryGetValue(cur.ParentId, out var p))
        {
            if (!visited.Add(p.Id)) break;
            p.IsExpanded = true;
            cur = p;
        }
        target.IsSelected = true;
    }

    private bool EnsureSafeToLeaveCurrent(bool isBlur = false)
    {
        if (!_isDirty || _selectedNodeId <= 0) return true;
        if (!isBlur && _settings.SaveMode == SaveMode.IdleAutoSave) { SaveCurrentNode(); return true; }

        if (isBlur)
        {
            if (_settings.BlurAction == BlurAction.None) return true;
            if (_settings.BlurAction == BlurAction.AutoSave) { SaveCurrentNode(); return true; }
        }

        var title = _nodeLookup.TryGetValue(_selectedNodeId, out var vm) ? vm.Title : "当前节点";
        var res = ShowModalMessage($"节点「{title}」有未保存的修改，是否保存？",
            "未保存提醒", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes) { SaveCurrentNode(); return true; }
        if (res == MessageBoxResult.No) { _idleSaveTimer.Stop(); SetDirtyState(false); return true; }
        return false;
    }

    private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressSelectionGuard) return;
        if (e.NewValue is not TreeNodeVM nextNode || nextNode.Id == _selectedNodeId) return;

        if (!EnsureSafeToLeaveCurrent())
        {
            _suppressSelectionGuard = true;
            try { if (_nodeLookup.TryGetValue(_selectedNodeId, out var prev)) prev.IsSelected = true; }
            finally { _suppressSelectionGuard = false; }
            return;
        }
        LoadNodeIntoEditor(nextNode.Id, preserveExactMode: false);
    }

    private void LoadNodeIntoEditor(long nodeId, bool preserveExactMode)
    {
        _selectedNodeId = nodeId;
        var detail = _db.GetNodeContent(nodeId);

        EmptyOverlay.Visibility = Visibility.Collapsed;
        EditorHeaderBar.Visibility = Visibility.Visible;

        _suppressEditorEvents = true;
        try
        {
            TxtNodeTitle.Text = detail.Title;
            _currentContent = detail.Content;
            _editorTextLoaded = false;
            SetDirtyState(false);
        }
        finally { _suppressEditorEvents = false; }

        int lines = Math.Max(1, DatabaseService.SplitLinesNormalized(_currentContent).Length);
        TxtCursorInfo.Text = "行 1, 列 1";
        TxtDocStats.Text = $"{_currentContent.Length} 字 · {lines} 行";

        if (_settings.ShowRightToc) RefreshRightTocTree();

        var targetMode = preserveExactMode
            ? _viewMode
            : (_preferredViewMode == EditorViewMode.Preview && string.IsNullOrWhiteSpace(_currentContent)
                ? EditorViewMode.Edit
                : _preferredViewMode);
        SetEditorViewMode(targetMode, focusEditor: false, updatePreference: false);
    }

    private void SetDirtyState(bool dirty)
    {
        _isDirty = dirty;
        TxtSaveStatus.Text = dirty ? "● 未保存" : "已保存";
        TxtSaveStatus.Foreground = ThemeHelper.GetBrush(dirty ? "DangerBrush" : "MutedFg");
    }

    private void SaveCurrentNode()
    {
        if (_selectedNodeId <= 0) return;
        _idleSaveTimer.Stop();
        var title = string.IsNullOrWhiteSpace(TxtNodeTitle.Text) ? "未命名节点" : TxtNodeTitle.Text.Trim();
        _currentContent = GetCurrentText();
        _db.SaveNode(_selectedNodeId, title, _currentContent);
        if (_nodeLookup.TryGetValue(_selectedNodeId, out var vm)) vm.Title = title;
        SetDirtyState(false);
    }

    // ===== 双视图切换与虚拟化预览 =====

    private void SetEditorViewMode(EditorViewMode mode, bool focusEditor = false, bool updatePreference = true)
    {
        if (updatePreference) _preferredViewMode = mode;
        if (mode == EditorViewMode.Preview && _editorTextLoaded)
        {
            _currentContent = _scintilla.Text;
        }

        _viewMode = mode;
        BtnViewEdit.FontWeight = mode == EditorViewMode.Edit ? FontWeights.Bold : FontWeights.Normal;
        BtnViewPreview.FontWeight = mode == EditorViewMode.Preview ? FontWeights.Bold : FontWeights.Normal;

        bool isOverlayOpen = SettingsOverlay.Visibility == Visibility.Visible || LightboxOverlay.Visibility == Visibility.Visible;
        FormatToolBar.Visibility = mode == EditorViewMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        EditorHost.Visibility = (mode == EditorViewMode.Edit && !isOverlayOpen) ? Visibility.Visible : Visibility.Collapsed;
        PreviewList.Visibility = mode == EditorViewMode.Preview ? Visibility.Visible : Visibility.Collapsed;

        if (mode == EditorViewMode.Edit)
        {
            bool needLoad = !_editorTextLoaded;
            EnsureEditorLoaded();
            if (!needLoad) _scintilla.RebuildFoldLevelsFromText(GetCurrentText());
            if (focusEditor && !isOverlayOpen) _scintilla.Focus();
        }
        else
        {
            FindBar.Visibility = Visibility.Collapsed;
            if (_editorTextLoaded) _scintilla.ClearSearchHighlights();
            RefreshPreviewFromMemory();
        }
    }

    private void RefreshPreviewFromMemory()
    {
        if (_selectedNodeId <= 0) return;
        var sw = Stopwatch.StartNew();
        PreviewList.ItemsSource = null;
        _previewEngine.ParseDocument(GetCurrentText());
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
        AppLogger.Info($"RefreshPreview node={_selectedNodeId}, blocks={_previewEngine.AllBlocks.Count}, elapsed={sw.ElapsedMilliseconds}ms");
    }

    private void RefreshRightTocTree()
    {
        if (_selectedNodeId <= 0) { RightTocTree.ItemsSource = null; return; }
        RightTocTree.ItemsSource = MarkdownScintilla.ExtractHeadingTreeFromText(GetCurrentText());
    }

    // ===== 编辑事件与低开销防抖刷新 =====

    private void TxtNodeTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedNodeId <= 0) return;
        if (_nodeLookup.TryGetValue(_selectedNodeId, out var vm))
            vm.Title = string.IsNullOrWhiteSpace(TxtNodeTitle.Text) ? "未命名节点" : TxtNodeTitle.Text;
        MarkDirty();
    }

    private void Scintilla_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _selectedNodeId <= 0) return;
        int digits = Math.Max(2, _scintilla.Lines.Count.ToString().Length);
        if (digits != _cachedLineDigits)
        {
            _cachedLineDigits = digits;
            _scintilla.UpdateLineNumberMarginWidth();
        }
        if (FindBar.Visibility == Visibility.Visible)
            _localFindCacheStale = true;

        _foldAndTocTimer.Stop();
        _foldAndTocTimer.Start();
        MarkDirty();
    }

    private void MarkDirty()
    {
        SetDirtyState(true);
        if (_settings.SaveMode == SaveMode.IdleAutoSave)
        {
            _idleSaveTimer.Stop();
            _idleSaveTimer.Start();
        }
    }

    private void UpdateCursorStatus()
    {
        if (!_editorTextLoaded) return;
        int pos = _scintilla.CurrentPosition;
        int line = _scintilla.LineFromPosition(pos) + 1;
        int col = _scintilla.GetColumn(pos) + 1;
        TxtCursorInfo.Text = $"行 {line}, 列 {col}";
        TxtDocStats.Text = $"{_scintilla.TextLength} 字 · {_scintilla.Lines.Count} 行";
    }

    private void SafeScrollPreviewToBlock(PreviewBlockVM? blk)
    {
        if (blk == null || PreviewList.Items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                if (_viewMode != EditorViewMode.Preview || PreviewList.Items.Count == 0) return;
                PreviewList.UpdateLayout();
                PreviewList.ScrollIntoView(blk);
            }
            catch (Exception ex) { AppLogger.Error(nameof(SafeScrollPreviewToBlock), ex); }
        }));
    }

    private void RightTocTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not HeadingNodeVM h) return;
        if (_viewMode == EditorViewMode.Edit)
        {
            EnsureEditorLoaded();
            _scintilla.ScrollToLineCentered(h.LineNumber);
            _scintilla.Focus();
        }
        else
        {
            if (_previewEngine.EnsureLineVisibleAndFindBlock(h.LineNumber, out var blk))
            {
                PreviewList.ItemsSource = null;
                _previewEngine.RebuildVisibleBlocks();
                PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
            }
            SafeScrollPreviewToBlock(blk);
        }
    }

    private void BtnRefreshToc_Click(object sender, RoutedEventArgs e)
    {
        if (_viewMode == EditorViewMode.Edit && _editorTextLoaded)
            _scintilla.RebuildFoldLevelsFromText(_scintilla.Text);
        RefreshRightTocTree();
        TxtSaveStatus.Text = "大纲与折叠已刷新";
    }

    private void BtnExpandToc_Click(object sender, RoutedEventArgs e) => SetTocExpandAll(RightTocTree.ItemsSource as ObservableCollection<HeadingNodeVM>, true);
    private void BtnCollapseToc_Click(object sender, RoutedEventArgs e) => SetTocExpandAll(RightTocTree.ItemsSource as ObservableCollection<HeadingNodeVM>, false);
    private static void SetTocExpandAll(ObservableCollection<HeadingNodeVM>? items, bool expand)
    {
        if (items == null) return;
        foreach (var it in items) { it.IsExpanded = expand; SetTocExpandAll(it.Children, expand); }
    }

    // ===== Ctrl+K 常驻全局搜索 =====

    private void OpenGlobalSearch()
    {
        GlobalSearchPanel.Visibility = Visibility.Visible;
        TxtGlobalSearch.Focus();
        TxtGlobalSearch.SelectAll();
    }

    private void BtnOpenGlobalSearch_Click(object sender, RoutedEventArgs e) => OpenGlobalSearch();
    private void BtnCloseGlobalSearch_Click(object sender, RoutedEventArgs e)
    {
        GlobalSearchPanel.Visibility = Visibility.Collapsed;
        _activeGlobalKeyword = "";
        if (_editorTextLoaded) _scintilla.ClearSearchHighlights();
        foreach (var b in _previewEngine.AllBlocks) b.IsSearchActive = false;
        if (_viewMode == EditorViewMode.Preview) RefreshPreviewFromMemory();
    }

    private void BtnToggleHitList_Changed(object sender, RoutedEventArgs e)
    {
        if (LstGlobalHits != null)
            LstGlobalHits.Visibility = BtnToggleHitList.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TxtGlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var kw = TxtGlobalSearch.Text.Trim();
            if (!string.Equals(kw, _activeGlobalKeyword, StringComparison.OrdinalIgnoreCase) || _globalHits.Count == 0)
                RunGlobalSearch(jumpFirst: true);
            else
                StepGlobalHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            BtnCloseGlobalSearch_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BtnGlobalRun_Click(object sender, RoutedEventArgs e) => RunGlobalSearch(jumpFirst: true);
    private void BtnGlobalNext_Click(object sender, RoutedEventArgs e) => StepGlobalHit(1);
    private void BtnGlobalPrev_Click(object sender, RoutedEventArgs e) => StepGlobalHit(-1);

    private void RunGlobalSearch(bool jumpFirst)
    {
        var kw = TxtGlobalSearch.Text.Trim();
        _activeGlobalKeyword = kw;
        if (string.IsNullOrEmpty(kw))
        {
            _globalHits.Clear();
            LstGlobalHits.ItemsSource = null;
            TxtGlobalSearchCount.Text = "请输入关键词";
            return;
        }

        var sw = Stopwatch.StartNew();
        _globalHits = _db.SearchOccurrences(
            kw,
            maxHits: 500,
            overrideNodeId: _selectedNodeId > 0 ? _selectedNodeId : null,
            overrideTitle: _selectedNodeId > 0 ? TxtNodeTitle.Text : null,
            overrideContent: _selectedNodeId > 0 ? GetCurrentText() : null);

        LstGlobalHits.ItemsSource = _globalHits;
        _globalHitIndex = -1;
        AppLogger.Info($"RunGlobalSearch kw=\"{kw}\", hits={_globalHits.Count}, elapsed={sw.ElapsedMilliseconds}ms");

        if (_globalHits.Count == 0)
        {
            TxtGlobalSearchCount.Text = "0 处匹配";
            return;
        }
        int nodeCount = _globalHits.Select(x => x.NodeId).Distinct().Count();
        TxtGlobalSearchCount.Text = $"共 {_globalHits.Count} 处（{nodeCount} 个节点）";
        if (jumpFirst) NavigateToGlobalHit(0);
    }

    private void StepGlobalHit(int delta)
    {
        if (_globalHits.Count == 0) { RunGlobalSearch(jumpFirst: true); return; }
        int next = (_globalHitIndex + delta + _globalHits.Count) % _globalHits.Count;
        NavigateToGlobalHit(next);
    }

    private void LstGlobalHits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGlobalHitSync || LstGlobalHits.SelectedIndex < 0) return;
        NavigateToGlobalHit(LstGlobalHits.SelectedIndex);
    }

    private void NavigateToGlobalHit(int hitIdx)
    {
        if (hitIdx < 0 || hitIdx >= _globalHits.Count) return;
        var hit = _globalHits[hitIdx];

        if (hit.NodeId != _selectedNodeId)
        {
            if (!_nodeLookup.ContainsKey(hit.NodeId)) return;
            if (!EnsureSafeToLeaveCurrent()) return;
            _suppressSelectionGuard = true;
            try
            {
                SelectTreeNode(hit.NodeId);
                LoadNodeIntoEditor(hit.NodeId, preserveExactMode: true);
            }
            finally { _suppressSelectionGuard = false; }
        }

        _globalHitIndex = hitIdx;
        int nodeCount = _globalHits.Select(x => x.NodeId).Distinct().Count();
        TxtGlobalSearchCount.Text = $"第 {hitIdx + 1} / {_globalHits.Count} 处（{nodeCount} 节点）";

        _suppressGlobalHitSync = true;
        try
        {
            LstGlobalHits.SelectedIndex = hitIdx;
            LstGlobalHits.ScrollIntoView(_globalHits[hitIdx]);
        }
        finally { _suppressGlobalHitSync = false; }

        int targetLine = hit.LineNumber > 0 ? hit.LineNumber : 1;
        if (_viewMode == EditorViewMode.Edit)
        {
            EnsureEditorLoaded();
            _scintilla.HighlightAndScrollToMatch(_activeGlobalKeyword, targetLine, hit.CharOffsetInLine, hit.MatchLength);
        }
        else
        {
            foreach (var b in _previewEngine.AllBlocks) b.IsSearchActive = false;
            if (hit.LineNumber == 0)
            {
                if (_previewEngine.VisibleBlocks.Count > 0)
                    SafeScrollPreviewToBlock(_previewEngine.VisibleBlocks[0]);
            }
            else
            {
                if (_previewEngine.EnsureLineVisibleAndFindBlock(targetLine, out var targetBlock))
                {
                    PreviewList.ItemsSource = null;
                    _previewEngine.RebuildVisibleBlocks();
                    PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
                }
                if (targetBlock != null) targetBlock.IsSearchActive = true;
                SafeScrollPreviewToBlock(targetBlock);
            }
        }
    }

    // ===== 粘贴、拖放图片与附件 =====

    private void Scintilla_PasteIntercepted(object? sender, HandledEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                e.Handled = true;
                ImportFilesAndInsert(list.Cast<string>());
                return;
            }
            if (Clipboard.ContainsText())
            {
                var txt = Clipboard.GetText();
                if (txt.IndexOf("file://", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Regex.IsMatch(txt, @"[a-zA-Z]:[\\/]"))
                {
                    var (rewritten, count) = _files.ResolveLocalReferencesInFragment(txt);
                    if (count > 0)
                    {
                        e.Handled = true;
                        InsertSnippet(rewritten);
                        TxtSaveStatus.Text = $"已自动归档 {count} 个引用至 files/";
                    }
                }
                return;
            }
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    e.Handled = true;
                    var res = _files.SaveClipboardImage(img);
                    InsertSnippet($"![{res.Filename}]({res.RelPath})\r\n");
                }
            }
        }
        catch (ExternalException ex)
        {
            AppLogger.Error("Scintilla_PasteIntercepted Clipboard locked", ex);
            TxtSaveStatus.Text = "剪贴板读取失败（正被占用）";
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(Scintilla_PasteIntercepted), ex);
            TxtSaveStatus.Text = $"粘贴归档失败: {ex.Message}";
        }
    }

    private void ImportFilesAndInsert(IEnumerable<string> paths)
    {
        var lines = new List<string>();
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var res = _files.ImportExternalFile(p);
                lines.Add(res.IsImage ? $"![{res.Filename}]({res.RelPath})" : $"[{res.Filename}]({res.RelPath})");
            }
            catch (Exception ex) { AppLogger.Error($"ImportExternalFile: {p}", ex); }
        }
        if (lines.Count > 0)
        {
            InsertSnippet(string.Join("\r\n", lines) + "\r\n");
            TxtSaveStatus.Text = $"已复制 {lines.Count} 个文件到 files/";
        }
    }

    private void OpenFileAndInsertDialog()
    {
        _isModalOpen = true;
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要插入的图片或附件（自动复制到 files 目录）" };
        bool ok = dlg.ShowDialog(this) == true;
        _isModalOpen = false;
        if (ok) ImportFilesAndInsert(dlg.FileNames);
    }

    private void InsertSnippet(string text)
    {
        EnsureEditorLoaded();
        _scintilla.ReplaceSelection(text);
        _scintilla.Focus();
    }

    private void InsertCodeBlockSnippet()
    {
        EnsureEditorLoaded();
        int start = _scintilla.SelectionStart;
        const string tpl = "\r\n```text\r\n\r\n```\r\n";
        _scintilla.ReplaceSelection(tpl);
        int insideLinePos = start + "\r\n```text\r\n".Length;
        _scintilla.GotoPosition(insideLinePos);
        _scintilla.Focus();
    }

    private void PreviewEngine_TaskCheckboxToggled(int lineNum, bool isChecked)
    {
        string newline = _currentContent.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        var lines = DatabaseService.SplitLinesNormalized(_currentContent);
        int idx = lineNum - 1;
        if (idx < 0 || idx >= lines.Length) return;

        var rx = new Regex(@"^(\s*(?:[-*+]|\d+\.)\s+)\[[ xX]\]");
        var updatedLine = rx.Replace(lines[idx], isChecked ? "$1[x]" : "$1[ ]", 1);
        lines[idx] = updatedLine;
        _currentContent = string.Join(newline, lines);

        if (_editorTextLoaded)
        {
            _suppressEditorEvents = true;
            try { _scintilla.ReplaceLinePreservingUndo(lineNum, updatedLine); }
            finally { _suppressEditorEvents = false; }
        }
        MarkDirty();
    }

    private void ShowImageLightbox(string fullPath)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();
            LightboxImg.Source = bmp;
            EditorHost.Visibility = Visibility.Collapsed;
            LightboxOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"ShowImageLightbox: {fullPath}", ex);
            ShowModalMessage($"无法加载大图：{ex.Message}", "图片预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LightboxOverlay_Click(object? sender, MouseButtonEventArgs? e)
    {
        LightboxOverlay.Visibility = Visibility.Collapsed;
        LightboxImg.Source = null;
        if (_viewMode == EditorViewMode.Edit) EditorHost.Visibility = Visibility.Visible;
    }

    private void OpenAttachmentFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null)
        {
            ShowModalMessage($"在 files 目录中找不到文件：{relPath}", "找不到附件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLogger.Error($"OpenAttachmentFile: {full}", ex);
            ShowModalMessage($"无法打开附件：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveAttachmentAsFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null)
        {
            ShowModalMessage($"在 files 目录中找不到文件：{relPath}", "找不到附件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _isModalOpen = true;
        try
        {
            var dlg = new SaveFileDialog { FileName = Path.GetFileName(full), Title = "附件另存为" };
            if (dlg.ShowDialog(this) == true) File.Copy(full, dlg.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"SaveAttachmentAsFile: {full}", ex);
            MessageBox.Show(this, $"另存失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _isModalOpen = false; }
    }

    private void OpenExternalUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppLogger.Error($"OpenExternalUrl: {url}", ex);
                ShowModalMessage($"无法打开链接：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    // ===== 节点内查找与替换 =====

    private void OpenFindBar(bool focusReplace)
    {
        if (_viewMode == EditorViewMode.Preview) SetEditorViewMode(EditorViewMode.Edit, true);
        EnsureEditorLoaded();
        FindBar.Visibility = Visibility.Visible;
        var sel = _scintilla.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n")) TxtFind.Text = sel;
        UpdateLocalFindMatches(jumpFirst: false);
        if (focusReplace) TxtReplace.Focus();
        else { TxtFind.Focus(); TxtFind.SelectAll(); }
    }

    private Regex? BuildLocalFindRegex()
    {
        var pattern = TxtFind.Text;
        if (string.IsNullOrEmpty(pattern)) return null;
        var opts = ChkCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(ChkRegex.IsChecked == true ? pattern : Regex.Escape(pattern), opts);
    }

    private void UpdateLocalFindMatches(bool jumpFirst)
    {
        _localFindMatches.Clear();
        _localFindIndex = -1;
        _localFindCacheStale = false;
        _scintilla.ClearSearchHighlights();

        var pattern = TxtFind.Text;
        if (string.IsNullOrEmpty(pattern)) { TxtFindCount.Text = "0/0"; return; }

        try
        {
            var rx = BuildLocalFindRegex()!;
            foreach (Match m in rx.Matches(_scintilla.Text))
            {
                if (m.Length > 0) _localFindMatches.Add((m.Index, m.Length));
                if (_localFindMatches.Count >= 5000) break;
            }
        }
        catch { TxtFindCount.Text = "正则错"; return; }

        _scintilla.IndicatorCurrent = 0;
        int drawLimit = Math.Min(600, _localFindMatches.Count);
        for (int i = 0; i < drawLimit; i++)
            _scintilla.IndicatorFillRange(_localFindMatches[i].Start, _localFindMatches[i].Length);

        if (_localFindMatches.Count > 0)
        {
            _localFindIndex = 0;
            if (jumpFirst) JumpLocalMatch(0);
        }
        string capHint = _localFindMatches.Count >= 5000 ? "+" : "";
        TxtFindCount.Text = _localFindMatches.Count == 0 ? "0/0" : $"{_localFindIndex + 1}/{_localFindMatches.Count}{capHint}";
    }

    private void JumpLocalMatch(int index)
    {
        if (_localFindCacheStale) UpdateLocalFindMatches(jumpFirst: false);
        if (_localFindMatches.Count == 0) return;

        _localFindIndex = (index + _localFindMatches.Count) % _localFindMatches.Count;
        var (start, len) = _localFindMatches[_localFindIndex];
        int lineIdx = _scintilla.LineFromPosition(start);
        _scintilla.Lines[lineIdx].EnsureVisible();
        _scintilla.FirstVisibleLine = Math.Max(0, lineIdx - Math.Max(1, _scintilla.LinesOnScreen / 2));

        _scintilla.IndicatorCurrent = 1;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);
        _scintilla.IndicatorFillRange(start, len);
        _scintilla.SetSelection(start + len, start);

        string capHint = _localFindMatches.Count >= 5000 ? "+" : "";
        TxtFindCount.Text = $"{_localFindIndex + 1}/{_localFindMatches.Count}{capHint}";
    }

    private void TxtFind_TextChanged(object sender, TextChangedEventArgs e) => UpdateLocalFindMatches(jumpFirst: true);
    private void FindOption_Changed(object sender, RoutedEventArgs e) => UpdateLocalFindMatches(jumpFirst: true);
    private void BtnFindNext_Click(object sender, RoutedEventArgs e) => JumpLocalMatch(_localFindIndex + 1);
    private void BtnFindPrev_Click(object sender, RoutedEventArgs e) => JumpLocalMatch(_localFindIndex - 1);
    private void BtnCloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        _scintilla.ClearSearchHighlights();
        _scintilla.Focus();
    }
    private void TxtFind_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { JumpLocalMatch(_localFindIndex + (Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1)); e.Handled = true; }
        else if (e.Key == Key.Escape) { BtnCloseFind_Click(sender, e); e.Handled = true; }
    }
    private void BtnReplaceOne_Click(object sender, RoutedEventArgs e)
    {
        if (_localFindCacheStale) UpdateLocalFindMatches(jumpFirst: false);
        if (_localFindMatches.Count == 0 || _localFindIndex < 0) return;

        var (s, len) = _localFindMatches[_localFindIndex];
        string replacement = TxtReplace.Text;
        if (ChkRegex.IsChecked == true)
        {
            try
            {
                var rx = BuildLocalFindRegex();
                if (rx != null)
                {
                    var m = rx.Match(_scintilla.Text, s);
                    if (m.Success && m.Index == s && m.Length == len)
                        replacement = m.Result(TxtReplace.Text);
                }
            }
            catch { return; }
        }

        _scintilla.SetSelection(s + len, s);
        _scintilla.ReplaceSelection(replacement);
        UpdateLocalFindMatches(jumpFirst: true);
    }
    private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_localFindCacheStale) UpdateLocalFindMatches(jumpFirst: false);
        if (_localFindMatches.Count == 0) return;

        Regex? rx = null;
        if (ChkRegex.IsChecked == true)
        {
            try { rx = BuildLocalFindRegex(); }
            catch { return; }
        }

        _scintilla.BeginUndoAction();
        try
        {
            for (int i = _localFindMatches.Count - 1; i >= 0; i--)
            {
                var (s, len) = _localFindMatches[i];
                string rep = TxtReplace.Text;
                if (rx != null)
                {
                    var m = rx.Match(_scintilla.Text, s);
                    if (m.Success && m.Index == s && m.Length == len)
                        rep = m.Result(TxtReplace.Text);
                }
                _scintilla.SetSelection(s + len, s);
                _scintilla.ReplaceSelection(rep);
            }
        }
        finally { _scintilla.EndUndoAction(); }
        UpdateLocalFindMatches(jumpFirst: false);
    }

    // ===== 树操作与原地增删移 =====

    private TreeNodeVM? GetContextTargetNode() => _contextTargetNode ?? (OutlineTree.SelectedItem as TreeNodeVM);

    private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OutlineTree.SelectedItem is not TreeNodeVM node || node.IsRenaming) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) CreateChildAndRename(node.Id);
            else CreateSiblingAfterAndRename(node);
        }
        else if (e.Key == Key.F2) { e.Handled = true; node.BeginRename(); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteNodeWithConfirm(node); }
        else if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) OutdentNode(node);
            else IndentNode(node);
        }
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            _contextTargetNode = item.DataContext as TreeNodeVM;
            e.Handled = false;
        }
    }

    private void OutlineTreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (_contextTargetNode == null && OutlineTree.SelectedItem is TreeNodeVM sel)
            _contextTargetNode = sel;
    }

    private void TreeViewItem_ExpandedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item && item.DataContext is TreeNodeVM vm) _db.SetNodeExpanded(vm.Id, vm.IsExpanded);
    }
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }), DispatcherPriority.Input);
    }
    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TreeNodeVM vm) return;
        if (e.Key == Key.Enter)
        {
            CommitTreeNodeRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRename();
            e.Handled = true;
        }
    }
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TreeNodeVM vm && vm.IsRenaming)
            CommitTreeNodeRename(vm);
    }

    private void CommitTreeNodeRename(TreeNodeVM vm)
    {
        string finalTitle = vm.CommitRename();
        _db.RenameNode(vm.Id, finalTitle);
        if (_selectedNodeId == vm.Id)
        {
            _suppressEditorEvents = true;
            try { TxtNodeTitle.Text = finalTitle; }
            finally { _suppressEditorEvents = false; }
        }
    }

    private ObservableCollection<TreeNodeVM> GetSiblingCollection(long parentId) =>
        parentId > 0 && _nodeLookup.TryGetValue(parentId, out var p) ? p.Children : RootNodes;

    private void InsertVmOrdered(TreeNodeVM vm)
    {
        var col = GetSiblingCollection(vm.ParentId);
        int idx = 0;
        while (idx < col.Count && col[idx].SortOrder <= vm.SortOrder) idx++;
        col.Insert(idx, vm);
    }

    private void CreateChildAndRename(long parentId)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var created = _db.CreateNode(parentId, "新节点");
        var vm = new TreeNodeVM
        {
            Id = created.Id, ParentId = created.ParentId, Title = created.Title,
            SortOrder = created.SortOrder, IsExpanded = created.IsExpanded
        };
        _nodeLookup[vm.Id] = vm;
        if (parentId > 0 && _nodeLookup.TryGetValue(parentId, out var parentVm))
            parentVm.IsExpanded = true;

        InsertVmOrdered(vm);
        SelectTreeNode(vm.Id);
        Dispatcher.BeginInvoke(new Action(() => vm.BeginRename()), DispatcherPriority.Loaded);
    }

    private void CreateSiblingAfterAndRename(TreeNodeVM target)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var sibs = GetSiblingCollection(target.ParentId);
        int idx = sibs.IndexOf(target);
        double order = idx >= 0 && idx + 1 < sibs.Count ? (target.SortOrder + sibs[idx + 1].SortOrder) / 2.0 : target.SortOrder + 1000.0;
        var created = _db.CreateNode(target.ParentId, "新节点", order);
        var vm = new TreeNodeVM
        {
            Id = created.Id, ParentId = created.ParentId, Title = created.Title,
            SortOrder = created.SortOrder, IsExpanded = created.IsExpanded
        };
        _nodeLookup[vm.Id] = vm;
        InsertVmOrdered(vm);
        SelectTreeNode(vm.Id);
        Dispatcher.BeginInvoke(new Action(() => vm.BeginRename()), DispatcherPriority.Loaded);
    }

    private bool IsSameOrDescendant(long candidateId, long ancestorId)
    {
        long cur = candidateId;
        int guard = 0;
        while (cur > 0 && guard++ < 100)
        {
            if (cur == ancestorId) return true;
            if (!_nodeLookup.TryGetValue(cur, out var vm)) break;
            cur = vm.ParentId;
        }
        return false;
    }

    private void RemoveSubtreeFromLookup(TreeNodeVM node)
    {
        foreach (var child in node.Children) RemoveSubtreeFromLookup(child);
        _nodeLookup.Remove(node.Id);
    }

    private void DeleteNodeWithConfirm(TreeNodeVM node)
    {
        var msg = node.Children.Count > 0 ? $"确定删除「{node.Title}」及其全部子节点吗？" : $"确定删除节点「{node.Title}」吗？";
        if (ShowModalMessage(msg, "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        bool affectsCurrent = IsSameOrDescendant(_selectedNodeId, node.Id);
        long fallbackSelectId = 0;
        if (affectsCurrent)
        {
            var sibs = GetSiblingCollection(node.ParentId);
            int idx = sibs.IndexOf(node);
            if (idx + 1 < sibs.Count) fallbackSelectId = sibs[idx + 1].Id;
            else if (idx > 0) fallbackSelectId = sibs[idx - 1].Id;
            else fallbackSelectId = node.ParentId;
            SetDirtyState(false);
        }

        _db.DeleteNode(node.Id);
        GetSiblingCollection(node.ParentId).Remove(node);
        RemoveSubtreeFromLookup(node);
        if (_contextTargetNode?.Id == node.Id) _contextTargetNode = null;

        if (affectsCurrent)
        {
            if (fallbackSelectId > 0 && _nodeLookup.ContainsKey(fallbackSelectId))
                SelectTreeNode(fallbackSelectId);
            else if (RootNodes.Count > 0)
                SelectTreeNode(RootNodes[0].Id);
            else
                ShowEmptyState();
        }
    }

    private void MoveNodeInMemory(TreeNodeVM node, long newParentId, double newSortOrder)
    {
        GetSiblingCollection(node.ParentId).Remove(node);
        node.ParentId = newParentId;
        node.SortOrder = newSortOrder;
        if (newParentId > 0 && _nodeLookup.TryGetValue(newParentId, out var np))
            np.IsExpanded = true;
        InsertVmOrdered(node);
    }

    private void IndentNode(TreeNodeVM node)
    {
        var sibs = GetSiblingCollection(node.ParentId);
        int idx = sibs.IndexOf(node);
        if (idx <= 0) return;
        var np = sibs[idx - 1];
        double order = np.Children.Count > 0 ? np.Children[np.Children.Count - 1].SortOrder + 1000.0 : 1000.0;
        _db.MoveNode(node.Id, np.Id, order);
        MoveNodeInMemory(node, np.Id, order);
        SelectTreeNode(node.Id);
    }

    private void OutdentNode(TreeNodeVM node)
    {
        if (node.ParentId <= 0 || !_nodeLookup.TryGetValue(node.ParentId, out var parent)) return;
        var gs = GetSiblingCollection(parent.ParentId);
        int pIdx = gs.IndexOf(parent);
        double order = pIdx + 1 < gs.Count ? (parent.SortOrder + gs[pIdx + 1].SortOrder) / 2.0 : parent.SortOrder + 1000.0;
        _db.MoveNode(node.Id, parent.ParentId, order);
        MoveNodeInMemory(node, parent.ParentId, order);
        SelectTreeNode(node.Id);
    }

    private void OutlineTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggingNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as TreeNodeVM;
    }
    private void OutlineTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingNode == null || _draggingNode.IsRenaming) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(OutlineTree, new DataObject(typeof(TreeNodeVM), _draggingNode), DragDropEffects.Move);
            _draggingNode = null;
        }
    }
    private void OutlineTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TreeNodeVM)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void OutlineTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TreeNodeVM))) return;
        var src = (TreeNodeVM)e.Data.GetData(typeof(TreeNodeVM))!;
        var targetItem = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not TreeNodeVM target || target.Id == src.Id) return;

        var header = targetItem.Template?.FindName("PART_Header", targetItem) as FrameworkElement;
        double headerHeight = (header != null && header.ActualHeight > 0) ? header.ActualHeight : 26.0;
        double relY = header != null ? e.GetPosition(header).Y : e.GetPosition(targetItem).Y;
        double ratio = Math.Max(0.0, Math.Min(1.0, relY / Math.Max(1.0, headerHeight)));

        long newPid = target.ParentId;
        double newOrder;
        if (ratio < 0.25)
        {
            var sibs = GetSiblingCollection(newPid).Where(x => x.Id != src.Id).ToList();
            int idx = sibs.FindIndex(x => x.Id == target.Id);
            newOrder = ((idx > 0 ? sibs[idx - 1].SortOrder : target.SortOrder - 1000.0) + target.SortOrder) / 2.0;
        }
        else if (ratio > 0.75)
        {
            var sibs = GetSiblingCollection(newPid).Where(x => x.Id != src.Id).ToList();
            int idx = sibs.FindIndex(x => x.Id == target.Id);
            newOrder = (target.SortOrder + (idx + 1 < sibs.Count ? sibs[idx + 1].SortOrder : target.SortOrder + 1000.0)) / 2.0;
        }
        else
        {
            newPid = target.Id;
            newOrder = target.Children.Count > 0 ? target.Children[target.Children.Count - 1].SortOrder + 1000.0 : 1000.0;
        }

        try
        {
            _db.MoveNode(src.Id, newPid, newOrder);
            MoveNodeInMemory(src, newPid, newOrder);
            if (src.Id == _selectedNodeId) SelectTreeNode(src.Id);
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(OutlineTree_Drop), ex);
            ShowModalMessage(ex.Message, "移动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? cur) where T : DependencyObject
    {
        while (cur != null) { if (cur is T t) return t; cur = VisualTreeHelper.GetParent(cur); }
        return null;
    }

    // ===== 工具栏、导出、设置浮层、孤儿清理与快捷键 =====

    private void BtnToggleLeft_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLeftTree = !_settings.ShowLeftTree;
        ApplyLeftVisibility(_settings.ShowLeftTree);
        _db.SaveSettings(_settings);
    }

    private void BtnToggleRight_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowRightToc = !_settings.ShowRightToc;
        ApplyRightVisibility(_settings.ShowRightToc);
        _db.SaveSettings(_settings);
    }

    private void BtnNewDb_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        _isModalOpen = true;
        string? picked = null;
        try
        {
            var dlg = new SaveFileDialog { Filter = "LiteList 笔记库 (*.llist)|*.llist", FileName = "new-notes.llist" };
            if (dlg.ShowDialog(this) == true) picked = dlg.FileName;
        }
        finally { _isModalOpen = false; }
        if (string.IsNullOrEmpty(picked)) return;

        try
        {
            if (string.Equals(Path.GetFullPath(picked), _db.CurrentDbPath, StringComparison.OrdinalIgnoreCase))
                _db.Dispose();
            if (File.Exists(picked)) File.Delete(picked);
            OpenDatabase(picked!);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"BtnNewDb_Click: {picked}", ex);
            ShowModalMessage($"创建新笔记库失败：{ex.Message}", "新建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenDb_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        _isModalOpen = true;
        string? picked = null;
        try
        {
            var dlg = new OpenFileDialog { Filter = "LiteList 笔记库 (*.llist)|*.llist|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) picked = dlg.FileName;
        }
        finally { _isModalOpen = false; }
        if (string.IsNullOrEmpty(picked)) return;

        try { OpenDatabase(picked!); }
        catch (Exception ex)
        {
            AppLogger.Error($"BtnOpenDb_Click: {picked}", ex);
            ShowModalMessage(ex.Message, "打开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (BtnExportMenu.ContextMenu != null)
        {
            BtnExportMenu.ContextMenu.PlacementTarget = BtnExportMenu;
            BtnExportMenu.ContextMenu.IsOpen = true;
        }
    }

    private void RunExport(long? nodeId, bool includeChildren)
    {
        if (_isDirty && !EnsureSafeToLeaveCurrent()) return;
        _isModalOpen = true;
        try
        {
            var defaultName = nodeId.HasValue && _nodeLookup.TryGetValue(nodeId.Value, out var vm)
                ? string.Join("_", vm.Title.Split(Path.GetInvalidFileNameChars())) + ".md"
                : Path.GetFileNameWithoutExtension(_db.CurrentDbPath) + ".md";

            var dlg = new SaveFileDialog { Filter = "Markdown 文件 (*.md)|*.md", FileName = defaultName };
            if (dlg.ShowDialog(this) != true) return;

            var copyAtt = MessageBox.Show(this, "是否将引用到的 files/ 图片与附件一并复制到导出目录？",
                "导出附件选项", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            int copied = _db.ExportMarkdown(nodeId, includeChildren, dlg.FileName, _files, copyAtt);
            TxtSaveStatus.Text = copied > 0 ? $"已导出 MD 及 {copied} 个附件" : "已导出 MD";
        }
        catch (Exception ex)
        {
            AppLogger.Error(nameof(RunExport), ex);
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _isModalOpen = false; }
    }

    private void MenuExportSingleNode_Click(object sender, RoutedEventArgs e) { if (_selectedNodeId > 0) RunExport(_selectedNodeId, false); }
    private void MenuExportSubtree_Click(object sender, RoutedEventArgs e) { if (_selectedNodeId > 0) RunExport(_selectedNodeId, true); }
    private void MenuExportAll_Click(object sender, RoutedEventArgs e) => RunExport(null, true);

    private void CtxExportSingleNode_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) RunExport(n.Id, false); }
    private void CtxExportSubtree_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) RunExport(n.Id, true); }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveCurrentNode();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        RbManualSave.IsChecked = _settings.SaveMode == SaveMode.Manual;
        RbIdleAutoSave.IsChecked = _settings.SaveMode == SaveMode.IdleAutoSave;
        CmbBlurAction.SelectedIndex = (int)_settings.BlurAction;
        SldFontSize.Value = _settings.FontSize;
        TxtSettingFontVal.Text = $"{(int)_settings.FontSize} px";
        ChkAutoVacuum.IsChecked = _settings.AutoVacuumOnExit;
        TxtOrphanInfo.Text = "可扫描并清理 files/ 中未被任何笔记引用的残留文件";

        EditorHost.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void BtnCleanOrphanFiles_Click(object sender, RoutedEventArgs e)
    {
        var contents = _db.GetAllNodeContents();
        if (_selectedNodeId > 0) contents.Add(GetCurrentText());

        var scan = _files.ScanOrphanFiles(contents);
        if (scan.FilePaths.Count == 0)
        {
            TxtOrphanInfo.Text = $"扫描完成：当前目录 {scan.TargetDir} 下无孤儿附件。";
            return;
        }

        double mb = scan.TotalBytes / (1024.0 * 1024.0);
        string sizeDesc = mb >= 1.0 ? $"{mb:F2} MB" : $"{Math.Max(1, scan.TotalBytes / 1024)} KB";
        TxtOrphanInfo.Text = $"发现 {scan.FilePaths.Count} 个未引用文件（共 {sizeDesc}）。";

        var ans = ShowModalMessage(
            $"在 {scan.TargetDir} 中发现 {scan.FilePaths.Count} 个未被任何笔记引用的文件（总计 {sizeDesc}）。\n\n是否永久删除这些孤儿附件？",
            "清理孤儿附件", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ans == MessageBoxResult.Yes)
        {
            int count = _files.DeleteOrphanFiles(scan.FilePaths);
            TxtOrphanInfo.Text = $"已清理 {count} 个孤儿附件（释放约 {sizeDesc}）。";
        }
    }

    private void SldFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSettingFontVal != null) TxtSettingFontVal.Text = $"{(int)e.NewValue} px";
    }

    private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (_viewMode == EditorViewMode.Edit) EditorHost.Visibility = Visibility.Visible;
    }

    private void BtnSettingsOk_Click(object sender, RoutedEventArgs e)
    {
        _settings.SaveMode = RbIdleAutoSave.IsChecked == true ? SaveMode.IdleAutoSave : SaveMode.Manual;
        _settings.BlurAction = (BlurAction)Math.Max(0, CmbBlurAction.SelectedIndex);
        _settings.FontSize = SldFontSize.Value;
        _settings.AutoVacuumOnExit = ChkAutoVacuum.IsChecked == true;
        _db.SaveSettings(_settings);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        ApplySettingsToUI();
        if (_viewMode == EditorViewMode.Edit) EditorHost.Visibility = Visibility.Visible;
    }

    private void BtnFontMinus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void BtnFontPlus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void ChangeFontSize(double delta)
    {
        _settings.FontSize = Math.Max(12.0, Math.Min(28.0, _settings.FontSize + delta));
        ApplySettingsToUI();
        _db.SaveSettings(_settings);
    }

    private void PreviewList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) { ChangeFontSize(e.Delta > 0 ? 1 : -1); e.Handled = true; }
    }

    private void BtnViewEdit_Click(object sender, RoutedEventArgs e) => SetEditorViewMode(EditorViewMode.Edit, true);
    private void BtnViewPreview_Click(object sender, RoutedEventArgs e) => SetEditorViewMode(EditorViewMode.Preview);
    private void PreviewList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SetEditorViewMode(EditorViewMode.Edit, true);

    private void BtnAddRoot_Click(object sender, RoutedEventArgs e) => CreateChildAndRename(0);
    private void BtnExpandAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(true); SetTreeExpandAll(RootNodes, true); }
    private void BtnCollapseAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(false); SetTreeExpandAll(RootNodes, false); }
    private static void SetTreeExpandAll(ObservableCollection<TreeNodeVM> nodes, bool expand)
    {
        foreach (var n in nodes) { n.IsExpanded = expand; SetTreeExpandAll(n.Children, expand); }
    }

    private void CtxAddChild_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) CreateChildAndRename(n.Id); }
    private void CtxAddSibling_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) CreateSiblingAfterAndRename(n); }
    private void CtxRename_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) n.BeginRename(); }
    private void CtxDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextTargetNode() is TreeNodeVM n && EnsureSafeToLeaveCurrent())
            ReloadTree(_db.DuplicateNode(n.Id, true).Id);
    }
    private void CtxMoveUp_Click(object sender, RoutedEventArgs e) => StepSibling(true);
    private void CtxMoveDown_Click(object sender, RoutedEventArgs e) => StepSibling(false);
    private void StepSibling(bool up)
    {
        if (GetContextTargetNode() is not TreeNodeVM n) return;
        var sibs = GetSiblingCollection(n.ParentId);
        int idx = sibs.IndexOf(n);
        int t = idx + (up ? -1 : 1);
        if (t < 0 || t >= sibs.Count) return;
        double order = up
            ? ((t > 0 ? sibs[t - 1].SortOrder : sibs[t].SortOrder - 1000.0) + sibs[t].SortOrder) / 2.0
            : (sibs[t].SortOrder + (t + 1 < sibs.Count ? sibs[t + 1].SortOrder : sibs[t].SortOrder + 1000.0)) / 2.0;
        _db.MoveNode(n.Id, n.ParentId, order);
        MoveNodeInMemory(n, n.ParentId, order);
        if (n.Id == _selectedNodeId) SelectTreeNode(n.Id);
    }
    private void CtxSortChildren_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextTargetNode() is not TreeNodeVM n) return;
        _db.SortChildren(n.Id);
        var sorted = n.Children.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id).ToList();
        n.Children.Clear();
        for (int i = 0; i < sorted.Count; i++)
        {
            sorted[i].SortOrder = (i + 1) * 1000.0;
            n.Children.Add(sorted[i]);
        }
    }
    private void CtxDelete_Click(object sender, RoutedEventArgs e) { if (GetContextTargetNode() is TreeNodeVM n) DeleteNodeWithConfirm(n); }

    private void WrapSel(string before, string after, string placeholder)
    {
        EnsureEditorLoaded();
        var sel = _scintilla.SelectedText;
        var inner = string.IsNullOrEmpty(sel) ? placeholder : sel;
        int anchor = _scintilla.SelectionStart;
        _scintilla.ReplaceSelection(before + inner + after);
        _scintilla.SetSelection(anchor + before.Length + inner.Length, anchor + before.Length);
        _scintilla.Focus();
    }

    private void ToggleLinePrefix(string prefix)
    {
        EnsureEditorLoaded();
        var line = _scintilla.Lines[_scintilla.CurrentLine];
        var raw = line.Text.TrimEnd('\r', '\n');
        if (raw.StartsWith(prefix, StringComparison.Ordinal)) _scintilla.DeleteRange(line.Position, prefix.Length);
        else _scintilla.InsertText(line.Position, prefix);
        _scintilla.Focus();
    }

    private void FmtUndo_Click(object sender, RoutedEventArgs e) => _scintilla.Undo();
    private void FmtRedo_Click(object sender, RoutedEventArgs e) => _scintilla.Redo();
    private void FmtHeading_Click(object sender, RoutedEventArgs e)
    {
        EnsureEditorLoaded();
        var line = _scintilla.Lines[_scintilla.CurrentLine];
        var raw = line.Text.TrimEnd('\r', '\n');
        var m = Regex.Match(raw, @"^(#{1,6})\s+");
        if (!m.Success) _scintilla.InsertText(line.Position, "# ");
        else
        {
            int lvl = m.Groups[1].Length;
            _scintilla.SetSelection(line.Position + m.Length, line.Position);
            _scintilla.ReplaceSelection(lvl >= 4 ? "" : new string('#', lvl + 1) + " ");
        }
        _scintilla.Focus();
    }
    private void FmtBold_Click(object sender, RoutedEventArgs e) => WrapSel("**", "**", "加粗文本");
    private void FmtItalic_Click(object sender, RoutedEventArgs e) => WrapSel("*", "*", "斜体文本");
    private void FmtStrike_Click(object sender, RoutedEventArgs e) => WrapSel("~~", "~~", "删除文本");
    private void FmtInlineCode_Click(object sender, RoutedEventArgs e) => WrapSel("`", "`", "code");
    private void FmtCodeBlock_Click(object sender, RoutedEventArgs e) => InsertCodeBlockSnippet();
    private void FmtQuote_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("> ");
    private void FmtTask_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("- [ ] ");
    private void FmtLink_Click(object sender, RoutedEventArgs e) => WrapSel("[", "](https://)", "链接文字");
    private void FmtTable_Click(object sender, RoutedEventArgs e) => InsertSnippet("\r\n| 列1 | 列2 | 列3 |\r\n| --- | --- | --- |\r\n| 内容 | 内容 | 内容 |\r\n");
    private void FmtInsertFile_Click(object sender, RoutedEventArgs e) => OpenFileAndInsertDialog();
    private void FmtFoldAll_Click(object sender, RoutedEventArgs e) { _scintilla.RebuildFoldLevelsFromText(GetCurrentText()); _scintilla.FoldAll(FoldAction.Contract); }
    private void FmtUnfoldAll_Click(object sender, RoutedEventArgs e) => _scintilla.FoldAll(FoldAction.Expand);
    private void FmtFind_Click(object sender, RoutedEventArgs e) => OpenFindBar(false);

    private void PreviewCopyMd_Click(object sender, RoutedEventArgs e)
    {
        if (VirtualizedMarkdownPreview.TrySetClipboardText(GetCurrentText()))
            TxtSaveStatus.Text = "已复制节点 Markdown 原文";
    }
    private void PreviewFoldAll_Click(object sender, RoutedEventArgs e)
    {
        PreviewList.ItemsSource = null;
        foreach (var b in _previewEngine.AllBlocks) if (b.HeadingLevel > 0) b.IsCollapsed = true;
        _previewEngine.RebuildVisibleBlocks();
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
    }
    private void PreviewUnfoldAll_Click(object sender, RoutedEventArgs e)
    {
        PreviewList.ItemsSource = null;
        foreach (var b in _previewEngine.AllBlocks) if (b.HeadingLevel > 0) b.IsCollapsed = false;
        _previewEngine.RebuildVisibleBlocks();
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
    }

    private void Scintilla_ShortcutPressed(WF.Keys keyData)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (SettingsOverlay.Visibility == Visibility.Visible || LightboxOverlay.Visibility == Visibility.Visible) return;

            if (keyData == (WF.Keys.Control | WF.Keys.S)) SaveCurrentNode();
            else if (keyData == (WF.Keys.Control | WF.Keys.K)) OpenGlobalSearch();
            else if (keyData == (WF.Keys.Control | WF.Keys.E)) SetEditorViewMode(EditorViewMode.Preview);
            else if (keyData == (WF.Keys.Control | WF.Keys.F)) OpenFindBar(false);
            else if (keyData == (WF.Keys.Control | WF.Keys.H)) OpenFindBar(true);
            else if (keyData == (WF.Keys.Control | WF.Keys.B)) WrapSel("**", "**", "加粗文本");
            else if (keyData == (WF.Keys.Control | WF.Keys.I)) WrapSel("*", "*", "斜体文本");
            else if (keyData == (WF.Keys.Control | WF.Keys.L)) WrapSel("[", "](https://)", "链接文字");
            else if (keyData == (WF.Keys.Control | WF.Keys.Oem5)) BtnToggleLeft_Click(this, new RoutedEventArgs());
            else if (keyData == (WF.Keys.Control | WF.Keys.OemQuestion)) BtnToggleRight_Click(this, new RoutedEventArgs());
            else if (keyData == (WF.Keys.Control | WF.Keys.Oemplus) || keyData == (WF.Keys.Control | WF.Keys.Add)) ChangeFontSize(1);
            else if (keyData == (WF.Keys.Control | WF.Keys.OemMinus) || keyData == (WF.Keys.Control | WF.Keys.Subtract)) ChangeFontSize(-1);
            else if (keyData == WF.Keys.F3) HandleF3Step(false);
            else if (keyData == (WF.Keys.Shift | WF.Keys.F3)) HandleF3Step(true);
        }));
    }

    private void HandleF3Step(bool backward)
    {
        if (FindBar.Visibility == Visibility.Visible) JumpLocalMatch(_localFindIndex + (backward ? -1 : 1));
        else if (GlobalSearchPanel.Visibility == Visibility.Visible) StepGlobalHit(backward ? -1 : 1);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (LightboxOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) LightboxOverlay_Click(sender, null);
            e.Handled = true;
            return;
        }
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) BtnSettingsCancel_Click(sender, e);
            if (Keyboard.Modifiers == ModifierKeys.Control || e.Key == Key.Escape || e.Key == Key.F3)
                e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: SaveCurrentNode(); e.Handled = true; break;
                case Key.K: OpenGlobalSearch(); e.Handled = true; break;
                case Key.E: SetEditorViewMode(_viewMode == EditorViewMode.Preview ? EditorViewMode.Edit : EditorViewMode.Preview, true); e.Handled = true; break;
                case Key.F: OpenFindBar(false); e.Handled = true; break;
                case Key.H: OpenFindBar(true); e.Handled = true; break;
                case Key.Oem5: BtnToggleLeft_Click(sender, e); e.Handled = true; break;
                case Key.OemQuestion: BtnToggleRight_Click(sender, e); e.Handled = true; break;
                case Key.OemPlus:
                case Key.Add:
                    ChangeFontSize(1); e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract:
                    ChangeFontSize(-1); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.F3)
        {
            HandleF3Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_nodeLookup.Values.Any(x => x.IsRenaming)) return;
            if (FindBar.Visibility == Visibility.Visible) { BtnCloseFind_Click(sender, e); e.Handled = true; }
            else if (GlobalSearchPanel.Visibility == Visibility.Visible) { BtnCloseGlobalSearch_Click(sender, e); e.Handled = true; }
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isModalOpen || SettingsOverlay.Visibility == Visibility.Visible || LightboxOverlay.Visibility == Visibility.Visible)
            return;
        EnsureSafeToLeaveCurrent(isBlur: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!EnsureSafeToLeaveCurrent()) { e.Cancel = true; return; }
        try
        {
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            _idleSaveTimer.Stop();
            _foldAndTocTimer.Stop();
            if (_settings.AutoVacuumOnExit) _db.SmartVacuumIfNeeded();
            _db.Dispose();
            AppLogger.Info("LiteList exited cleanly.");
        }
        catch (Exception ex) { AppLogger.Error(nameof(Window_Closing), ex); }
    }
}

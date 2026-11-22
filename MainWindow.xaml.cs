using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

/// <summary>
/// 虚拟化预览列表的按需容器：进入屏幕时构建控件，滑出屏幕后立即释放内存
/// </summary>
public sealed class PreviewBlockPresenter : Border
{
    public static readonly DependencyProperty BlockProperty = DependencyProperty.Register(
        nameof(Block), typeof(PreviewBlockVM), typeof(PreviewBlockPresenter),
        new PropertyMetadata(null, OnBlockChanged));

    internal static Func<PreviewBlockVM, UIElement>? ActiveBuilder;

    public PreviewBlockVM? Block
    {
        get => (PreviewBlockVM?)GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public PreviewBlockPresenter()
    {
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => Child = null;
    }

    private static void OnBlockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PreviewBlockPresenter p && p.IsLoaded) p.Rebuild();
    }

    public void Rebuild()
    {
        Child = (Block != null && ActiveBuilder != null) ? ActiveBuilder(Block) : null;
    }
}

public partial class MainWindow : Window
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.DispatcherUnhandledException += (_, eArgs) =>
        {
            MessageBox.Show($"发生异常：{eArgs.Exception.Message}", "LiteList", MessageBoxButton.OK, MessageBoxImage.Error);
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
    private readonly DispatcherTimer _globalSearchTimer;

    private AppSettings _settings = new();
    private EditorViewMode _viewMode = EditorViewMode.Preview;
    private readonly Dictionary<long, TreeNodeVM> _nodeLookup = new();
    public ObservableCollection<TreeNodeVM> RootNodes { get; } = new();

    private long _selectedNodeId;
    private bool _isDirty;
    private bool _suppressEditorEvents;
    private bool _suppressSelectionGuard;
    private bool _isModalOpen;

    private readonly List<(int Start, int Length)> _localFindMatches = new();
    private int _localFindIndex = -1;

    private List<SearchHit> _globalHits = new();
    private int _globalHitIndex = -1;
    private string _activeGlobalKeyword = "";
    private bool _suppressGlobalHitSync;

    private Point _dragStartPoint;
    private TreeNodeVM? _draggingNode;

    public MainWindow(string? initialDbPath)
    {
        InitializeComponent();
        OutlineTree.ItemsSource = RootNodes;

        _previewEngine = new VirtualizedMarkdownPreview(_files);
        PreviewBlockPresenter.ActiveBuilder = vm =>
            _previewEngine.BuildBlockElement(vm, _settings.FontSize,
                GlobalSearchPanel.Visibility == Visibility.Visible ? _activeGlobalKeyword : null);
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;

        _previewEngine.HeadingFoldToggled += _ => _previewEngine.RebuildVisibleBlocks();
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

        _globalSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _globalSearchTimer.Tick += (_, _) => { _globalSearchTimer.Stop(); RunGlobalSearch(jumpFirst: false); };

        var targetDb = initialDbPath ?? _db.ResolveDefaultDbPath(_files.AppBaseDir);
        OpenDatabase(targetDb);
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
        _scintilla.MouseWheel += (_, e) =>
        {
            if ((WF.Control.ModifierKeys & WF.Keys.Control) != 0)
                Dispatcher.BeginInvoke(new Action(() => ChangeFontSize(e.Delta > 0 ? 1 : -1)));
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
        Add("插入代码块", () => InsertSnippet("\r\n```text\r\n\r\n```\r\n"));
        Add("插入待办 - [ ]", () => ToggleLinePrefix("- [ ] "));
        Add("插入表格", () => InsertSnippet("\r\n| 列1 | 列2 | 列3 |\r\n| --- | --- | --- |\r\n| 内容 | 内容 | 内容 |\r\n"));
        Add("插入图片 / 附件...", OpenFileAndInsertDialog);
        menu.Items.Add(new WF.ToolStripSeparator());
        Add("查找与替换 (Ctrl+F)", () => OpenFindBar(false));
        Add("折叠全部标题与缩进块", () => { _scintilla.RebuildFoldLevels(); _scintilla.FoldAll(FoldAction.Contract); });
        Add("展开全部折叠", () => _scintilla.FoldAll(FoldAction.Expand));
        return menu;
    }

    // ===== 数据库打开、设置应用与树构建 =====

    private void OpenDatabase(string dbPath)
    {
        _db.OpenOrCreate(dbPath);
        _files.CurrentDbDir = Path.GetDirectoryName(_db.CurrentDbPath);
        TxtDbBadge.Text = Path.GetFileName(_db.CurrentDbPath);
        TxtDbBadge.ToolTip = _db.CurrentDbPath;
        Title = $"LiteList - {TxtDbBadge.Text}";
        TxtFilesDirInfo.Text = $"附件目录: {_files.GetTargetFilesDir()}";

        _settings = _db.LoadSettings();
        ApplySettingsToUI();
        ReloadTree(selectId: null);
    }

    private void ApplySettingsToUI()
    {
        _scintilla.ApplyThemeAndFont(_settings.FontSize);
        ApplyLeftVisibility(_settings.ShowLeftTree);
        ApplyRightVisibility(_settings.ShowRightToc);
        if (_viewMode == EditorViewMode.Preview) RefreshPreviewFromEditorText();
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
        _selectedNodeId = 0;
        _isDirty = false;
        EmptyOverlay.Visibility = Visibility.Visible;
        EditorHeaderBar.Visibility = Visibility.Collapsed;
        FormatToolBar.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Collapsed;
        PreviewList.Visibility = Visibility.Collapsed;
        RightTocTree.ItemsSource = null;
    }

    private void SelectTreeNode(long nodeId)
    {
        if (!_nodeLookup.TryGetValue(nodeId, out var target)) return;
        var cur = target;
        while (cur.ParentId > 0 && _nodeLookup.TryGetValue(cur.ParentId, out var p))
        {
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

        _isModalOpen = true;
        try
        {
            var title = _nodeLookup.TryGetValue(_selectedNodeId, out var vm) ? vm.Title : "当前节点";
            var res = MessageBox.Show(this, $"节点「{title}」有未保存的修改，是否保存？",
                "未保存提醒", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes) { SaveCurrentNode(); return true; }
            if (res == MessageBoxResult.No) { SetDirtyState(false); return true; }
            return false;
        }
        finally { _isModalOpen = false; }
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
            _scintilla.Text = detail.Content;
            _scintilla.EmptyUndoBuffer();
            _scintilla.UpdateLineNumberMarginWidth();
            _scintilla.RebuildFoldLevels();
            SetDirtyState(false);
        }
        finally { _suppressEditorEvents = false; }

        UpdateCursorStatus();

        // 只有在右侧正文大纲开启时才构建标题树，打字过程中绝不触发
        if (_settings.ShowRightToc) RefreshRightTocTree();

        var targetMode = preserveExactMode
            ? _viewMode
            : (string.IsNullOrWhiteSpace(detail.Content) && _viewMode == EditorViewMode.Preview
                ? EditorViewMode.Edit : _viewMode);
        SetEditorViewMode(targetMode, focusEditor: false);
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
        _db.SaveNode(_selectedNodeId, title, _scintilla.Text);
        if (_nodeLookup.TryGetValue(_selectedNodeId, out var vm)) vm.Title = title;
        SetDirtyState(false);
    }

    // ===== 纯净双视图（编辑 / 预览）与虚拟化渲染 =====

    private void SetEditorViewMode(EditorViewMode mode, bool focusEditor = false)
    {
        _viewMode = mode;
        BtnViewEdit.FontWeight = mode == EditorViewMode.Edit ? FontWeights.Bold : FontWeights.Normal;
        BtnViewPreview.FontWeight = mode == EditorViewMode.Preview ? FontWeights.Bold : FontWeights.Normal;

        bool isOverlayOpen = SettingsOverlay.Visibility == Visibility.Visible || LightboxOverlay.Visibility == Visibility.Visible;
        FormatToolBar.Visibility = mode == EditorViewMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        EditorHost.Visibility = (mode == EditorViewMode.Edit && !isOverlayOpen) ? Visibility.Visible : Visibility.Collapsed;
        PreviewList.Visibility = mode == EditorViewMode.Preview ? Visibility.Visible : Visibility.Collapsed;

        if (mode == EditorViewMode.Preview)
        {
            FindBar.Visibility = Visibility.Collapsed;
            RefreshPreviewFromEditorText();
        }
        else if (focusEditor && !isOverlayOpen)
        {
            _scintilla.Focus();
        }
    }

    private void RefreshPreviewFromEditorText()
    {
        if (_selectedNodeId <= 0) return;
        _previewEngine.ParseDocument(_scintilla.Text);
        PreviewList.ItemsSource = null;
        PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
    }

    private void RefreshRightTocTree()
    {
        if (_selectedNodeId <= 0) { RightTocTree.ItemsSource = null; return; }
        RightTocTree.ItemsSource = _viewMode == EditorViewMode.Preview
            ? _previewEngine.ExtractHeadingTree()
            : _scintilla.ExtractHeadingTree();
    }

    // ===== 编辑事件（零全文大纲扫描，流畅不卡） =====

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
        _scintilla.UpdateLineNumberMarginWidth();
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
        int pos = _scintilla.CurrentPosition;
        int line = _scintilla.LineFromPosition(pos) + 1;
        int col = _scintilla.GetColumn(pos) + 1;
        TxtCursorInfo.Text = $"行 {line}, 列 {col}";
        TxtDocStats.Text = $"{_scintilla.TextLength} 字 · {_scintilla.Lines.Count} 行";
    }

    // ===== 右侧树形正文大纲联动 =====

    private void RightTocTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not HeadingNodeVM h) return;
        if (_viewMode == EditorViewMode.Edit)
        {
            _scintilla.ScrollToLineCentered(h.LineNumber);
            _scintilla.Focus();
        }
        else
        {
            var blk = _previewEngine.EnsureLineVisibleAndFindBlock(h.LineNumber);
            if (blk != null) PreviewList.ScrollIntoView(blk);
        }
    }

    private void BtnRefreshToc_Click(object sender, RoutedEventArgs e)
    {
        if (_viewMode == EditorViewMode.Edit) _scintilla.RebuildFoldLevels();
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

    // ===== ListPro 风格：Ctrl+K 常驻全局搜索（严格保留当前编辑/预览模式） =====

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
        _scintilla.ClearSearchHighlights();
        if (_viewMode == EditorViewMode.Preview) RefreshPreviewFromEditorText();
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

        _globalHits = _db.SearchOccurrences(kw);
        LstGlobalHits.ItemsSource = _globalHits;
        _globalHitIndex = -1;

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

        // 切到不同节点前检查未保存提示
        if (hit.NodeId != _selectedNodeId)
        {
            if (!EnsureSafeToLeaveCurrent()) return;
            _suppressSelectionGuard = true;
            try
            {
                SelectTreeNode(hit.NodeId);
                // 严格保留当前编辑/预览模式，不擅自切换模式
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
            _scintilla.HighlightAndScrollToMatch(_activeGlobalKeyword, targetLine, hit.CharOffsetInLine, hit.MatchLength);
        }
        else
        {
            // 全程保持预览模式：高亮关键词 + 展开所在标题章节 + 精准滚入该块
            foreach (var b in _previewEngine.AllBlocks) b.IsSearchActive = false;
            var targetBlock = _previewEngine.EnsureLineVisibleAndFindBlock(targetLine);
            if (targetBlock != null) targetBlock.IsSearchActive = true;

            PreviewList.ItemsSource = null;
            PreviewList.ItemsSource = _previewEngine.VisibleBlocks;
            if (targetBlock != null) PreviewList.ScrollIntoView(targetBlock);
        }
    }

    // ===== 粘贴、拖放图片与附件（自动存至 files/） =====

    private void Scintilla_PasteIntercepted(object? sender, HandledEventArgs e)
    {
        if (Clipboard.ContainsImage())
        {
            var img = Clipboard.GetImage();
            if (img != null)
            {
                e.Handled = true;
                var res = _files.SaveClipboardImage(img);
                InsertSnippet($"![{res.Filename}]({res.RelPath})\r\n");
                return;
            }
        }
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
        }
    }

    private void ImportFilesAndInsert(IEnumerable<string> paths)
    {
        var lines = new List<string>();
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var res = _files.ImportExternalFile(p);
            lines.Add(res.IsImage ? $"![{res.Filename}]({res.RelPath})" : $"[{res.Filename}]({res.RelPath})");
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
        _scintilla.ReplaceSelection(text);
        _scintilla.Focus();
    }

    private void PreviewEngine_TaskCheckboxToggled(int lineNum, bool isChecked)
    {
        int idx = lineNum - 1;
        if (idx < 0 || idx >= _scintilla.Lines.Count) return;
        var line = _scintilla.Lines[idx];
        var raw = line.Text.TrimEnd('\r', '\n');
        var updated = isChecked ? Regex.Replace(raw, @"\[ \]", "[x]") : Regex.Replace(raw, @"\[[xX]\]", "[ ]");
        _scintilla.SetSelection(line.Position + raw.Length, line.Position);
        _scintilla.ReplaceSelection(updated);
        RefreshPreviewFromEditorText();
    }

    private void ShowImageLightbox(string fullPath)
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

    private void LightboxOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        LightboxOverlay.Visibility = Visibility.Collapsed;
        if (_viewMode == EditorViewMode.Edit) EditorHost.Visibility = Visibility.Visible;
    }

    private void OpenAttachmentFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null) { MessageBox.Show($"在 files 目录中找不到文件：{relPath}"); return; }
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }

    private void SaveAttachmentAsFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null) { MessageBox.Show($"在 files 目录中找不到文件：{relPath}"); return; }
        _isModalOpen = true;
        var dlg = new SaveFileDialog { FileName = Path.GetFileName(full), Title = "附件另存为" };
        if (dlg.ShowDialog(this) == true) File.Copy(full, dlg.FileName, overwrite: true);
        _isModalOpen = false;
    }

    private void OpenExternalUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ===== 节点内查找与替换（C# Regex Unicode 安全 + 统一主题控件） =====

    private void OpenFindBar(bool focusReplace)
    {
        if (_viewMode == EditorViewMode.Preview) SetEditorViewMode(EditorViewMode.Edit, true);
        FindBar.Visibility = Visibility.Visible;
        var sel = _scintilla.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains("\n")) TxtFind.Text = sel;
        UpdateLocalFindMatches(jumpFirst: false);
        if (focusReplace) TxtReplace.Focus();
        else { TxtFind.Focus(); TxtFind.SelectAll(); }
    }

    private void UpdateLocalFindMatches(bool jumpFirst)
    {
        _localFindMatches.Clear();
        _localFindIndex = -1;
        _scintilla.ClearSearchHighlights();

        var pattern = TxtFind.Text;
        if (string.IsNullOrEmpty(pattern)) { TxtFindCount.Text = "0/0"; return; }

        try
        {
            var opts = ChkCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            var rx = new Regex(ChkRegex.IsChecked == true ? pattern : Regex.Escape(pattern), opts);
            foreach (Match m in rx.Matches(_scintilla.Text))
                if (m.Length > 0) _localFindMatches.Add((m.Index, m.Length));
        }
        catch { TxtFindCount.Text = "正则错"; return; }

        _scintilla.IndicatorCurrent = 0;
        foreach (var (s, len) in _localFindMatches)
            _scintilla.IndicatorFillRange(s, len);

        if (_localFindMatches.Count > 0)
        {
            _localFindIndex = 0;
            if (jumpFirst) JumpLocalMatch(0);
        }
        TxtFindCount.Text = _localFindMatches.Count == 0 ? "0/0" : $"{_localFindIndex + 1}/{_localFindMatches.Count}";
    }

    private void JumpLocalMatch(int index)
    {
        if (_localFindMatches.Count == 0) return;
        _localFindIndex = (index + _localFindMatches.Count) % _localFindMatches.Count;
        var (start, len) = _localFindMatches[_localFindIndex];
        int lineIdx = _scintilla.LineFromPosition(start);
        _scintilla.Lines[lineIdx].EnsureVisible();
        _scintilla.FirstVisibleLine = Math.Max(0, lineIdx - Math.Max(1, _scintilla.LinesOnScreen / 2));
        _scintilla.SetSelection(start + len, start);
        TxtFindCount.Text = $"{_localFindIndex + 1}/{_localFindMatches.Count}";
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
        if (_localFindMatches.Count == 0 || _localFindIndex < 0) return;
        var (s, len) = _localFindMatches[_localFindIndex];
        _scintilla.SetSelection(s + len, s);
        _scintilla.ReplaceSelection(TxtReplace.Text);
        UpdateLocalFindMatches(jumpFirst: true);
    }
    private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_localFindMatches.Count == 0) return;
        _scintilla.BeginUndoAction();
        for (int i = _localFindMatches.Count - 1; i >= 0; i--)
        {
            var (s, len) = _localFindMatches[i];
            _scintilla.SetSelection(s + len, s);
            _scintilla.ReplaceSelection(TxtReplace.Text);
        }
        _scintilla.EndUndoAction();
        UpdateLocalFindMatches(jumpFirst: false);
    }

    // ===== 树操作与拖拽 =====

    private void OutlineTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OutlineTree.SelectedItem is not TreeNodeVM node || node.IsRenaming) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) CreateChildAndRename(node.Id);
            else CreateSiblingAfterAndRename(node);
        }
        else if (e.Key == Key.F2) { e.Handled = true; node.IsRenaming = true; }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteNodeWithConfirm(node); }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) OutdentNode(node);
            else IndentNode(node);
        }
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item) { item.Focus(); item.IsSelected = true; }
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
    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TreeNodeVM vm && (e.Key == Key.Enter || e.Key == Key.Escape))
        {
            vm.IsRenaming = false;
            _db.RenameNode(vm.Id, vm.Title);
            if (_selectedNodeId == vm.Id) TxtNodeTitle.Text = vm.Title;
            e.Handled = true;
        }
    }
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TreeNodeVM vm && vm.IsRenaming)
        {
            vm.IsRenaming = false;
            _db.RenameNode(vm.Id, vm.Title);
            if (_selectedNodeId == vm.Id) TxtNodeTitle.Text = vm.Title;
        }
    }

    private void CreateChildAndRename(long parentId)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var created = _db.CreateNode(parentId, "新节点");
        ReloadTree(created.Id);
        if (_nodeLookup.TryGetValue(created.Id, out var vm)) vm.IsRenaming = true;
    }

    private void CreateSiblingAfterAndRename(TreeNodeVM target)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var sibs = target.ParentId > 0 && _nodeLookup.TryGetValue(target.ParentId, out var p) ? p.Children : RootNodes;
        int idx = sibs.IndexOf(target);
        double order = idx >= 0 && idx + 1 < sibs.Count ? (target.SortOrder + sibs[idx + 1].SortOrder) / 2.0 : target.SortOrder + 1000.0;
        var created = _db.CreateNode(target.ParentId, "新节点", order);
        ReloadTree(created.Id);
        if (_nodeLookup.TryGetValue(created.Id, out var vm)) vm.IsRenaming = true;
    }

    private void DeleteNodeWithConfirm(TreeNodeVM node)
    {
        _isModalOpen = true;
        var msg = node.Children.Count > 0 ? $"确定删除「{node.Title}」及其全部子节点吗？" : $"确定删除节点「{node.Title}」吗？";
        bool ok = MessageBox.Show(this, msg, "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        _isModalOpen = false;
        if (!ok) return;
        SetDirtyState(false);
        _db.DeleteNode(node.Id);
        ReloadTree(null);
    }

    private void IndentNode(TreeNodeVM node)
    {
        var sibs = node.ParentId > 0 && _nodeLookup.TryGetValue(node.ParentId, out var p) ? p.Children : RootNodes;
        int idx = sibs.IndexOf(node);
        if (idx <= 0) return;
        var np = sibs[idx - 1];
        double order = np.Children.Count > 0 ? np.Children[np.Children.Count - 1].SortOrder + 1000.0 : 1000.0;
        _db.MoveNode(node.Id, np.Id, order);
        ReloadTree(node.Id);
    }

    private void OutdentNode(TreeNodeVM node)
    {
        if (node.ParentId <= 0 || !_nodeLookup.TryGetValue(node.ParentId, out var parent)) return;
        var gs = parent.ParentId > 0 && _nodeLookup.TryGetValue(parent.ParentId, out var gp) ? gp.Children : RootNodes;
        int pIdx = gs.IndexOf(parent);
        double order = pIdx + 1 < gs.Count ? (parent.SortOrder + gs[pIdx + 1].SortOrder) / 2.0 : parent.SortOrder + 1000.0;
        _db.MoveNode(node.Id, parent.ParentId, order);
        ReloadTree(node.Id);
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

        double ratio = e.GetPosition(targetItem).Y / Math.Max(1, targetItem.ActualHeight);
        long newPid = target.ParentId;
        double newOrder;
        if (ratio < 0.25)
        {
            var sibs = (newPid > 0 && _nodeLookup.TryGetValue(newPid, out var p) ? p.Children : RootNodes).Where(x => x.Id != src.Id).ToList();
            int idx = sibs.FindIndex(x => x.Id == target.Id);
            newOrder = ((idx > 0 ? sibs[idx - 1].SortOrder : target.SortOrder - 1000.0) + target.SortOrder) / 2.0;
        }
        else if (ratio > 0.75)
        {
            var sibs = (newPid > 0 && _nodeLookup.TryGetValue(newPid, out var p) ? p.Children : RootNodes).Where(x => x.Id != src.Id).ToList();
            int idx = sibs.FindIndex(x => x.Id == target.Id);
            newOrder = (target.SortOrder + (idx + 1 < sibs.Count ? sibs[idx + 1].SortOrder : target.SortOrder + 1000.0)) / 2.0;
        }
        else
        {
            newPid = target.Id;
            newOrder = target.Children.Count > 0 ? target.Children[target.Children.Count - 1].SortOrder + 1000.0 : 1000.0;
        }
        try { _db.MoveNode(src.Id, newPid, newOrder); ReloadTree(src.Id); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private static T? FindAncestor<T>(DependencyObject? cur) where T : DependencyObject
    {
        while (cur != null) { if (cur is T t) return t; cur = VisualTreeHelper.GetParent(cur); }
        return null;
    }

    // ===== 工具栏、导出、设置浮层与快捷键 =====

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
        var dlg = new SaveFileDialog { Filter = "LiteList 笔记库 (*.llist)|*.llist", FileName = "new-notes.llist" };
        bool ok = dlg.ShowDialog(this) == true;
        _isModalOpen = false;
        if (!ok) return;
        if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
        OpenDatabase(dlg.FileName);
    }

    private void BtnOpenDb_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        _isModalOpen = true;
        var dlg = new OpenFileDialog { Filter = "LiteList 笔记库 (*.llist)|*.llist|所有文件 (*.*)|*.*" };
        bool ok = dlg.ShowDialog(this) == true;
        _isModalOpen = false;
        if (!ok) return;
        try { OpenDatabase(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "打开失败"); }
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
        finally { _isModalOpen = false; }
    }

    private void MenuExportSingleNode_Click(object sender, RoutedEventArgs e) { if (_selectedNodeId > 0) RunExport(_selectedNodeId, false); }
    private void MenuExportSubtree_Click(object sender, RoutedEventArgs e) { if (_selectedNodeId > 0) RunExport(_selectedNodeId, true); }
    private void MenuExportAll_Click(object sender, RoutedEventArgs e) => RunExport(null, true);
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveCurrentNode();

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        RbManualSave.IsChecked = _settings.SaveMode == SaveMode.Manual;
        RbIdleAutoSave.IsChecked = _settings.SaveMode == SaveMode.IdleAutoSave;
        CmbBlurAction.SelectedIndex = (int)_settings.BlurAction;
        SldFontSize.Value = _settings.FontSize;
        TxtSettingFontVal.Text = $"{(int)_settings.FontSize} px";
        ChkAutoVacuum.IsChecked = _settings.AutoVacuumOnExit;

        EditorHost.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;
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
    private void BtnExpandAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(true); ReloadTree(_selectedNodeId); }
    private void BtnCollapseAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(false); ReloadTree(_selectedNodeId); }

    private void CtxAddChild_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) CreateChildAndRename(n.Id); }
    private void CtxAddSibling_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) CreateSiblingAfterAndRename(n); }
    private void CtxRename_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) n.IsRenaming = true; }
    private void CtxDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (OutlineTree.SelectedItem is TreeNodeVM n && EnsureSafeToLeaveCurrent())
            ReloadTree(_db.DuplicateNode(n.Id, true).Id);
    }
    private void CtxMoveUp_Click(object sender, RoutedEventArgs e) => StepSibling(true);
    private void CtxMoveDown_Click(object sender, RoutedEventArgs e) => StepSibling(false);
    private void StepSibling(bool up)
    {
        if (OutlineTree.SelectedItem is not TreeNodeVM n) return;
        var sibs = n.ParentId > 0 && _nodeLookup.TryGetValue(n.ParentId, out var p) ? p.Children : RootNodes;
        int idx = sibs.IndexOf(n);
        int t = idx + (up ? -1 : 1);
        if (t < 0 || t >= sibs.Count) return;
        double order = up
            ? ((t > 0 ? sibs[t - 1].SortOrder : sibs[t].SortOrder - 1000.0) + sibs[t].SortOrder) / 2.0
            : (sibs[t].SortOrder + (t + 1 < sibs.Count ? sibs[t + 1].SortOrder : sibs[t].SortOrder + 1000.0)) / 2.0;
        _db.MoveNode(n.Id, n.ParentId, order);
        ReloadTree(n.Id);
    }
    private void CtxSortChildren_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) { _db.SortChildren(n.Id); ReloadTree(n.Id); } }
    private void CtxDelete_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) DeleteNodeWithConfirm(n); }

    // ===== 排版命令 =====
    private void WrapSel(string before, string after, string placeholder)
    {
        var sel = _scintilla.SelectedText;
        var inner = string.IsNullOrEmpty(sel) ? placeholder : sel;
        int anchor = _scintilla.SelectionStart;
        _scintilla.ReplaceSelection(before + inner + after);
        _scintilla.SetSelection(anchor + before.Length + inner.Length, anchor + before.Length);
        _scintilla.Focus();
    }

    private void ToggleLinePrefix(string prefix)
    {
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
    private void FmtCodeBlock_Click(object sender, RoutedEventArgs e) => InsertSnippet("\r\n```text\r\n\r\n```\r\n");
    private void FmtQuote_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("> ");
    private void FmtTask_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("- [ ] ");
    private void FmtLink_Click(object sender, RoutedEventArgs e) => WrapSel("[", "](https://)", "链接文字");
    private void FmtTable_Click(object sender, RoutedEventArgs e) => InsertSnippet("\r\n| 列1 | 列2 | 列3 |\r\n| --- | --- | --- |\r\n| 内容 | 内容 | 内容 |\r\n");
    private void FmtInsertFile_Click(object sender, RoutedEventArgs e) => OpenFileAndInsertDialog();
    private void FmtFoldAll_Click(object sender, RoutedEventArgs e) { _scintilla.RebuildFoldLevels(); _scintilla.FoldAll(FoldAction.Contract); }
    private void FmtUnfoldAll_Click(object sender, RoutedEventArgs e) => _scintilla.FoldAll(FoldAction.Expand);
    private void FmtFind_Click(object sender, RoutedEventArgs e) => OpenFindBar(false);

    private void PreviewCopyMd_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(_scintilla.Text);
    private void PreviewFoldAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _previewEngine.AllBlocks) if (b.HeadingLevel > 0) b.IsCollapsed = true;
        _previewEngine.RebuildVisibleBlocks();
    }
    private void PreviewUnfoldAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _previewEngine.AllBlocks) if (b.HeadingLevel > 0) b.IsCollapsed = false;
        _previewEngine.RebuildVisibleBlocks();
    }

    // ===== 快捷键统筹（兼容 WPF 与 WinForms Scintilla） =====

    private void Scintilla_ShortcutPressed(WF.Keys keyData)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
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
            else if (keyData == (WF.Keys.Control | WF.Keys.Oemplus)) ChangeFontSize(1);
            else if (keyData == (WF.Keys.Control | WF.Keys.OemMinus)) ChangeFontSize(-1);
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
            }
        }
        else if (e.Key == Key.F3)
        {
            HandleF3Step((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (LightboxOverlay.Visibility == Visibility.Visible) { LightboxOverlay_Click(sender, null!); e.Handled = true; }
            else if (SettingsOverlay.Visibility == Visibility.Visible) { BtnSettingsCancel_Click(sender, e); e.Handled = true; }
            else if (FindBar.Visibility == Visibility.Visible) { BtnCloseFind_Click(sender, e); e.Handled = true; }
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
        if (_settings.AutoVacuumOnExit) _db.SmartVacuumIfNeeded();
        _db.Dispose();
    }
}

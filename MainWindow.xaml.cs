using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;

namespace LiteList;

public partial class MainWindow : Window
{
    private readonly DatabaseService _db = new();
    private readonly FileArchiveService _files = new();
    private readonly MarkdownColorizer _colorizer = new();
    private readonly SearchHighlightRenderer _searchRenderer = new();
    private readonly MarkdownFlowRenderer _flowRenderer;
    private readonly FoldingManager _foldingManager;

    private readonly DispatcherTimer _idleSaveTimer;
    private readonly DispatcherTimer _foldAndTocTimer;
    private readonly DispatcherTimer _splitPreviewTimer;
    private readonly DispatcherTimer _globalSearchTimer;

    private AppSettings _settings = new();
    private EditorViewMode _viewMode = EditorViewMode.Preview;
    private Dictionary<int, Block> _previewHeadingBlocks = new();
    private readonly Dictionary<long, TreeNodeVM> _nodeLookup = new();
    public ObservableCollection<TreeNodeVM> RootNodes { get; } = new();

    private long _selectedNodeId;
    private bool _isDirty;
    private bool _suppressEditorEvents;
    private bool _suppressSelectionGuard;
    private bool _isModalOpen;
    private int _currentFindIndex = -1;
    private Point _dragStartPoint;
    private TreeNodeVM? _draggingNode;
    private string _renameBackup = "";

    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show($"发生未处理异常：{e.Exception.Message}", "LiteList 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        ThemeHelper.ApplySystemTheme(app.Resources);

        string? initialDb = null;
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("-"))
            initialDb = Path.GetFullPath(args[0].Trim());

        var win = new MainWindow(initialDb);
        app.Run(win);
    }

    public MainWindow(string? initialDbPath)
    {
        InitializeComponent();
        OutlineTree.ItemsSource = RootNodes;

        MdEditor.FontFamily = SystemFonts.MessageFontFamily;
        MdEditor.TextArea.Caret.CaretBrush = ThemeHelper.GetBrush("CaretBrush");
        MdEditor.TextArea.SelectionBrush = ThemeHelper.GetBrush("SelectionBg");
        MdEditor.TextArea.TextView.CurrentLineBackground = ThemeHelper.GetBrush("EditorActiveLineBg");
        MdEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        MdEditor.TextArea.TextView.BackgroundRenderers.Add(_searchRenderer);
        MdEditor.TextArea.SelectionChanged += (_, _) => UpdateCursorStatus();
        MdEditor.TextChanged += MdEditor_TextChanged;
        DataObject.AddPastingHandler(MdEditor, MdEditor_OnPasting);

        _foldingManager = FoldingManager.Install(MdEditor.TextArea);

        _flowRenderer = new MarkdownFlowRenderer(_files);
        _flowRenderer.TaskCheckboxToggled += FlowRenderer_TaskCheckboxToggled;
        _flowRenderer.ImageClicked += ShowImageLightbox;
        _flowRenderer.AttachmentClicked += OpenAttachmentFile;
        _flowRenderer.AttachmentSaveAsRequested += SaveAttachmentAsFile;
        _flowRenderer.ExternalUrlClicked += OpenExternalUrl;
        _flowRenderer.CodeCopied += msg => TxtSaveStatus.Text = msg;

        _idleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idleSaveTimer.Tick += (_, _) =>
        {
            _idleSaveTimer.Stop();
            if (_settings.SaveMode == SaveMode.IdleAutoSave && _isDirty) SaveCurrentNode();
        };

        _foldAndTocTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _foldAndTocTimer.Tick += (_, _) => { _foldAndTocTimer.Stop(); RefreshFoldingAndToc(); };

        _splitPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _splitPreviewTimer.Tick += (_, _) => { _splitPreviewTimer.Stop(); RenderPreview(); };

        _globalSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _globalSearchTimer.Tick += (_, _) => { _globalSearchTimer.Stop(); ExecuteGlobalSearch(); };

        OpenDatabase(initialDbPath ?? _db.ResolveDefaultDbPath(_files.AppBaseDir));
    }

    // ===== 数据库与设置 =====

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
        ReloadTree(null);
    }

    private void ApplySettingsToUI()
    {
        MdEditor.FontSize = _settings.FontSize;
        ApplyLeftVisibility(_settings.ShowLeftTree);
        ApplyRightVisibility(_settings.ShowRightToc);
        if (PreviewViewer.Document != null) PreviewViewer.Document.FontSize = _settings.FontSize;
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
    }

    // ===== 大纲树与防丢拦截 =====

    private void ReloadTree(long? selectId)
    {
        var records = _db.GetTree();
        RootNodes.Clear();
        _nodeLookup.Clear();

        foreach (var r in records)
        {
            _nodeLookup[r.Id] = new TreeNodeVM
            {
                Id = r.Id,
                ParentId = r.ParentId,
                Title = r.Title,
                SortOrder = r.SortOrder,
                IsExpanded = r.IsExpanded
            };
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
        SetDirtyState(false);
        EmptyOverlay.Visibility = Visibility.Visible;
        EditorHeaderBar.Visibility = Visibility.Collapsed;
        FormatToolBar.Visibility = Visibility.Collapsed;
        MdEditor.Visibility = Visibility.Collapsed;
        PreviewViewer.Visibility = Visibility.Collapsed;
        LstRightToc.ItemsSource = null;
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
            var res = MessageBox.Show(this, $"节点「{title}」尚未保存，是否先保存？", "未保存提醒",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
        LoadNodeIntoEditor(nextNode.Id);
    }

    private void LoadNodeIntoEditor(long nodeId, int? jumpLine = null, string? searchKw = null)
    {
        _selectedNodeId = nodeId;
        var detail = _db.GetNodeContent(nodeId);

        EmptyOverlay.Visibility = Visibility.Collapsed;
        EditorHeaderBar.Visibility = Visibility.Visible;

        _suppressEditorEvents = true;
        try
        {
            TxtNodeTitle.Text = detail.Title;
            MdEditor.Document.Text = detail.Content;
            MdEditor.Document.UndoStack.ClearAll();
            _colorizer.InvalidateCache();
            SetDirtyState(false);
        }
        finally { _suppressEditorEvents = false; }

        RefreshFoldingAndToc();
        UpdateCursorStatus();

        var mode = string.IsNullOrWhiteSpace(detail.Content) && _viewMode == EditorViewMode.Preview
            ? EditorViewMode.Edit : _viewMode;
        SetEditorViewMode(mode, jumpLine.HasValue, searchKw);
        if (jumpLine is > 0) JumpEditorToLine(jumpLine.Value, searchKw);
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
        _db.SaveNode(_selectedNodeId, title, MdEditor.Document.Text);
        if (_nodeLookup.TryGetValue(_selectedNodeId, out var vm)) vm.Title = title;
        SetDirtyState(false);
    }

    // ===== 视图切换与联动 =====

    private void SetEditorViewMode(EditorViewMode mode, bool focusEditor = false, string? highlightKw = null)
    {
        _viewMode = mode;
        BtnViewEdit.FontWeight = mode == EditorViewMode.Edit ? FontWeights.Bold : FontWeights.Normal;
        BtnViewSplit.FontWeight = mode == EditorViewMode.Split ? FontWeights.Bold : FontWeights.Normal;
        BtnViewPreview.FontWeight = mode == EditorViewMode.Preview ? FontWeights.Bold : FontWeights.Normal;

        FormatToolBar.Visibility = mode == EditorViewMode.Preview ? Visibility.Collapsed : Visibility.Visible;
        MdEditor.Visibility = mode == EditorViewMode.Preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewViewer.Visibility = mode == EditorViewMode.Edit ? Visibility.Collapsed : Visibility.Visible;

        if (mode == EditorViewMode.Edit)
        {
            ColEditor.Width = new GridLength(1, GridUnitType.Star);
            ColSplitLine.Width = new GridLength(0);
            ColPreview.Width = new GridLength(0);
        }
        else if (mode == EditorViewMode.Preview)
        {
            ColEditor.Width = new GridLength(0);
            ColSplitLine.Width = new GridLength(0);
            ColPreview.Width = new GridLength(1, GridUnitType.Star);
            RenderPreview(highlightKw);
        }
        else
        {
            ColEditor.Width = new GridLength(1, GridUnitType.Star);
            ColSplitLine.Width = new GridLength(1);
            ColPreview.Width = new GridLength(1, GridUnitType.Star);
            RenderPreview(highlightKw);
        }
        if (focusEditor && mode != EditorViewMode.Preview) MdEditor.Focus();
    }

    private void RenderPreview(string? highlightKw = null)
    {
        if (_selectedNodeId <= 0) return;
        PreviewViewer.Document = _flowRenderer.Render(
            MdEditor.Document.Text, _settings.FontSize, out _previewHeadingBlocks, highlightKw);
    }

    private void TxtNodeTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEditorEvents || _selectedNodeId <= 0) return;
        if (_nodeLookup.TryGetValue(_selectedNodeId, out var vm))
            vm.Title = string.IsNullOrWhiteSpace(TxtNodeTitle.Text) ? "未命名节点" : TxtNodeTitle.Text;
        MarkDirty();
    }

    private void MdEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _selectedNodeId <= 0) return;
        MarkDirty();
        _foldAndTocTimer.Stop();
        _foldAndTocTimer.Start();
        if (_viewMode == EditorViewMode.Split) { _splitPreviewTimer.Stop(); _splitPreviewTimer.Start(); }
        if (FindBar.Visibility == Visibility.Visible) UpdateFindMatches(false);
    }

    private void MarkDirty()
    {
        SetDirtyState(true);
        if (_settings.SaveMode == SaveMode.IdleAutoSave) { _idleSaveTimer.Stop(); _idleSaveTimer.Start(); }
    }

    private void RefreshFoldingAndToc()
    {
        MarkdownFoldingStrategy.UpdateFoldings(_foldingManager, MdEditor.Document);
        LstRightToc.ItemsSource = MarkdownFoldingStrategy.ExtractHeadings(MdEditor.Document);
    }

    private void UpdateCursorStatus()
    {
        var loc = MdEditor.TextArea.Caret.Location;
        TxtCursorInfo.Text = $"行 {loc.Line}, 列 {loc.Column}";
        TxtDocStats.Text = $"{MdEditor.Document.TextLength} 字 · {MdEditor.Document.LineCount} 行";
    }

    private void LstRightToc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstRightToc.SelectedItem is not HeadingItem h) return;
        if (_viewMode != EditorViewMode.Preview) JumpEditorToLine(h.LineNumber);
        if (_viewMode != EditorViewMode.Edit && _previewHeadingBlocks.TryGetValue(h.LineNumber, out var blk))
            blk.BringIntoView();
    }

    private void JumpEditorToLine(int lineNumber, string? selectKeyword = null)
    {
        if (lineNumber < 1 || lineNumber > MdEditor.Document.LineCount) return;
        var docLine = MdEditor.Document.GetLineByNumber(lineNumber);
        foreach (var fs in _foldingManager.AllFoldings)
            if (fs.IsFolded && fs.StartOffset <= docLine.Offset && fs.EndOffset >= docLine.Offset) fs.IsFolded = false;

        if (!string.IsNullOrEmpty(selectKeyword))
        {
            int idx = MdEditor.Document.GetText(docLine).IndexOf(selectKeyword, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                MdEditor.Select(docLine.Offset + idx, selectKeyword.Length);
                MdEditor.ScrollTo(lineNumber, idx + 1);
                MdEditor.Focus();
                return;
            }
        }
        MdEditor.TextArea.Caret.Offset = docLine.Offset;
        MdEditor.ScrollToLine(lineNumber);
        MdEditor.Focus();
    }

    // ===== 粘贴截图 / 拖入文件 / 附件操作 =====

    private void MdEditor_OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
        {
            e.CancelCommand();
            var res = _files.SaveClipboardImage(img);
            InsertMarkdownSnippet($"![{res.Filename}]({res.RelPath})\n");
            return;
        }
        if (Clipboard.ContainsFileDropList())
        {
            e.CancelCommand();
            ImportFilesAndInsert(Clipboard.GetFileDropList().Cast<string>());
            return;
        }
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) &&
            e.DataObject.GetData(DataFormats.UnicodeText) is string txt)
        {
            var (rewritten, count) = _files.ResolveLocalReferencesInFragment(txt);
            if (count > 0)
            {
                e.CancelCommand();
                InsertMarkdownSnippet(rewritten);
                TxtSaveStatus.Text = $"已自动归档 {count} 个引用到 files/";
            }
        }
    }

    private void MdEditor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    private void MdEditor_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            ImportFilesAndInsert(paths);
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
            InsertMarkdownSnippet(string.Join("\n", lines) + "\n");
            TxtSaveStatus.Text = $"已复制 {lines.Count} 个文件到 files/";
        }
    }

    private void InsertMarkdownSnippet(string text)
    {
        var sel = MdEditor.TextArea.Selection;
        if (!sel.IsEmpty)
        {
            var seg = sel.SurroundingSegment;
            MdEditor.Document.Replace(seg.Offset, seg.Length, text);
            MdEditor.CaretOffset = seg.Offset + text.Length;
        }
        else
        {
            int pos = MdEditor.CaretOffset;
            MdEditor.Document.Insert(pos, text);
            MdEditor.CaretOffset = pos + text.Length;
        }
        MdEditor.Focus();
    }

    private void FlowRenderer_TaskCheckboxToggled(int lineNum, bool isChecked)
    {
        if (lineNum < 1 || lineNum > MdEditor.Document.LineCount) return;
        var line = MdEditor.Document.GetLineByNumber(lineNum);
        var txt = MdEditor.Document.GetText(line);
        var updated = isChecked ? Regex.Replace(txt, @"\[ \]", "[x]") : Regex.Replace(txt, @"\[[xX]\]", "[ ]");
        MdEditor.Document.Replace(line.Offset, line.Length, updated);
        if (_viewMode == EditorViewMode.Preview) RenderPreview();
    }

    private void ShowImageLightbox(string fullPath)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(fullPath);
        bmp.EndInit();
        ImgLightbox.Source = bmp;
        LightboxOverlay.Visibility = Visibility.Visible;
    }

    private void LightboxOverlay_MouseDown(object sender, MouseButtonEventArgs e) =>
        LightboxOverlay.Visibility = Visibility.Collapsed;

    private void OpenAttachmentFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null) { MessageBox.Show($"在 files 目录中找不到文件：{relPath}"); return; }
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }

    private void SaveAttachmentAsFile(string relPath)
    {
        var full = _files.ResolveExistingFile(relPath);
        if (full == null) { MessageBox.Show($"找不到文件：{relPath}"); return; }
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

    // ===== 悬浮查找替换 =====

    private void OpenFindBar(bool focusReplace = false)
    {
        if (_viewMode == EditorViewMode.Preview) SetEditorViewMode(EditorViewMode.Edit);
        FindBar.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(MdEditor.SelectedText) && !MdEditor.SelectedText.Contains('\n'))
            TxtFind.Text = MdEditor.SelectedText;
        UpdateFindMatches(false);
        if (focusReplace) TxtReplace.Focus();
        else { TxtFind.Focus(); TxtFind.SelectAll(); }
    }

    private void UpdateFindMatches(bool jumpToCurrent)
    {
        _searchRenderer.Matches.Clear();
        _currentFindIndex = -1;
        var pattern = TxtFind.Text;
        if (string.IsNullOrEmpty(pattern))
        {
            TxtFindCount.Text = "0/0";
            MdEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            return;
        }
        try
        {
            var opts = ChkCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
            var rx = new Regex(ChkRegex.IsChecked == true ? pattern : Regex.Escape(pattern), opts);
            foreach (Match m in rx.Matches(MdEditor.Document.Text))
                if (m.Length > 0) _searchRenderer.Matches.Add((m.Index, m.Length));
        }
        catch { TxtFindCount.Text = "正则错"; return; }

        if (_searchRenderer.Matches.Count > 0)
        {
            _currentFindIndex = 0;
            if (jumpToCurrent) SelectFindMatch(0);
        }
        TxtFindCount.Text = _searchRenderer.Matches.Count == 0 ? "0/0" : $"{_currentFindIndex + 1}/{_searchRenderer.Matches.Count}";
        MdEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void SelectFindMatch(int index)
    {
        if (_searchRenderer.Matches.Count == 0) return;
        _currentFindIndex = (index + _searchRenderer.Matches.Count) % _searchRenderer.Matches.Count;
        var (start, len) = _searchRenderer.Matches[_currentFindIndex];
        var loc = MdEditor.Document.GetLocation(start);
        foreach (var fs in _foldingManager.AllFoldings)
            if (fs.IsFolded && fs.StartOffset <= start && fs.EndOffset >= start) fs.IsFolded = false;
        MdEditor.Select(start, len);
        MdEditor.ScrollTo(loc.Line, loc.Column);
        TxtFindCount.Text = $"{_currentFindIndex + 1}/{_searchRenderer.Matches.Count}";
    }

    private void TxtFind_TextChanged(object sender, TextChangedEventArgs e) => UpdateFindMatches(true);
    private void FindOption_Changed(object sender, RoutedEventArgs e) => UpdateFindMatches(true);
    private void BtnFindNext_Click(object sender, RoutedEventArgs e) => SelectFindMatch(_currentFindIndex + 1);
    private void BtnFindPrev_Click(object sender, RoutedEventArgs e) => SelectFindMatch(_currentFindIndex - 1);
    private void BtnCloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        _searchRenderer.Matches.Clear();
        MdEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        MdEditor.Focus();
    }
    private void TxtFind_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SelectFindMatch(_currentFindIndex + (Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1)); e.Handled = true; }
        else if (e.Key == Key.Escape) { BtnCloseFind_Click(sender, e); e.Handled = true; }
    }
    private void BtnReplaceOne_Click(object sender, RoutedEventArgs e)
    {
        if (_searchRenderer.Matches.Count == 0 || _currentFindIndex < 0) return;
        var (start, len) = _searchRenderer.Matches[_currentFindIndex];
        MdEditor.Document.Replace(start, len, TxtReplace.Text);
        UpdateFindMatches(true);
    }
    private void BtnReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (_searchRenderer.Matches.Count == 0) return;
        MdEditor.Document.BeginUpdate();
        for (int i = _searchRenderer.Matches.Count - 1; i >= 0; i--)
            MdEditor.Document.Replace(_searchRenderer.Matches[i].Start, _searchRenderer.Matches[i].Length, TxtReplace.Text);
        MdEditor.Document.EndUpdate();
        UpdateFindMatches(false);
    }

    // ===== 全局搜索浮层 (Ctrl+K) =====

    private void BtnQuickSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchOverlay.Visibility = Visibility.Visible;
        TxtGlobalSearch.Text = "";
        LstGlobalHits.ItemsSource = null;
        TxtGlobalSearch.Focus();
    }
    private void BtnCloseSearchOverlay_Click(object sender, RoutedEventArgs e) => SearchOverlay.Visibility = Visibility.Collapsed;
    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SearchOverlay) SearchOverlay.Visibility = Visibility.Collapsed;
    }
    private void TxtGlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _globalSearchTimer.Stop();
        _globalSearchTimer.Start();
    }
    private void ExecuteGlobalSearch()
    {
        var kw = TxtGlobalSearch.Text.Trim();
        if (string.IsNullOrEmpty(kw)) { LstGlobalHits.ItemsSource = null; return; }
        var hits = _db.SearchOccurrences(kw);
        LstGlobalHits.ItemsSource = hits;
        if (hits.Count > 0) LstGlobalHits.SelectedIndex = 0;
    }
    private void ConfirmGlobalHit()
    {
        if (LstGlobalHits.SelectedItem is not SearchHit hit) return;
        var kw = TxtGlobalSearch.Text.Trim();
        SearchOverlay.Visibility = Visibility.Collapsed;
        if (hit.NodeId != _selectedNodeId && !EnsureSafeToLeaveCurrent()) return;
        SelectTreeNode(hit.NodeId);
        LoadNodeIntoEditor(hit.NodeId, hit.LineNumber > 0 ? hit.LineNumber : null, kw);
    }
    private void TxtGlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
        else if (e.Key == Key.Down && LstGlobalHits.Items.Count > 0)
        {
            LstGlobalHits.SelectedIndex = Math.Min(LstGlobalHits.Items.Count - 1, LstGlobalHits.SelectedIndex + 1);
            LstGlobalHits.ScrollIntoView(LstGlobalHits.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && LstGlobalHits.Items.Count > 0)
        {
            LstGlobalHits.SelectedIndex = Math.Max(0, LstGlobalHits.SelectedIndex - 1);
            LstGlobalHits.ScrollIntoView(LstGlobalHits.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter) { ConfirmGlobalHit(); e.Handled = true; }
    }
    private void LstGlobalHits_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ConfirmGlobalHit();
    private void LstGlobalHits_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ConfirmGlobalHit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
    }

    // ===== 设置浮层 =====

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        RbManual.IsChecked = _settings.SaveMode == SaveMode.Manual;
        RbIdleAuto.IsChecked = _settings.SaveMode == SaveMode.IdleAutoSave;
        CmbBlurAction.SelectedIndex = (int)_settings.BlurAction;
        SldFontSize.Value = _settings.FontSize;
        TxtSetFontLabel.Text = $"{(int)_settings.FontSize} px";
        ChkAutoVacuum.IsChecked = _settings.AutoVacuumOnExit;
        SettingsOverlay.Visibility = Visibility.Visible;
    }
    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsOverlay) SettingsOverlay.Visibility = Visibility.Collapsed;
    }
    private void SldFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtSetFontLabel != null) TxtSetFontLabel.Text = $"{(int)e.NewValue} px";
    }
    private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;
    private void BtnSettingsOk_Click(object sender, RoutedEventArgs e)
    {
        _settings.SaveMode = RbIdleAuto.IsChecked == true ? SaveMode.IdleAutoSave : SaveMode.Manual;
        _settings.BlurAction = (BlurAction)Math.Max(0, CmbBlurAction.SelectedIndex);
        _settings.FontSize = SldFontSize.Value;
        _settings.AutoVacuumOnExit = ChkAutoVacuum.IsChecked == true;
        _db.SaveSettings(_settings);
        ApplySettingsToUI();
        SettingsOverlay.Visibility = Visibility.Collapsed;
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
        else if (e.Key == Key.F2) { e.Handled = true; BeginRename(node); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteNodeWithConfirm(node); }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) OutdentNode(node);
            else IndentNode(node);
        }
    }
    private void BeginRename(TreeNodeVM node) { _renameBackup = node.Title; node.IsRenaming = true; }
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
            Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); }, DispatcherPriority.Input);
    }
    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TreeNodeVM vm) return;
        if (e.Key == Key.Enter) { vm.IsRenaming = false; CommitRename(vm); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.Title = _renameBackup; vm.IsRenaming = false; e.Handled = true; }
    }
    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is TreeNodeVM vm && vm.IsRenaming)
        {
            vm.IsRenaming = false;
            CommitRename(vm);
        }
    }
    private void CommitRename(TreeNodeVM vm)
    {
        _db.RenameNode(vm.Id, vm.Title);
        if (_selectedNodeId == vm.Id) TxtNodeTitle.Text = vm.Title;
    }

    private void CreateChildAndRename(long parentId)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var created = _db.CreateNode(parentId, "新节点");
        ReloadTree(created.Id);
        if (_nodeLookup.TryGetValue(created.Id, out var vm)) BeginRename(vm);
    }
    private void CreateSiblingAfterAndRename(TreeNodeVM target)
    {
        if (!EnsureSafeToLeaveCurrent()) return;
        var sibs = target.ParentId > 0 && _nodeLookup.TryGetValue(target.ParentId, out var p) ? p.Children : RootNodes;
        int idx = sibs.IndexOf(target);
        double order = idx >= 0 && idx + 1 < sibs.Count ? (target.SortOrder + sibs[idx + 1].SortOrder) / 2.0 : target.SortOrder + 1000.0;
        var created = _db.CreateNode(target.ParentId, "新节点", order);
        ReloadTree(created.Id);
        if (_nodeLookup.TryGetValue(created.Id, out var vm)) BeginRename(vm);
    }
    private void DeleteNodeWithConfirm(TreeNodeVM node)
    {
        _isModalOpen = true;
        var msg = node.Children.Count > 0 ? $"确定删除「{node.Title}」及其全部子节点吗？" : $"确定删除节点「{node.Title}」吗？";
        var ok = MessageBox.Show(this, msg, "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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
        _db.MoveNode(node.Id, np.Id, np.Children.Count > 0 ? np.Children[^1].SortOrder + 1000.0 : 1000.0);
        ReloadTree(node.Id);
    }
    private void OutdentNode(TreeNodeVM node)
    {
        if (node.ParentId <= 0 || !_nodeLookup.TryGetValue(node.ParentId, out var parent)) return;
        var gs = parent.ParentId > 0 && _nodeLookup.TryGetValue(parent.ParentId, out var gp) ? gp.Children : RootNodes;
        int idx = gs.IndexOf(parent);
        double order = idx + 1 < gs.Count ? (parent.SortOrder + gs[idx + 1].SortOrder) / 2.0 : parent.SortOrder + 1000.0;
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
            newOrder = target.Children.Count > 0 ? target.Children[^1].SortOrder + 1000.0 : 1000.0;
        }
        try { _db.MoveNode(src.Id, newPid, newOrder); ReloadTree(src.Id); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null) { if (current is T t) return t; current = VisualTreeHelper.GetParent(current); }
        return null;
    }

    // ===== 顶部与右键事件 =====

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
        try { OpenDatabase(dlg.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message, "打开失败"); }
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
            var copyAtt = MessageBox.Show(this, "是否一并复制所引用的 files/ 图片与附件到导出目录？",
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
    private void BtnFontMinus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void BtnFontPlus_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);
    private void ChangeFontSize(double delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 12, 28);
        ApplySettingsToUI();
        _db.SaveSettings(_settings);
    }
    private void MdEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) { ChangeFontSize(e.Delta > 0 ? 1 : -1); e.Handled = true; }
    }
    private void PreviewViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) { ChangeFontSize(e.Delta > 0 ? 1 : -1); e.Handled = true; }
    }
    private void BtnViewEdit_Click(object sender, RoutedEventArgs e) => SetEditorViewMode(EditorViewMode.Edit, true);
    private void BtnViewSplit_Click(object sender, RoutedEventArgs e) => SetEditorViewMode(EditorViewMode.Split, true);
    private void BtnViewPreview_Click(object sender, RoutedEventArgs e) => SetEditorViewMode(EditorViewMode.Preview);
    private void PreviewViewer_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SetEditorViewMode(EditorViewMode.Edit, true);
    private void BtnAddRoot_Click(object sender, RoutedEventArgs e) => CreateChildAndRename(0);
    private void BtnExpandAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(true); ReloadTree(_selectedNodeId); }
    private void BtnCollapseAllTree_Click(object sender, RoutedEventArgs e) { _db.SetAllExpanded(false); ReloadTree(_selectedNodeId); }
    private void CtxAddChild_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) CreateChildAndRename(n.Id); }
    private void CtxAddSibling_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) CreateSiblingAfterAndRename(n); }
    private void CtxRename_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) BeginRename(n); }
    private void CtxDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (OutlineTree.SelectedItem is TreeNodeVM n && EnsureSafeToLeaveCurrent()) ReloadTree(_db.DuplicateNode(n.Id, true).Id);
    }
    private void CtxMoveUp_Click(object sender, RoutedEventArgs e) => StepSibling(true);
    private void CtxMoveDown_Click(object sender, RoutedEventArgs e) => StepSibling(false);
    private void StepSibling(bool up)
    {
        if (OutlineTree.SelectedItem is not TreeNodeVM n) return;
        var sibs = n.ParentId > 0 && _nodeLookup.TryGetValue(n.ParentId, out var p) ? p.Children : RootNodes;
        int idx = sibs.IndexOf(n);
        int targetIdx = idx + (up ? -1 : 1);
        if (targetIdx < 0 || targetIdx >= sibs.Count) return;
        double order = up
            ? (targetIdx > 0 ? (sibs[targetIdx - 1].SortOrder + sibs[targetIdx].SortOrder) / 2.0 : sibs[targetIdx].SortOrder - 1000.0)
            : (targetIdx + 1 < sibs.Count ? (sibs[targetIdx].SortOrder + sibs[targetIdx + 1].SortOrder) / 2.0 : sibs[targetIdx].SortOrder + 1000.0);
        _db.MoveNode(n.Id, n.ParentId, order);
        ReloadTree(n.Id);
    }
    private void CtxSortChildren_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) { _db.SortChildren(n.Id); ReloadTree(n.Id); } }
    private void CtxDelete_Click(object sender, RoutedEventArgs e) { if (OutlineTree.SelectedItem is TreeNodeVM n) DeleteNodeWithConfirm(n); }

    private void FmtUndo_Click(object sender, RoutedEventArgs e) => MdEditor.Undo();
    private void FmtRedo_Click(object sender, RoutedEventArgs e) => MdEditor.Redo();
    private void EdCut_Click(object sender, RoutedEventArgs e) => MdEditor.Cut();
    private void EdCopy_Click(object sender, RoutedEventArgs e) => MdEditor.Copy();
    private void EdPaste_Click(object sender, RoutedEventArgs e) => MdEditor.Paste();
    private void EdSelectAll_Click(object sender, RoutedEventArgs e) => MdEditor.SelectAll();

    private void WrapSel(string before, string after, string placeholder)
    {
        var sel = MdEditor.SelectedText;
        var inner = string.IsNullOrEmpty(sel) ? placeholder : sel;
        var offset = MdEditor.SelectionLength > 0 ? MdEditor.SelectionStart : MdEditor.CaretOffset;
        if (MdEditor.SelectionLength > 0) MdEditor.Document.Replace(offset, MdEditor.SelectionLength, before + inner + after);
        else MdEditor.Document.Insert(offset, before + inner + after);
        MdEditor.Select(offset + before.Length, inner.Length);
        MdEditor.Focus();
    }
    private void ToggleLinePrefix(string prefix)
    {
        var line = MdEditor.Document.GetLineByOffset(MdEditor.CaretOffset);
        if (MdEditor.Document.GetText(line).StartsWith(prefix)) MdEditor.Document.Remove(line.Offset, prefix.Length);
        else MdEditor.Document.Insert(line.Offset, prefix);
        MdEditor.Focus();
    }
    private void FmtHeading_Click(object sender, RoutedEventArgs e)
    {
        var line = MdEditor.Document.GetLineByOffset(MdEditor.CaretOffset);
        var m = Regex.Match(MdEditor.Document.GetText(line), @"^(#{1,6})\s+");
        if (!m.Success) MdEditor.Document.Insert(line.Offset, "# ");
        else MdEditor.Document.Replace(line.Offset, m.Length, m.Groups[1].Length >= 4 ? "" : new string('#', m.Groups[1].Length + 1) + " ");
        MdEditor.Focus();
    }
    private void FmtBold_Click(object sender, RoutedEventArgs e) => WrapSel("**", "**", "加粗文本");
    private void FmtItalic_Click(object sender, RoutedEventArgs e) => WrapSel("*", "*", "斜体文本");
    private void FmtStrike_Click(object sender, RoutedEventArgs e) => WrapSel("~~", "~~", "删除文本");
    private void FmtInlineCode_Click(object sender, RoutedEventArgs e) => WrapSel("`", "`", "code");
    private void FmtCodeBlock_Click(object sender, RoutedEventArgs e) => InsertMarkdownSnippet("\n```text\n\n```\n");
    private void FmtQuote_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("> ");
    private void FmtTask_Click(object sender, RoutedEventArgs e) => ToggleLinePrefix("- [ ] ");
    private void FmtLink_Click(object sender, RoutedEventArgs e) => WrapSel("[", "](https://)", "链接文字");
    private void FmtTable_Click(object sender, RoutedEventArgs e) =>
        InsertMarkdownSnippet("\n| 列1 | 列2 | 列3 |\n| --- | --- | --- |\n| 内容 | 内容 | 内容 |\n");
    private void FmtInsertFile_Click(object sender, RoutedEventArgs e)
    {
        _isModalOpen = true;
        var dlg = new OpenFileDialog { Multiselect = true, Title = "选择要插入的图片或附件（将复制进 files 目录）" };
        bool ok = dlg.ShowDialog(this) == true;
        _isModalOpen = false;
        if (ok) ImportFilesAndInsert(dlg.FileNames);
    }
    private void FmtFoldAll_Click(object sender, RoutedEventArgs e) { foreach (var f in _foldingManager.AllFoldings) f.IsFolded = true; }
    private void FmtUnfoldAll_Click(object sender, RoutedEventArgs e) { foreach (var f in _foldingManager.AllFoldings) f.IsFolded = false; }
    private void FmtFind_Click(object sender, RoutedEventArgs e) => OpenFindBar();
    private void PreviewCopySel_Click(object sender, RoutedEventArgs e) { if (!PreviewViewer.Selection.IsEmpty) Clipboard.SetText(PreviewViewer.Selection.Text); }
    private void PreviewCopyMd_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(MdEditor.Document.Text);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (LightboxOverlay.Visibility == Visibility.Visible) { LightboxOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (SearchOverlay.Visibility == Visibility.Visible) { SearchOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (SettingsOverlay.Visibility == Visibility.Visible) { SettingsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.S: SaveCurrentNode(); e.Handled = true; break;
                case Key.K: BtnQuickSearch_Click(sender, e); e.Handled = true; break;
                case Key.E: SetEditorViewMode(_viewMode == EditorViewMode.Preview ? EditorViewMode.Edit : EditorViewMode.Preview, true); e.Handled = true; break;
                case Key.F: OpenFindBar(false); e.Handled = true; break;
                case Key.H: OpenFindBar(true); e.Handled = true; break;
                case Key.B when MdEditor.IsKeyboardFocusWithin: FmtBold_Click(sender, e); e.Handled = true; break;
                case Key.I when MdEditor.IsKeyboardFocusWithin: FmtItalic_Click(sender, e); e.Handled = true; break;
                case Key.L when MdEditor.IsKeyboardFocusWithin: FmtLink_Click(sender, e); e.Handled = true; break;
                case Key.Oem5: BtnToggleLeft_Click(sender, e); e.Handled = true; break;
                case Key.OemQuestion: BtnToggleRight_Click(sender, e); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.F3 && FindBar.Visibility == Visibility.Visible)
        {
            SelectFindMatch(_currentFindIndex + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1));
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isModalOpen) return;
        EnsureSafeToLeaveCurrent(isBlur: true);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!EnsureSafeToLeaveCurrent()) { e.Cancel = true; return; }
        if (_settings.AutoVacuumOnExit) _db.SmartVacuumIfNeeded();
        _db.Dispose();
    }
}

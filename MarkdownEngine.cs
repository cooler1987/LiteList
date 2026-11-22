using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfList = System.Windows.Documents.List;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace LiteList;

/// <summary>
/// AvalonEdit 视口级实时着色器（仅对当前可见屏幕行着色，十万字零卡顿）
/// </summary>
public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private static readonly Regex InlineCodeRegex = new(@"`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*[^*]+\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<!\*)\*[^*\s][^*]*\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex StrikeRegex = new(@"~~[^~]+~~", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"!?\[[^\]]*\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex CodeKeywordRegex = new(
        @"\b(if|else|for|while|switch|case|return|break|continue|func|function|var|let|const|class|struct|interface|public|private|static|void|int|string|bool|true|false|null|nil|None|def|import|from|using|namespace|SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|JOIN|ORDER|BY|CREATE|TABLE)\b",
        RegexOptions.Compiled);
    private static readonly Regex CodeStringRegex = new("\"([^\"\\\\]|\\\\.)*\"|'([^'\\\\]|\\\\.)*'", RegexOptions.Compiled);
    private static readonly Regex CodeCommentRegex = new(@"(//.*$|#.*$|--.*$)", RegexOptions.Compiled);

    private readonly FontFamily _monoFamily = new("Consolas, Courier New");
    private ITextSourceVersion? _cachedVersion;
    private bool[] _inCodeBlock = [];

    public void InvalidateCache() => _cachedVersion = null;

    private void EnsureFenceCache(TextDocument doc)
    {
        if (doc.Version != null && ReferenceEquals(_cachedVersion, doc.Version)) return;

        int lineCount = doc.LineCount;
        if (_inCodeBlock.Length != lineCount + 1)
            _inCodeBlock = new bool[lineCount + 1];

        bool inside = false;
        for (int i = 1; i <= lineCount; i++)
        {
            var txt = doc.GetText(doc.GetLineByNumber(i)).TrimStart();
            if (txt.StartsWith("```"))
            {
                _inCodeBlock[i] = true;
                inside = !inside;
            }
            else
            {
                _inCodeBlock[i] = inside;
            }
        }
        _cachedVersion = doc.Version;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var doc = CurrentContext.Document;
        EnsureFenceCache(doc);

        var text = doc.GetText(line);
        if (string.IsNullOrEmpty(text)) return;
        int start = line.Offset;

        // 1. 代码块内部着色（切为等宽字体）
        if (line.LineNumber < _inCodeBlock.Length && _inCodeBlock[line.LineNumber])
        {
            ChangeLinePart(start, line.EndOffset, el =>
            {
                el.TextRunProperties.SetTypeface(new Typeface(
                    _monoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
                el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("InlineCodeColor"));
            });

            if (text.TrimStart().StartsWith("```"))
            {
                ChangeLinePart(start, line.EndOffset, el =>
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("MutedFg")));
                return;
            }

            foreach (Match m in CodeKeywordRegex.Matches(text))
                if (m.Length > 0)
                    ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                        el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("KeywordColor")));

            foreach (Match m in CodeStringRegex.Matches(text))
                if (m.Length > 0)
                    ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                        el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("StringColor")));

            foreach (Match m in CodeCommentRegex.Matches(text))
                if (m.Length > 0)
                    ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                        el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("CommentColor")));
            return;
        }

        // 2. 标题 #..######
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && (hashes == trimmed.Length || trimmed[hashes] == ' '))
            {
                double scale = hashes switch { 1 => 1.35, 2 => 1.22, 3 => 1.12, _ => 1.04 };
                ChangeLinePart(start, line.EndOffset, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("HeadingColor"));
                    el.TextRunProperties.SetTypeface(new Typeface(
                        el.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                    el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * scale);
                });
                return;
            }
        }

        // 3. 引用 >
        if (trimmed.StartsWith('>'))
        {
            ChangeLinePart(start, line.EndOffset, el =>
            {
                el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("MutedFg"));
                el.TextRunProperties.SetTypeface(new Typeface(
                    el.TextRunProperties.Typeface.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal));
            });
        }

        // 4. 加粗 / 斜体 / 删除线 / 链接 / 行内代码
        foreach (Match m in BoldRegex.Matches(text))
            if (m.Length > 0)
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("StrongColor"));
                    el.TextRunProperties.SetTypeface(new Typeface(
                        el.TextRunProperties.Typeface.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal));
                });

        foreach (Match m in ItalicRegex.Matches(text))
            if (m.Length > 0)
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("ItalicColor"));
                    el.TextRunProperties.SetTypeface(new Typeface(
                        el.TextRunProperties.Typeface.FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal));
                });

        foreach (Match m in StrikeRegex.Matches(text))
            if (m.Length > 0)
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                    el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));

        foreach (Match m in LinkRegex.Matches(text))
            if (m.Length > 0)
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("AccentBrush")));

        foreach (Match m in InlineCodeRegex.Matches(text))
            if (m.Length > 0)
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(ThemeHelper.GetBrush("InlineCodeColor"));
                    el.TextRunProperties.SetTypeface(new Typeface(
                        _monoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
                });
    }
}

/// <summary>
/// 支持正文标题多级折叠、代码块折叠、缩进列表主从折叠与正文大纲提取
/// </summary>
public static class MarkdownFoldingStrategy
{
    public static void UpdateFoldings(FoldingManager manager, TextDocument doc)
    {
        var folds = new List<NewFolding>();
        var headingStack = new Stack<(int Level, int StartOffset, string Title)>();
        var listStack = new Stack<(int Indent, int StartOffset, int LastChildEnd)>();

        int fenceStart = -1;
        string fenceName = "";
        int lineCount = doc.LineCount;

        for (int i = 1; i <= lineCount; i++)
        {
            var line = doc.GetLineByNumber(i);
            var text = doc.GetText(line);
            var trimmed = text.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                if (fenceStart < 0)
                {
                    fenceStart = line.EndOffset;
                    fenceName = trimmed.Length > 3 ? trimmed : "```";
                }
                else
                {
                    if (line.EndOffset > fenceStart)
                        folds.Add(new NewFolding(fenceStart, line.EndOffset) { Name = fenceName });
                    fenceStart = -1;
                }
                continue;
            }
            if (fenceStart >= 0) continue;

            if (trimmed.StartsWith('#'))
            {
                int lvl = 0;
                while (lvl < trimmed.Length && trimmed[lvl] == '#') lvl++;
                if (lvl is >= 1 and <= 6 && lvl < trimmed.Length && trimmed[lvl] == ' ')
                {
                    FlushListStack(listStack, folds);
                    var prevLineEnd = i > 1 ? doc.GetLineByNumber(i - 1).EndOffset : 0;
                    while (headingStack.Count > 0 && headingStack.Peek().Level >= lvl)
                    {
                        var top = headingStack.Pop();
                        if (prevLineEnd > top.StartOffset)
                            folds.Add(new NewFolding(top.StartOffset, prevLineEnd) { Name = top.Title });
                    }
                    headingStack.Push((lvl, line.EndOffset, trimmed));
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                int indent = text.Length - trimmed.Length;
                bool isListLine = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") ||
                                  trimmed.StartsWith("+ ") || Regex.IsMatch(trimmed, @"^\d+\.\s");
                if (isListLine)
                {
                    while (listStack.Count > 0 && listStack.Peek().Indent >= indent)
                    {
                        var popped = listStack.Pop();
                        if (popped.LastChildEnd > popped.StartOffset)
                            folds.Add(new NewFolding(popped.StartOffset, popped.LastChildEnd) { Name = "..." });
                    }
                    if (listStack.Count > 0)
                    {
                        var parent = listStack.Pop();
                        listStack.Push((parent.Indent, parent.StartOffset, line.EndOffset));
                    }
                    listStack.Push((indent, line.EndOffset, line.EndOffset));
                }
                else if (listStack.Count > 0 && indent > listStack.Peek().Indent)
                {
                    var parent = listStack.Pop();
                    listStack.Push((parent.Indent, parent.StartOffset, line.EndOffset));
                }
                else if (listStack.Count > 0)
                {
                    FlushListStack(listStack, folds);
                }
            }
        }

        var docEnd = doc.TextLength;
        FlushListStack(listStack, folds);
        while (headingStack.Count > 0)
        {
            var top = headingStack.Pop();
            if (docEnd > top.StartOffset)
                folds.Add(new NewFolding(top.StartOffset, docEnd) { Name = top.Title });
        }

        var unique = folds
            .Where(f => f.EndOffset > f.StartOffset)
            .GroupBy(f => f.StartOffset)
            .Select(g => g.OrderByDescending(x => x.EndOffset).First())
            .OrderBy(f => f.StartOffset)
            .ToList();

        manager.UpdateFoldings(unique, -1);
    }

    private static void FlushListStack(Stack<(int Indent, int StartOffset, int LastChildEnd)> stack, List<NewFolding> folds)
    {
        while (stack.Count > 0)
        {
            var popped = stack.Pop();
            if (popped.LastChildEnd > popped.StartOffset)
                folds.Add(new NewFolding(popped.StartOffset, popped.LastChildEnd) { Name = "..." });
        }
    }

    public static List<HeadingItem> ExtractHeadings(TextDocument doc)
    {
        var list = new List<HeadingItem>();
        bool insideFence = false;
        for (int i = 1; i <= doc.LineCount; i++)
        {
            var line = doc.GetLineByNumber(i);
            var trimmed = doc.GetText(line).TrimStart();
            if (trimmed.StartsWith("```")) { insideFence = !insideFence; continue; }
            if (insideFence || !trimmed.StartsWith('#')) continue;

            int lvl = 0;
            while (lvl < trimmed.Length && trimmed[lvl] == '#') lvl++;
            if (lvl is >= 1 and <= 6 && lvl < trimmed.Length && trimmed[lvl] == ' ')
            {
                list.Add(new HeadingItem
                {
                    Level = lvl,
                    Text = trimmed[(lvl + 1)..].Trim(),
                    LineNumber = i
                });
            }
        }
        return list;
    }
}

/// <summary>
/// 编辑器视口内查找高亮渲染器
/// </summary>
public sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    public List<(int Start, int Length)> Matches { get; } = new();
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Matches.Count == 0 || !textView.VisualLinesValid) return;
        var brush = ThemeHelper.GetBrush("SearchMatchBg");
        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;

        foreach (var (start, len) in Matches)
        {
            if (start + len < viewStart || start > viewEnd) continue;
            var geoBuilder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            geoBuilder.AddSegment(textView, new TextSegment { StartOffset = start, Length = len });
            var geo = geoBuilder.CreateGeometry();
            if (geo != null) drawingContext.DrawGeometry(brush, null, geo);
        }
    }
}

/// <summary>
/// 使用 Markdig AST 构建原生 WPF FlowDocument 预览（支持标题折叠、待办勾选、代码复制、看图与附件打开）
/// </summary>
public sealed class MarkdownFlowRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private readonly FileArchiveService _files;
    private readonly FontFamily _monoFamily = new("Consolas, Courier New");

    public event Action<int, bool>? TaskCheckboxToggled;
    public event Action<string>? ImageClicked;
    public event Action<string>? AttachmentClicked;
    public event Action<string>? AttachmentSaveAsRequested;
    public event Action<string>? ExternalUrlClicked;
    public event Action<string>? CodeCopied;

    public MarkdownFlowRenderer(FileArchiveService files)
    {
        _files = files;
    }

    public FlowDocument Render(string markdown, double baseFontSize,
        out Dictionary<int, WpfBlock> headingBlockMap, string? highlightKeyword = null)
    {
        headingBlockMap = new Dictionary<int, WpfBlock>();

        var doc = new FlowDocument
        {
            FontFamily = SystemFonts.MessageFontFamily,
            FontSize = baseFontSize,
            Foreground = ThemeHelper.GetBrush("TextFg"),
            Background = ThemeHelper.GetBrush("AppBg"),
            PagePadding = new Thickness(24, 18, 24, 48)
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("暂无正文内容，双击此处或按 Ctrl+E 开始编写 Markdown..."))
            {
                Foreground = ThemeHelper.GetBrush("MutedFg"),
                FontStyle = FontStyles.Italic
            });
            return doc;
        }

        var ast = Markdown.Parse(markdown, Pipeline);
        var renderedBlocks = new List<(WpfBlock Block, int HeadingLevel, Button? FoldBtn)>();
        var collapsedFlags = new List<bool>();

        foreach (var node in ast)
        {
            var wpfBlock = ConvertBlock(node, baseFontSize, highlightKeyword, out int hLvl, out Button? foldBtn);
            if (wpfBlock != null)
            {
                doc.Blocks.Add(wpfBlock);
                renderedBlocks.Add((wpfBlock, hLvl, foldBtn));
                collapsedFlags.Add(false);
                if (hLvl > 0) headingBlockMap[node.Line + 1] = wpfBlock;
            }
        }

        void SyncVisibleBlocks()
        {
            doc.Blocks.Clear();
            int? hiddenBelowLevel = null;
            for (int i = 0; i < renderedBlocks.Count; i++)
            {
                var (blk, lvl, _) = renderedBlocks[i];
                if (lvl > 0)
                {
                    if (hiddenBelowLevel.HasValue && lvl <= hiddenBelowLevel.Value)
                        hiddenBelowLevel = null;

                    if (!hiddenBelowLevel.HasValue)
                    {
                        doc.Blocks.Add(blk);
                        if (collapsedFlags[i]) hiddenBelowLevel = lvl;
                    }
                }
                else if (!hiddenBelowLevel.HasValue)
                {
                    doc.Blocks.Add(blk);
                }
            }
        }

        for (int i = 0; i < renderedBlocks.Count; i++)
        {
            var btn = renderedBlocks[i].FoldBtn;
            if (btn == null) continue;
            int idx = i;
            btn.Click += (_, _) =>
            {
                collapsedFlags[idx] = !collapsedFlags[idx];
                btn.Content = collapsedFlags[idx] ? "▸" : "▾";
                SyncVisibleBlocks();
            };
        }

        return doc;
    }

    private WpfBlock? ConvertBlock(MdBlock block, double baseFontSize, string? highlightKw,
        out int headingLevel, out Button? foldBtn)
    {
        headingLevel = 0;
        foldBtn = null;

        switch (block)
        {
            case HeadingBlock hb:
            {
                headingLevel = hb.Level;
                double scale = hb.Level switch { 1 => 1.55, 2 => 1.32, 3 => 1.15, _ => 1.05 };
                var p = new Paragraph
                {
                    FontSize = baseFontSize * scale,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 14, 0, 6),
                    Foreground = ThemeHelper.GetBrush("HeadingColor")
                };
                if (hb.Level == 1)
                {
                    p.BorderBrush = ThemeHelper.GetBrush("BorderBrush");
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.Padding = new Thickness(0, 0, 0, 4);
                }
                foldBtn = new Button
                {
                    Content = "▾",
                    FontSize = 11,
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = ThemeHelper.GetBrush("MutedFg")
                };
                p.Inlines.Add(new InlineUIContainer(foldBtn) { BaselineAlignment = BaselineAlignment.Center });
                AppendInlines(p.Inlines, hb.Inline, baseFontSize * scale, highlightKw);
                return p;
            }

            case ParagraphBlock pb:
            {
                var p = new Paragraph { Margin = new Thickness(0, 5, 0, 5), LineHeight = baseFontSize * 1.65 };
                AppendInlines(p.Inlines, pb.Inline, baseFontSize, highlightKw);
                return p;
            }

            case CodeBlock cb:
            {
                var lang = (cb as FencedCodeBlock)?.Info?.Trim() ?? "";
                var codeText = cb.Lines.ToString().TrimEnd('\r', '\n');
                return BuildCodeCard(lang, codeText, baseFontSize);
            }

            case QuoteBlock qb:
            {
                var sec = new Section
                {
                    BorderBrush = ThemeHelper.GetBrush("AccentBrush"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = ThemeHelper.GetBrush("SidebarBg"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 8, 0, 8),
                    Foreground = ThemeHelper.GetBrush("MutedFg")
                };
                foreach (var child in qb)
                {
                    var inner = ConvertBlock(child, baseFontSize, highlightKw, out _, out _);
                    if (inner != null) sec.Blocks.Add(inner);
                }
                return sec;
            }

            case ListBlock lb:
            {
                var list = new WpfList
                {
                    Margin = new Thickness(0, 4, 0, 6),
                    Padding = new Thickness(20, 0, 0, 0),
                    MarkerStyle = lb.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
                };
                foreach (var itemObj in lb)
                {
                    if (itemObj is not ListItemBlock lib) continue;
                    var li = new ListItem { Margin = new Thickness(0, 2, 0, 2) };
                    bool isTask = false;
                    foreach (var sub in lib)
                    {
                        if (sub is ParagraphBlock subPb && subPb.Inline?.FirstChild is TaskList tl)
                        {
                            isTask = true;
                            list.MarkerStyle = TextMarkerStyle.None;
                            var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2), LineHeight = baseFontSize * 1.55 };
                            var chk = new CheckBox
                            {
                                IsChecked = tl.Checked,
                                Margin = new Thickness(0, 0, 7, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            int lineNum = subPb.Line + 1;
                            chk.Click += (_, _) => TaskCheckboxToggled?.Invoke(lineNum, chk.IsChecked == true);
                            p.Inlines.Add(new InlineUIContainer(chk) { BaselineAlignment = BaselineAlignment.Center });

                            var span = new Span();
                            if (tl.Checked)
                            {
                                span.TextDecorations = TextDecorations.Strikethrough;
                                span.Foreground = ThemeHelper.GetBrush("MutedFg");
                            }
                            AppendInlines(span.Inlines, subPb.Inline, baseFontSize, highlightKw, skipFirstTask: true);
                            p.Inlines.Add(span);
                            li.Blocks.Add(p);
                        }
                        else
                        {
                            var b = ConvertBlock(sub, baseFontSize, highlightKw, out _, out _);
                            if (b != null) li.Blocks.Add(b);
                        }
                    }
                    if (isTask) list.Padding = new Thickness(6, 0, 0, 0);
                    list.ListItems.Add(li);
                }
                return list;
            }

            case MdTable mdTable:
            {
                var tbl = new WpfTable
                {
                    CellSpacing = 0,
                    Margin = new Thickness(0, 8, 0, 8),
                    BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
                    BorderThickness = new Thickness(1)
                };
                var rg = new TableRowGroup();
                foreach (var rowObj in mdTable)
                {
                    if (rowObj is not MdTableRow mdRow) continue;
                    var wpfRow = new WpfTableRow();
                    if (mdRow.IsHeader)
                    {
                        wpfRow.Background = ThemeHelper.GetBrush("SidebarBg");
                        wpfRow.FontWeight = FontWeights.Bold;
                    }
                    foreach (var cellObj in mdRow)
                    {
                        if (cellObj is not MdTableCell mdCell) continue;
                        var cell = new WpfTableCell
                        {
                            BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
                            BorderThickness = new Thickness(0.5),
                            Padding = new Thickness(8, 5, 8, 5)
                        };
                        foreach (var cb in mdCell)
                        {
                            var wb = ConvertBlock(cb, baseFontSize, highlightKw, out _, out _);
                            if (wb != null) cell.Blocks.Add(wb);
                        }
                        wpfRow.Cells.Add(cell);
                    }
                    rg.Rows.Add(wpfRow);
                }
                tbl.RowGroups.Add(rg);
                return tbl;
            }

            case ThematicBreakBlock:
                return new BlockUIContainer(new Border
                {
                    Height = 1,
                    Background = ThemeHelper.GetBrush("BorderBrush"),
                    Margin = new Thickness(0, 10, 0, 10)
                });
        }
        return null;
    }

    private BlockUIContainer BuildCodeCard(string lang, string codeText, double baseFontSize)
    {
        var outer = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeHelper.GetBrush("CodeBg"),
            Margin = new Thickness(0, 8, 0, 8)
        };
        var stack = new StackPanel();
        var header = new DockPanel { Background = ThemeHelper.GetBrush("SidebarBg") };
        var langTxt = new TextBlock
        {
            Text = string.IsNullOrEmpty(lang) ? "code" : lang,
            FontSize = 11,
            Foreground = ThemeHelper.GetBrush("MutedFg"),
            Margin = new Thickness(10, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var copyBtn = new Button
        {
            Content = "复制代码",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 2, 6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ThemeHelper.GetBrush("MutedFg")
        };
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(codeText);
            copyBtn.Content = "已复制 ✓";
            CodeCopied?.Invoke("代码已复制到剪贴板");
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        header.Children.Add(copyBtn);
        header.Children.Add(langTxt);

        var codeBox = new TextBox
        {
            Text = codeText,
            IsReadOnly = true,
            FontFamily = _monoFamily,
            FontSize = Math.Max(12, baseFontSize - 1),
            Background = Brushes.Transparent,
            Foreground = ThemeHelper.GetBrush("TextFg"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 10),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        stack.Children.Add(header);
        stack.Children.Add(new Border { Height = 1, Background = ThemeHelper.GetBrush("BorderBrush") });
        stack.Children.Add(codeBox);
        outer.Child = stack;
        return new BlockUIContainer(outer);
    }

    private void AppendInlines(InlineCollection target, ContainerInline? container,
        double baseFontSize, string? highlightKw, bool skipFirstTask = false)
    {
        if (container == null) return;
        bool skipped = false;

        foreach (var child in container)
        {
            if (skipFirstTask && !skipped && child is TaskList)
            {
                skipped = true;
                continue;
            }
            switch (child)
            {
                case LiteralInline lit:
                    AddHighlightedText(target, lit.Content.ToString(), highlightKw);
                    break;

                case CodeInline ci:
                    target.Add(new Run($" {ci.Content} ")
                    {
                        FontFamily = _monoFamily,
                        FontSize = Math.Max(11, baseFontSize - 1),
                        Background = ThemeHelper.GetBrush("CodeBg"),
                        Foreground = ThemeHelper.GetBrush("InlineCodeColor")
                    });
                    break;

                case EmphasisInline ei:
                {
                    var span = new Span();
                    if (ei.DelimiterChar == '~' && ei.DelimiterCount == 2)
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                        span.Foreground = ThemeHelper.GetBrush("MutedFg");
                    }
                    else if (ei.DelimiterCount == 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = ThemeHelper.GetBrush("StrongColor");
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                        span.Foreground = ThemeHelper.GetBrush("ItalicColor");
                    }
                    AppendInlines(span.Inlines, ei, baseFontSize, highlightKw);
                    target.Add(span);
                    break;
                }

                case LinkInline li:
                {
                    var url = li.Url ?? "";
                    if (li.IsImage)
                    {
                        var imgUi = BuildImageElement(url);
                        if (imgUi != null) target.Add(imgUi);
                        else target.Add(new Run($"[图片缺失: {url}]") { Foreground = ThemeHelper.GetBrush("DangerBrush") });
                    }
                    else if (url.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                             url.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
                    {
                        var label = ExtractPlainText(li);
                        var pill = BuildAttachmentPill(string.IsNullOrEmpty(label) ? Path.GetFileName(url) : label, url);
                        target.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.Center });
                    }
                    else
                    {
                        var hl = new Hyperlink { Foreground = ThemeHelper.GetBrush("AccentBrush") };
                        AppendInlines(hl.Inlines, li, baseFontSize, highlightKw);
                        hl.Click += (_, _) => ExternalUrlClicked?.Invoke(url);
                        target.Add(hl);
                    }
                    break;
                }

                case AutolinkInline al:
                {
                    var hl = new Hyperlink(new Run(al.Url)) { Foreground = ThemeHelper.GetBrush("AccentBrush") };
                    hl.Click += (_, _) => ExternalUrlClicked?.Invoke(al.Url);
                    target.Add(hl);
                    break;
                }

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
            }
        }
    }

    private void AddHighlightedText(InlineCollection target, string text, string? kw)
    {
        if (string.IsNullOrEmpty(kw))
        {
            target.Add(new Run(text));
            return;
        }
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(kw, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { target.Add(new Run(text[pos..])); break; }
            if (idx > pos) target.Add(new Run(text[pos..idx]));
            target.Add(new Run(text.Substring(idx, kw.Length))
            {
                Background = ThemeHelper.GetBrush("SearchMatchBg"),
                FontWeight = FontWeights.SemiBold
            });
            pos = idx + kw.Length;
        }
    }

    private static string ExtractPlainText(ContainerInline inline)
    {
        var text = "";
        foreach (var c in inline)
        {
            if (c is LiteralInline l) text += l.Content.ToString();
            else if (c is ContainerInline ci) text += ExtractPlainText(ci);
        }
        return text;
    }

    private InlineUIContainer? BuildImageElement(string url)
    {
        var resolved = _files.ResolveExistingFile(url);
        if (resolved == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(resolved, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            var img = new Image
            {
                Source = bmp,
                MaxHeight = 420,
                Stretch = Stretch.Uniform,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 6),
                ToolTip = "点击查看大图"
            };
            img.MouseLeftButtonUp += (_, _) => ImageClicked?.Invoke(resolved);
            return new InlineUIContainer(img);
        }
        catch { return null; }
    }

    private UIElement BuildAttachmentPill(string label, string relPath)
    {
        var btn = new Button
        {
            Content = $"📎 {label}",
            FontSize = 12.5,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(2, 0, 2, 0),
            Background = ThemeHelper.GetBrush("SidebarBg"),
            Foreground = ThemeHelper.GetBrush("TextFg"),
            BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            ToolTip = "左键用系统默认程序打开，右键另存为"
        };
        btn.Click += (_, _) => AttachmentClicked?.Invoke(relPath);

        var cm = new ContextMenu();
        var openItem = new MenuItem { Header = "用默认程序打开" };
        openItem.Click += (_, _) => AttachmentClicked?.Invoke(relPath);
        var saveItem = new MenuItem { Header = "另存为..." };
        saveItem.Click += (_, _) => AttachmentSaveAsRequested?.Invoke(relPath);
        cm.Items.Add(openItem);
        cm.Items.Add(saveItem);
        btn.ContextMenu = cm;
        return btn;
    }
}

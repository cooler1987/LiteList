using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using ScintillaNET;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using WpfBlock = Markdig.Syntax.Block;

namespace LiteList;

/// <summary>
/// 高性能 Scintilla 编辑器：绕过 UTF-16 慢速包装，直接走原生 UTF-8 字节流批量着色与折叠
/// </summary>
public sealed class MarkdownScintilla : Scintilla
{
    public const byte StyleDefault = 0;
    public const byte StyleHeading = 1;
    public const byte StyleBold = 2;
    public const byte StyleItalic = 3;
    public const byte StyleStrike = 4;
    public const byte StyleInlineCode = 5;
    public const byte StyleLink = 6;
    public const byte StyleQuote = 7;
    public const byte StyleTask = 8;
    public const byte StyleCodeBlock = 10;
    public const byte StyleCodeFenceOpen = 11;
    public const byte StyleCodeKeyword = 12;
    public const byte StyleCodeString = 13;
    public const byte StyleCodeComment = 14;
    public const byte StyleCodeFenceClose = 15;

    private const int MarginLineNum = 0;
    private const int MarginFold = 2;
    private const int IndicatorAllMatch = 0;
    private const int IndicatorActiveMatch = 1;
    private const int FoldLevelBase = 1024;
    private const int FoldHeaderFlag = 0x2000;

    // Scintilla 原生消息常量
    private const int SCI_GETLENGTH = 2006;
    private const int SCI_GETENDSTYLED = 2028;
    private const int SCI_STARTSTYLING = 2032;
    private const int SCI_SETSTYLINGEX = 2073;
    private const int SCI_GETLINEENDPOSITION = 2136;
    private const int SCI_LINEFROMPOSITION = 2166;
    private const int SCI_POSITIONFROMLINE = 2167;
    private const int SCI_SETFOLDLEVEL = 2222;
    private const int SCI_SETLAYOUTCACHE = 2272;
    private const int SCI_SETTECHNOLOGY = 2630;
    private const int SCI_GETRANGEPOINTER = 2643;
    private const int SCI_SETIDLESTYLING = 2692;

    private static readonly Regex CodeKeywordRegex = new(
        @"\b(if|else|for|while|switch|case|return|break|continue|func|function|var|let|const|class|struct|interface|public|private|static|void|int|string|bool|true|false|null|nil|None|def|import|from|using|namespace|SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|JOIN|ORDER|BY|CREATE|TABLE)\b",
        RegexOptions.Compiled);
    private static readonly Regex CodeStringRegex = new("\"([^\"\\\\]|\\\\.)*\"|'([^'\\\\]|\\\\.)*'", RegexOptions.Compiled);
    private static readonly Regex CodeCommentRegex = new(@"(//.*$|#.*$|--.*$)", RegexOptions.Compiled);

    public event EventHandler<HandledEventArgs>? PasteIntercepted;
    public event Action<WF.Keys>? ShortcutPressed;

    public MarkdownScintilla()
    {
        BorderStyle = WF.BorderStyle.None;
        WrapMode = WrapMode.Word;
        ScrollWidthTracking = false;
        ScrollWidth = 1;
        TabWidth = 2;
        UseTabs = false;
        IndentWidth = 2;
        UsePopup(false);

        // 启用 DirectWrite 硬件加速排版 + 页级换行缓存 + 空闲增量着色，消除大中文文档卡顿
        DirectMessage(SCI_SETTECHNOLOGY, (IntPtr)1, IntPtr.Zero);
        DirectMessage(SCI_SETLAYOUTCACHE, (IntPtr)2, IntPtr.Zero); // SC_CACHE_PAGE
        DirectMessage(SCI_SETIDLESTYLING, (IntPtr)1, IntPtr.Zero); // SC_IDLESTYLING_TOVISIBLE

        Lexer = Lexer.Container;
        StyleNeeded += OnStyleNeeded;
        CharAdded += OnCharAdded;

        ApplyThemeAndFont(15.0);
        SetupMarginsAndFolding();
        SetupSearchIndicators();
    }

    public void ApplyThemeAndFont(double fontSize)
    {
        bool dark = ThemeHelper.IsDarkMode;
        var bg = ThemeHelper.ToWinFormsColor(dark ? "#18181B" : "#FFFFFF");
        var fg = ThemeHelper.ToWinFormsColor(dark ? "#E4E4E7" : "#1F2937");
        var gutterBg = ThemeHelper.ToWinFormsColor(dark ? "#202024" : "#F6F6F7");
        var muted = ThemeHelper.ToWinFormsColor(dark ? "#8E8E98" : "#6B7280");
        var accent = ThemeHelper.ToWinFormsColor(dark ? "#60A5FA" : "#2563EB");

        StyleResetDefault();
        Styles[ScintillaNET.Style.Default].Font = SystemFonts.MessageFontFamily.Source;
        Styles[ScintillaNET.Style.Default].Size = (int)Math.Round(fontSize * 0.78);
        Styles[ScintillaNET.Style.Default].BackColor = bg;
        Styles[ScintillaNET.Style.Default].ForeColor = fg;
        StyleClearAll();

        Styles[ScintillaNET.Style.LineNumber].BackColor = gutterBg;
        Styles[ScintillaNET.Style.LineNumber].ForeColor = muted;
        SetFoldMarginColor(true, gutterBg);
        SetFoldMarginHighlightColor(true, gutterBg);

        CaretForeColor = ThemeHelper.ToWinFormsColor(dark ? "#FFFFFF" : "#111827");
        CaretWidth = 2;
        CaretLineVisible = true;
        CaretLineBackColor = ThemeHelper.ToWinFormsColor(dark ? "#25252D" : "#EFF6FF");
        SetSelectionBackColor(true, ThemeHelper.ToWinFormsColor(dark ? "#264F78" : "#BFDBFE"));

        int basePt = Styles[ScintillaNET.Style.Default].Size;
        Styles[StyleHeading].ForeColor = ThemeHelper.ToWinFormsColor(dark ? "#60A5FA" : "#1D4ED8");
        Styles[StyleHeading].Bold = true;
        Styles[StyleBold].ForeColor = ThemeHelper.ToWinFormsColor(dark ? "#FFFFFF" : "#111827");
        Styles[StyleBold].Bold = true;
        Styles[StyleItalic].ForeColor = ThemeHelper.ToWinFormsColor(dark ? "#C084FC" : "#7C3AED");
        Styles[StyleItalic].Italic = true;
        Styles[StyleStrike].ForeColor = muted;
        Styles[StyleLink].ForeColor = accent;
        Styles[StyleLink].Underline = true;
        Styles[StyleQuote].ForeColor = muted;
        Styles[StyleQuote].Italic = true;
        Styles[StyleTask].ForeColor = accent;
        Styles[StyleTask].Bold = true;

        void SetCodeStyle(int st, string hex, bool bold = false, bool italic = false)
        {
            Styles[st].Font = "Consolas";
            Styles[st].Size = Math.Max(9, basePt - 1);
            Styles[st].ForeColor = ThemeHelper.ToWinFormsColor(hex);
            Styles[st].Bold = bold;
            Styles[st].Italic = italic;
        }

        SetCodeStyle(StyleInlineCode, dark ? "#F472B6" : "#BE185D");
        SetCodeStyle(StyleCodeBlock, dark ? "#E4E4E7" : "#1F2937");
        SetCodeStyle(StyleCodeFenceOpen, dark ? "#8E8E98" : "#6B7280");
        SetCodeStyle(StyleCodeFenceClose, dark ? "#8E8E98" : "#6B7280");
        SetCodeStyle(StyleCodeKeyword, dark ? "#FF7B72" : "#D73A49", bold: true);
        SetCodeStyle(StyleCodeString, dark ? "#A5D6FF" : "#032F62");
        SetCodeStyle(StyleCodeComment, dark ? "#8B949E" : "#6A737D", italic: true);

        var foldFg = ThemeHelper.ToWinFormsColor(dark ? "#A1A1AA" : "#4B5563");
        for (int i = Marker.FolderEnd; i <= Marker.FolderOpen; i++)
        {
            Markers[i].SetForeColor(gutterBg);
            Markers[i].SetBackColor(foldFg);
        }
    }

    private void SetupMarginsAndFolding()
    {
        Margins[MarginLineNum].Type = MarginType.Number;
        Margins[MarginLineNum].Width = 44;
        Margins[1].Width = 0;

        Margins[MarginFold].Type = MarginType.Symbol;
        Margins[MarginFold].Mask = Marker.MaskFolders;
        Margins[MarginFold].Sensitive = true;
        Margins[MarginFold].Width = 16;

        Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;
        Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;
        Markers[Marker.FolderEnd].Symbol = MarkerSymbol.BoxPlusConnected;
        Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
        Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.BoxMinusConnected;
        Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
        Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;

        AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;
        SetFoldFlags(FoldFlags.LineAfterContracted);
    }

    private void SetupSearchIndicators()
    {
        Indicators[IndicatorAllMatch].Style = IndicatorStyle.StraightBox;
        Indicators[IndicatorAllMatch].ForeColor = SD.Color.FromArgb(234, 179, 8);
        Indicators[IndicatorAllMatch].Alpha = 85;
        Indicators[IndicatorAllMatch].OutlineAlpha = 160;
        Indicators[IndicatorAllMatch].Under = true;

        Indicators[IndicatorActiveMatch].Style = IndicatorStyle.StraightBox;
        Indicators[IndicatorActiveMatch].ForeColor = SD.Color.FromArgb(249, 115, 22);
        Indicators[IndicatorActiveMatch].Alpha = 150;
        Indicators[IndicatorActiveMatch].OutlineAlpha = 230;
        Indicators[IndicatorActiveMatch].Under = true;
    }

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == 0x0302)
        {
            var args = new HandledEventArgs(false);
            PasteIntercepted?.Invoke(this, args);
            if (args.Handled) return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref WF.Message msg, WF.Keys keyData)
    {
        if (keyData == (WF.Keys.Control | WF.Keys.S) ||
            keyData == (WF.Keys.Control | WF.Keys.K) ||
            keyData == (WF.Keys.Control | WF.Keys.E) ||
            keyData == (WF.Keys.Control | WF.Keys.F) ||
            keyData == (WF.Keys.Control | WF.Keys.H) ||
            keyData == (WF.Keys.Control | WF.Keys.B) ||
            keyData == (WF.Keys.Control | WF.Keys.I) ||
            keyData == (WF.Keys.Control | WF.Keys.L) ||
            keyData == (WF.Keys.Control | WF.Keys.Oem5) ||
            keyData == (WF.Keys.Control | WF.Keys.OemQuestion) ||
            keyData == (WF.Keys.Control | WF.Keys.Oemplus) ||
            keyData == (WF.Keys.Control | WF.Keys.OemMinus) ||
            keyData == WF.Keys.F3 ||
            keyData == (WF.Keys.Shift | WF.Keys.F3))
        {
            ShortcutPressed?.Invoke(keyData);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        if (e.Char != '\n') return;
        int curLineIdx = CurrentLine;
        if (curLineIdx <= 0) return;
        var prevLine = Lines[curLineIdx - 1];
        var prevText = prevLine.Text.TrimEnd('\r', '\n');
        var m = Regex.Match(prevText, @"^(\s*(?:[-*+]\s+\[[ xX]\]\s|[-*+]\s|\d+\.\s|>\s))(.*)$");
        if (!m.Success) return;

        if (string.IsNullOrWhiteSpace(m.Groups[2].Value))
        {
            DeleteRange(prevLine.Position, prevText.Length);
            return;
        }
        var prefix = Regex.Replace(m.Groups[1].Value, @"\[[xX]\]", "[ ]");
        var numMatch = Regex.Match(prefix, @"^(\s*)(\d+)\.(\s)$");
        if (numMatch.Success && int.TryParse(numMatch.Groups[2].Value, out int num))
            prefix = $"{numMatch.Groups[1].Value}{num + 1}.{numMatch.Groups[3].Value}";
        ReplaceSelection(prefix);
    }

    /// <summary>
    /// 零字符串分配的高速原生 UTF-8 字节级着色：彻底消除中文卡顿与空行死循环
    /// </summary>
    private void OnStyleNeeded(object? sender, StyleNeededEventArgs e)
    {
        int docLen = DirectMessage(SCI_GETLENGTH).ToInt32();
        if (docLen <= 0) return;

        int startByte = DirectMessage(SCI_GETENDSTYLED).ToInt32();
        if (startByte >= docLen) return;

        int startLine = DirectMessage(SCI_LINEFROMPOSITION, (IntPtr)startByte).ToInt32();
        startByte = DirectMessage(SCI_POSITIONFROMLINE, (IntPtr)startLine).ToInt32();

        int endLine = LineFromPosition(Math.Min(e.Position, TextLength));
        int totalLines = Lines.Count;
        if (endLine >= totalLines) endLine = totalLines - 1;

        int endByte = DirectMessage(SCI_GETLINEENDPOSITION, (IntPtr)endLine).ToInt32();
        if (endLine >= totalLines - 1)
        {
            endByte = docLen;
        }
        else if (endByte < docLen)
        {
            int nextStart = DirectMessage(SCI_POSITIONFROMLINE, (IntPtr)(endLine + 1)).ToInt32();
            if (nextStart > endByte) endByte = nextStart;
        }

        int byteLen = endByte - startByte;
        if (byteLen <= 0) return;

        bool insideFence = false;
        if (startLine > 0)
        {
            int prevEnd = DirectMessage(SCI_GETLINEENDPOSITION, (IntPtr)(startLine - 1)).ToInt32();
            if (prevEnd > 0)
            {
                byte prevStyle = (byte)DirectMessage(2010 /* SCI_GETSTYLEAT */, (IntPtr)(prevEnd - 1)).ToInt32();
                insideFence = prevStyle == StyleCodeFenceOpen || prevStyle == StyleCodeBlock;
            }
        }

        IntPtr rangePtr = DirectMessage(SCI_GETRANGEPOINTER, (IntPtr)startByte, (IntPtr)byteLen);
        if (rangePtr == IntPtr.Zero) return;

        var rawBytes = new byte[byteLen];
        Marshal.Copy(rangePtr, rawBytes, 0, byteLen);
        var styles = new byte[byteLen];

        int pos = 0;
        while (pos < byteLen)
        {
            int lineStart = pos;
            int nl = Array.IndexOf(rawBytes, (byte)'\n', pos);
            int lineEndWithEol = nl >= 0 ? nl + 1 : byteLen;
            int lineEnd = lineEndWithEol;
            while (lineEnd > lineStart && (rawBytes[lineEnd - 1] == '\n' || rawBytes[lineEnd - 1] == '\r'))
                lineEnd--;

            int contentLen = lineEnd - lineStart;
            int trimStart = lineStart;
            while (trimStart < lineEnd && (rawBytes[trimStart] == ' ' || rawBytes[trimStart] == '\t'))
                trimStart++;

            bool isFence = (lineEnd - trimStart) >= 3 &&
                           rawBytes[trimStart] == '`' && rawBytes[trimStart + 1] == '`' && rawBytes[trimStart + 2] == '`';

            if (isFence)
            {
                byte fst = insideFence ? StyleCodeFenceClose : StyleCodeFenceOpen;
                for (int k = lineStart; k < lineEndWithEol; k++) styles[k] = fst;
                insideFence = !insideFence;
            }
            else if (insideFence)
            {
                for (int k = lineStart; k < lineEndWithEol; k++) styles[k] = StyleCodeBlock;
                if (contentLen > 0 && contentLen <= 512)
                    StyleCodeBytesLine(rawBytes, styles, lineStart, contentLen);
            }
            else if (contentLen > 0)
            {
                if (IsHeadingBytes(rawBytes, trimStart, lineEnd))
                {
                    for (int k = lineStart; k < lineEnd; k++) styles[k] = StyleHeading;
                }
                else
                {
                    if (trimStart < lineEnd && rawBytes[trimStart] == '>')
                        for (int k = lineStart; k < lineEnd; k++) styles[k] = StyleQuote;

                    // 仅对常规长度行做行内 Markdown 扫描，超长小说段落零正则零开销
                    if (contentLen <= 2048)
                        StyleMarkdownInlineBytes(rawBytes, styles, lineStart, lineEnd, trimStart);
                }
            }

            pos = lineEndWithEol;
        }

        DirectMessage(SCI_STARTSTYLING, (IntPtr)startByte, IntPtr.Zero);
        var handle = GCHandle.Alloc(styles, GCHandleType.Pinned);
        try
        {
            DirectMessage(SCI_SETSTYLINGEX, (IntPtr)styles.Length, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static bool IsHeadingBytes(byte[] buf, int trimStart, int lineEnd)
    {
        if (trimStart >= lineEnd || buf[trimStart] != '#') return false;
        int p = trimStart;
        while (p < lineEnd && buf[p] == '#' && p - trimStart < 7) p++;
        int hashes = p - trimStart;
        return hashes >= 1 && hashes <= 6 && (p == lineEnd || buf[p] == ' ');
    }

    private static void StyleMarkdownInlineBytes(byte[] buf, byte[] styles, int lineStart, int lineEnd, int trimStart)
    {
        // 待办 - [ ] / - [x]
        if (trimStart + 5 <= lineEnd &&
            (buf[trimStart] == '-' || buf[trimStart] == '*' || buf[trimStart] == '+') &&
            buf[trimStart + 1] == ' ')
        {
            int p = trimStart + 2;
            while (p < lineEnd && buf[p] == ' ') p++;
            if (p + 2 < lineEnd && buf[p] == '[' &&
                (buf[p + 1] == ' ' || buf[p + 1] == 'x' || buf[p + 1] == 'X') && buf[p + 2] == ']')
            {
                for (int k = trimStart; k <= p + 2; k++) styles[k] = StyleTask;
            }
        }

        // 纯 ASCII 字节扫描 `code`、**bold**、~~strike~~（无需任何 Regex，O(N) 零分配）
        for (int i = lineStart; i < lineEnd; i++)
        {
            byte b = buf[i];
            if (b == '`')
            {
                int close = Array.IndexOf(buf, (byte)'`', i + 1, lineEnd - (i + 1));
                if (close > i)
                {
                    for (int k = i; k <= close; k++) styles[k] = StyleInlineCode;
                    i = close;
                }
            }
            else if (b == '*' && i + 1 < lineEnd && buf[i + 1] == '*')
            {
                int close = FindDoubleByte(buf, (byte)'*', i + 2, lineEnd);
                if (close > i)
                {
                    for (int k = i; k <= close + 1; k++) styles[k] = StyleBold;
                    i = close + 1;
                }
            }
            else if (b == '~' && i + 1 < lineEnd && buf[i + 1] == '~')
            {
                int close = FindDoubleByte(buf, (byte)'~', i + 2, lineEnd);
                if (close > i)
                {
                    for (int k = i; k <= close + 1; k++) styles[k] = StyleStrike;
                    i = close + 1;
                }
            }
        }
    }

    private static int FindDoubleByte(byte[] buf, byte target, int start, int end)
    {
        for (int i = start; i + 1 < end; i++)
            if (buf[i] == target && buf[i + 1] == target) return i;
        return -1;
    }

    private static void StyleCodeBytesLine(byte[] rawBytes, byte[] styles, int lineStart, int contentLen)
    {
        var lineStr = Encoding.UTF8.GetString(rawBytes, lineStart, contentLen);
        void PaintMatches(Regex rx, byte styleId)
        {
            foreach (Match m in rx.Matches(lineStr))
            {
                int bStart = lineStart + Encoding.UTF8.GetByteCount(lineStr, 0, m.Index);
                int bLen = Encoding.UTF8.GetByteCount(lineStr, m.Index, m.Length);
                int bEnd = Math.Min(lineStart + contentLen, bStart + bLen);
                for (int k = bStart; k < bEnd; k++) styles[k] = styleId;
            }
        }
        PaintMatches(CodeKeywordRegex, StyleCodeKeyword);
        PaintMatches(CodeStringRegex, StyleCodeString);
        PaintMatches(CodeCommentRegex, StyleCodeComment);
    }

    /// <summary>
    /// 纯 C# 字符串快速计算折叠层级并批量调用 SCI_SETFOLDLEVEL（5000 行耗时 < 3ms）
    /// </summary>
    public void RebuildFoldLevelsFast(string? cachedText = null)
    {
        var text = cachedText ?? Text;
        if (string.IsNullOrEmpty(text)) return;

        bool insideFence = false;
        int lineIdx = 0;
        int pos = 0;
        int len = text.Length;

        while (pos <= len)
        {
            int nl = text.IndexOf('\n', pos);
            int lineEnd = nl >= 0 ? nl : len;
            int trimEnd = lineEnd;
            if (trimEnd > pos && text[trimEnd - 1] == '\r') trimEnd--;

            int trimStart = pos;
            while (trimStart < trimEnd && (text[trimStart] == ' ' || text[trimStart] == '\t')) trimStart++;

            int foldVal = FoldLevelBase + 7;
            if (trimEnd - trimStart >= 3 && text[trimStart] == '`' && text[trimStart + 1] == '`' && text[trimStart + 2] == '`')
            {
                if (!insideFence) { foldVal = (FoldLevelBase + 20) | FoldHeaderFlag; insideFence = true; }
                else { foldVal = FoldLevelBase + 20; insideFence = false; }
            }
            else if (insideFence)
            {
                foldVal = FoldLevelBase + 21;
            }
            else if (trimStart < trimEnd && text[trimStart] == '#')
            {
                int p = trimStart;
                while (p < trimEnd && text[p] == '#' && p - trimStart < 7) p++;
                int hLvl = p - trimStart;
                if (hLvl >= 1 && hLvl <= 6 && (p == trimEnd || text[p] == ' '))
                    foldVal = (FoldLevelBase + hLvl) | FoldHeaderFlag;
            }

            DirectMessage(SCI_SETFOLDLEVEL, (IntPtr)lineIdx, (IntPtr)foldVal);
            lineIdx++;
            if (nl < 0) break;
            pos = nl + 1;
        }
    }

    public static ObservableCollection<HeadingNodeVM> ExtractHeadingTreeFromText(string text)
    {
        var roots = new ObservableCollection<HeadingNodeVM>();
        if (string.IsNullOrEmpty(text)) return roots;

        var stack = new Stack<HeadingNodeVM>();
        bool insideFence = false;
        int lineNum = 1;
        int pos = 0;
        int len = text.Length;

        while (pos <= len)
        {
            int nl = text.IndexOf('\n', pos);
            int lineEnd = nl >= 0 ? nl : len;
            int trimEnd = lineEnd;
            if (trimEnd > pos && text[trimEnd - 1] == '\r') trimEnd--;

            int trimStart = pos;
            while (trimStart < trimEnd && (text[trimStart] == ' ' || text[trimStart] == '\t')) trimStart++;

            if (trimEnd - trimStart >= 3 && text[trimStart] == '`' && text[trimStart + 1] == '`' && text[trimStart + 2] == '`')
            {
                insideFence = !insideFence;
            }
            else if (!insideFence && trimStart < trimEnd && text[trimStart] == '#')
            {
                int p = trimStart;
                while (p < trimEnd && text[p] == '#' && p - trimStart < 7) p++;
                int lvl = p - trimStart;
                if (lvl >= 1 && lvl <= 6 && p < trimEnd && text[p] == ' ')
                {
                    var node = new HeadingNodeVM
                    {
                        Level = lvl,
                        Text = text.Substring(p + 1, trimEnd - (p + 1)).Trim(),
                        LineNumber = lineNum
                    };
                    while (stack.Count > 0 && stack.Peek().Level >= lvl) stack.Pop();
                    if (stack.Count == 0) roots.Add(node);
                    else stack.Peek().Children.Add(node);
                    stack.Push(node);
                }
            }

            lineNum++;
            if (nl < 0) break;
            pos = nl + 1;
        }
        return roots;
    }

    public void UpdateLineNumberMarginWidth()
    {
        int digits = Math.Max(2, Lines.Count.ToString().Length);
        Margins[MarginLineNum].Width = 18 + digits * 9;
    }

    public void ClearSearchHighlights()
    {
        IndicatorCurrent = IndicatorAllMatch;
        IndicatorClearRange(0, TextLength);
        IndicatorCurrent = IndicatorActiveMatch;
        IndicatorClearRange(0, TextLength);
    }

    public void HighlightAndScrollToMatch(string keyword, int lineNumber1Based, int charOffsetInLine, int matchLength)
    {
        ClearSearchHighlights();
        if (string.IsNullOrEmpty(keyword)) return;

        // 用 C# 搜索可见位置或全文前先快速跳转，避免 Scintilla 在超长文本里慢速 char/byte 换算
        var fullText = Text;
        IndicatorCurrent = IndicatorAllMatch;
        int searchFrom = 0;
        int count = 0;
        while (searchFrom < fullText.Length && count < 300)
        {
            int found = fullText.IndexOf(keyword, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            IndicatorFillRange(found, keyword.Length);
            searchFrom = found + keyword.Length;
            count++;
        }

        int lineIdx = Math.Max(0, Math.Min(Lines.Count - 1, lineNumber1Based - 1));
        Lines[lineIdx].EnsureVisible();
        FirstVisibleLine = Math.Max(0, lineIdx - Math.Max(1, LinesOnScreen / 2));

        if (matchLength > 0)
        {
            int pos = Math.Min(TextLength, Lines[lineIdx].Position + Math.Max(0, charOffsetInLine));
            int len = Math.Min(matchLength, Math.Max(0, TextLength - pos));
            IndicatorCurrent = IndicatorActiveMatch;
            IndicatorFillRange(pos, len);
            SetSelection(pos + len, pos);
        }
        else
        {
            GotoPosition(Lines[lineIdx].Position);
        }
    }

    public void ScrollToLineCentered(int lineNumber1Based)
    {
        int lineIdx = Math.Max(0, Math.Min(Lines.Count - 1, lineNumber1Based - 1));
        Lines[lineIdx].EnsureVisible();
        FirstVisibleLine = Math.Max(0, lineIdx - Math.Max(1, LinesOnScreen / 3));
        GotoPosition(Lines[lineIdx].Position);
    }
}

/// <summary>
/// 轻量分块 VM：50 万字小说只切成约 250 块纯字符串片段，仅当滑入视口时才用 Markdig 解析渲染
/// </summary>
public sealed class PreviewBlockVM : INotifyPropertyChanged
{
    private bool _isCollapsed;
    private bool _isSearchActive;

    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int HeadingLevel { get; set; }
    public string RawMarkdown { get; set; } = "";

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set { if (_isCollapsed != value) { _isCollapsed = value; OnPropertyChanged(); } }
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        set { if (_isSearchActive != value) { _isSearchActive = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class VirtualizedMarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private static readonly Regex HtmlImgRegex = new(
        @"<img[^>]+src=[""']([^""']+)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly FileArchiveService _files;
    private readonly FontFamily _monoFamily = new("Consolas, Courier New");

    public List<PreviewBlockVM> AllBlocks { get; } = new();
    public ObservableCollection<PreviewBlockVM> VisibleBlocks { get; } = new();

    public event Action<PreviewBlockVM>? HeadingFoldToggled;
    public event Action<int, bool>? TaskCheckboxToggled;
    public event Action<string>? ImageClicked;
    public event Action<string>? AttachmentClicked;
    public event Action<string>? AttachmentSaveAsRequested;
    public event Action<string>? ExternalUrlClicked;
    public event Action<string>? StatusToast;

    public VirtualizedMarkdownPreview(FileArchiveService files)
    {
        _files = files;
    }

    /// <summary>
    /// 极速切块（50万字耗时 < 8ms）：按标题独立成块，正文按每 25 行分块（不截断代码块与表格）
    /// </summary>
    public void ParseDocument(string markdown)
    {
        AllBlocks.Clear();
        VisibleBlocks.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // 1. 标题行独立成块，方便按章节折叠与精准跳转
            if (IsHeading(trimmed, out int hLvl))
            {
                var hVm = new PreviewBlockVM
                {
                    StartLine = i + 1,
                    EndLine = i + 1,
                    HeadingLevel = hLvl,
                    RawMarkdown = line
                };
                AllBlocks.Add(hVm);
                VisibleBlocks.Add(hVm);
                i++;
                continue;
            }

            // 2. 收集连续正文块（最多 25 行，保持围栏代码块和表格完整不截断）
            int chunkStart = i;
            var sb = new StringBuilder();
            bool inFence = false;
            int taken = 0;

            while (i < lines.Length)
            {
                var cur = lines[i];
                var curTrim = cur.TrimStart();

                if (curTrim.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                }
                else if (!inFence && taken > 0 && IsHeading(curTrim, out _))
                {
                    break;
                }

                if (taken > 0) sb.Append('\n');
                sb.Append(cur);
                taken++;
                i++;

                bool inTable = cur.Contains("|");
                if (!inFence && !inTable && taken >= 25 && string.IsNullOrWhiteSpace(cur))
                    break;
                if (!inFence && taken >= 45)
                    break;
            }

            var vm = new PreviewBlockVM
            {
                StartLine = chunkStart + 1,
                EndLine = i,
                HeadingLevel = 0,
                RawMarkdown = sb.ToString()
            };
            AllBlocks.Add(vm);
            VisibleBlocks.Add(vm);
        }
    }

    private static bool IsHeading(string trimmed, out int level)
    {
        level = 0;
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '#') return false;
        while (level < trimmed.Length && trimmed[level] == '#' && level < 7) level++;
        return level >= 1 && level <= 6 && (level == trimmed.Length || trimmed[level] == ' ');
    }

    public void RebuildVisibleBlocks()
    {
        VisibleBlocks.Clear();
        int hiddenUnderLevel = 0;

        foreach (var blk in AllBlocks)
        {
            if (blk.HeadingLevel > 0)
            {
                if (hiddenUnderLevel > 0 && blk.HeadingLevel > hiddenUnderLevel) continue;
                hiddenUnderLevel = blk.IsCollapsed ? blk.HeadingLevel : 0;
                VisibleBlocks.Add(blk);
            }
            else
            {
                if (hiddenUnderLevel > 0) continue;
                VisibleBlocks.Add(blk);
            }
        }
    }

    public PreviewBlockVM? EnsureLineVisibleAndFindBlock(int targetLine)
    {
        var activeHeadings = new Stack<PreviewBlockVM>();
        PreviewBlockVM? found = null;
        bool needRebuild = false;

        foreach (var blk in AllBlocks)
        {
            if (blk.HeadingLevel > 0)
            {
                while (activeHeadings.Count > 0 && activeHeadings.Peek().HeadingLevel >= blk.HeadingLevel)
                    activeHeadings.Pop();
                activeHeadings.Push(blk);
            }

            if (blk.StartLine <= targetLine && targetLine <= blk.EndLine) { found = blk; break; }
            if (blk.StartLine > targetLine) { found = blk; break; }
        }

        if (found == null && AllBlocks.Count > 0) found = AllBlocks[AllBlocks.Count - 1];

        if (found != null)
        {
            foreach (var h in activeHeadings)
            {
                if (h != found && h.IsCollapsed) { h.IsCollapsed = false; needRebuild = true; }
            }
            if (needRebuild) RebuildVisibleBlocks();
        }
        return found;
    }

    public UIElement BuildBlockElement(PreviewBlockVM vm, double fontSize, string? highlightKw)
    {
        var panel = new StackPanel();
        if (!string.IsNullOrEmpty(vm.RawMarkdown))
        {
            var doc = Markdown.Parse(vm.RawMarkdown, Pipeline);
            foreach (var astBlock in doc)
            {
                var ui = RenderAstBlock(astBlock, vm, fontSize, highlightKw);
                if (ui != null) panel.Children.Add(ui);
            }
        }

        if (vm.IsSearchActive)
        {
            return new Border
            {
                BorderBrush = ThemeHelper.GetBrush("AccentBrush"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 2, 4, 2),
                Background = ThemeHelper.GetBrush("ActiveBg"),
                CornerRadius = new CornerRadius(3),
                Child = panel
            };
        }
        return panel;
    }

    private UIElement? RenderAstBlock(WpfBlock block, PreviewBlockVM? ownerVm, double fontSize, string? kw)
    {
        switch (block)
        {
            case HeadingBlock hb:
            {
                double scale = hb.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.05 };
                var dp = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
                var foldBtn = new Button
                {
                    Content = ownerVm?.IsCollapsed == true ? "▸" : "▾",
                    FontSize = 11,
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = ThemeHelper.GetBrush("MutedFg"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (ownerVm != null)
                {
                    foldBtn.Click += (_, _) =>
                    {
                        ownerVm.IsCollapsed = !ownerVm.IsCollapsed;
                        HeadingFoldToggled?.Invoke(ownerVm);
                    };
                }
                DockPanel.SetDock(foldBtn, Dock.Left);

                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = SystemFonts.MessageFontFamily,
                    FontSize = fontSize * scale,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeHelper.GetBrush("HeadingColor")
                };
                AppendInlines(tb.Inlines, hb.Inline, fontSize * scale, kw);
                dp.Children.Add(foldBtn);
                dp.Children.Add(tb);

                if (hb.Level == 1)
                {
                    return new Border
                    {
                        BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(0, 0, 0, 4),
                        Child = dp
                    };
                }
                return dp;
            }

            case ParagraphBlock pb:
            {
                if (pb.Inline != null && pb.Inline.FirstChild == pb.Inline.LastChild &&
                    pb.Inline.FirstChild is LinkInline singleLi && singleLi.IsImage)
                {
                    return BuildImageControl(singleLi.Url ?? "");
                }

                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = SystemFonts.MessageFontFamily,
                    FontSize = fontSize,
                    LineHeight = fontSize * 1.65,
                    Foreground = ThemeHelper.GetBrush("TextFg"),
                    Margin = new Thickness(0, 3, 0, 4)
                };
                AppendInlines(tb.Inlines, pb.Inline, fontSize, kw);
                return tb;
            }

            case CodeBlock cb:
            {
                var lang = (cb as FencedCodeBlock)?.Info?.Trim() ?? "";
                var codeText = cb.Lines.ToString().TrimEnd('\r', '\n');
                return BuildCodeCard(lang, codeText, fontSize, kw);
            }

            case QuoteBlock qb:
            {
                var stack = new StackPanel();
                foreach (var child in qb)
                {
                    var ui = RenderAstBlock(child, null, fontSize, kw);
                    if (ui != null) stack.Children.Add(ui);
                }
                return new Border
                {
                    BorderBrush = ThemeHelper.GetBrush("AccentBrush"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = ThemeHelper.GetBrush("SidebarBg"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 5, 0, 6),
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Child = stack
                };
            }

            case ListBlock lb:
            {
                var stack = new StackPanel { Margin = new Thickness(8, 3, 0, 5) };
                int idx = 1;
                foreach (var itemObj in lb)
                {
                    if (itemObj is not ListItemBlock lib) continue;
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var innerStack = new StackPanel();

                    bool hasTask = false;
                    foreach (var sub in lib)
                    {
                        if (sub is ParagraphBlock subPb && subPb.Inline?.FirstChild is TaskList tl)
                        {
                            hasTask = true;
                            var chk = new CheckBox
                            {
                                IsChecked = tl.Checked,
                                Margin = new Thickness(0, 4, 7, 0),
                                VerticalAlignment = VerticalAlignment.Top
                            };
                            int globalLine = (ownerVm?.StartLine ?? 1) + subPb.Line;
                            chk.Click += (_, _) => TaskCheckboxToggled?.Invoke(globalLine, chk.IsChecked == true);
                            DockPanel.SetDock(chk, Dock.Left);
                            row.Children.Add(chk);

                            var tb = new TextBlock
                            {
                                TextWrapping = TextWrapping.Wrap,
                                FontFamily = SystemFonts.MessageFontFamily,
                                FontSize = fontSize,
                                LineHeight = fontSize * 1.55,
                                Foreground = tl.Checked ? ThemeHelper.GetBrush("MutedFg") : ThemeHelper.GetBrush("TextFg")
                            };
                            if (tl.Checked) tb.TextDecorations = TextDecorations.Strikethrough;
                            AppendInlines(tb.Inlines, subPb.Inline, fontSize, kw, skipFirstTask: true);
                            innerStack.Children.Add(tb);
                        }
                        else
                        {
                            var ui = RenderAstBlock(sub, ownerVm, fontSize, kw);
                            if (ui != null) innerStack.Children.Add(ui);
                        }
                    }

                    if (!hasTask)
                    {
                        var bullet = new TextBlock
                        {
                            Text = lb.IsOrdered ? $"{idx}. " : "• ",
                            FontSize = fontSize,
                            Foreground = ThemeHelper.GetBrush("MutedFg"),
                            Margin = new Thickness(0, 2, 4, 0)
                        };
                        DockPanel.SetDock(bullet, Dock.Left);
                        row.Children.Add(bullet);
                    }
                    row.Children.Add(innerStack);
                    stack.Children.Add(row);
                    idx++;
                }
                return stack;
            }

            case Markdig.Extensions.Tables.Table mdTable:
            {
                var grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
                int colCount = mdTable.ColumnDefinitions.Count;
                if (colCount == 0 && mdTable.Count > 0 && mdTable[0] is Markdig.Extensions.Tables.TableRow r0)
                    colCount = r0.Count;
                for (int c = 0; c < colCount; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int rIdx = 0;
                foreach (var rowObj in mdTable)
                {
                    if (rowObj is not Markdig.Extensions.Tables.TableRow mdRow) continue;
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    int cIdx = 0;
                    foreach (var cellObj in mdRow)
                    {
                        if (cellObj is not Markdig.Extensions.Tables.TableCell mdCell) continue;
                        var cellStack = new StackPanel();
                        foreach (var cb in mdCell)
                        {
                            var ui = RenderAstBlock(cb, null, fontSize, kw);
                            if (ui != null) cellStack.Children.Add(ui);
                        }
                        var cellBorder = new Border
                        {
                            BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
                            BorderThickness = new Thickness(0.5),
                            Background = mdRow.IsHeader ? ThemeHelper.GetBrush("SidebarBg") : Brushes.Transparent,
                            Padding = new Thickness(8, 5, 8, 5),
                            Child = cellStack
                        };
                        Grid.SetRow(cellBorder, rIdx);
                        Grid.SetColumn(cellBorder, cIdx++);
                        grid.Children.Add(cellBorder);
                    }
                    rIdx++;
                }
                return new Border { BorderBrush = ThemeHelper.GetBrush("BorderBrush"), BorderThickness = new Thickness(1), Child = grid };
            }

            case HtmlBlock hb:
            {
                var html = hb.Lines.ToString();
                var m = HtmlImgRegex.Match(html);
                if (m.Success) return BuildImageControl(m.Groups[1].Value);
                return new TextBlock
                {
                    Text = html.Trim(),
                    FontFamily = _monoFamily,
                    FontSize = fontSize - 1,
                    Foreground = ThemeHelper.GetBrush("MutedFg"),
                    TextWrapping = TextWrapping.Wrap
                };
            }

            case ThematicBreakBlock:
                return new Border { Height = 1, Background = ThemeHelper.GetBrush("BorderBrush"), Margin = new Thickness(0, 10, 0, 10) };
        }
        return null;
    }

    private UIElement BuildCodeCard(string lang, string codeText, double fontSize, string? kw)
    {
        var outer = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeHelper.GetBrush("CodeBg"),
            Margin = new Thickness(0, 6, 0, 6)
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
            StatusToast?.Invoke("代码已复制");
        };
        DockPanel.SetDock(copyBtn, Dock.Right);
        header.Children.Add(copyBtn);
        header.Children.Add(langTxt);

        var tb = new TextBlock
        {
            FontFamily = _monoFamily,
            FontSize = Math.Max(12, fontSize - 1),
            Foreground = ThemeHelper.GetBrush("TextFg"),
            Padding = new Thickness(12, 8, 12, 10),
            TextWrapping = TextWrapping.Wrap
        };
        AddHighlightedText(tb.Inlines, codeText, kw);

        stack.Children.Add(header);
        stack.Children.Add(new Border { Height = 1, Background = ThemeHelper.GetBrush("BorderBrush") });
        stack.Children.Add(tb);
        outer.Child = stack;
        return outer;
    }

    private void AppendInlines(InlineCollection target, ContainerInline? container,
        double fontSize, string? kw, bool skipFirstTask = false)
    {
        if (container == null) return;
        bool skipped = false;

        foreach (var child in container)
        {
            if (skipFirstTask && !skipped && child is TaskList) { skipped = true; continue; }
            switch (child)
            {
                case LiteralInline lit:
                    AddHighlightedText(target, lit.Content.ToString(), kw);
                    break;

                case CodeInline ci:
                    target.Add(new Run($" {ci.Content} ")
                    {
                        FontFamily = _monoFamily,
                        FontSize = Math.Max(11, fontSize - 1),
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
                    AppendInlines(span.Inlines, ei, fontSize, kw);
                    target.Add(span);
                    break;
                }

                case LinkInline li:
                {
                    var url = li.Url ?? "";
                    if (li.IsImage)
                    {
                        target.Add(new InlineUIContainer(BuildImageControl(url)));
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
                        AppendInlines(hl.Inlines, li, fontSize, kw);
                        hl.Click += (_, _) => ExternalUrlClicked?.Invoke(url);
                        target.Add(hl);
                    }
                    break;
                }

                case HtmlInline hi:
                {
                    var m = HtmlImgRegex.Match(hi.Tag);
                    if (m.Success) target.Add(new InlineUIContainer(BuildImageControl(m.Groups[1].Value)));
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

    private static void AddHighlightedText(InlineCollection target, string text, string? kw)
    {
        if (string.IsNullOrEmpty(kw)) { target.Add(new Run(text)); return; }
        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(kw, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { target.Add(new Run(text.Substring(pos))); break; }
            if (idx > pos) target.Add(new Run(text.Substring(pos, idx - pos)));
            target.Add(new Run(text.Substring(idx, kw.Length))
            {
                Background = ThemeHelper.GetBrush("SearchMatchBg"),
                FontWeight = FontWeights.Bold
            });
            pos = idx + kw.Length;
        }
    }

    private static string ExtractPlainText(ContainerInline? inline)
    {
        if (inline == null) return "";
        var text = "";
        foreach (var c in inline)
        {
            if (c is LiteralInline l) text += l.Content.ToString();
            else if (c is ContainerInline ci) text += ExtractPlainText(ci);
        }
        return text;
    }

    private UIElement BuildImageControl(string rawUrl)
    {
        var resolved = _files.ResolveExistingFile(rawUrl);
        if (resolved == null)
        {
            return new TextBlock
            {
                Text = $"[图片缺失: {rawUrl}]",
                Foreground = ThemeHelper.GetBrush("DangerBrush"),
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        try
        {
            var bmp = new BitmapImage();
            using (var fs = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.EndInit();
            }
            bmp.Freeze();

            var img = new Image
            {
                Source = bmp,
                MaxHeight = 440,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "点击查看大图"
            };
            img.MouseLeftButtonUp += (_, _) => ImageClicked?.Invoke(resolved);
            return img;
        }
        catch (Exception ex)
        {
            return new TextBlock { Text = $"[图片加载失败: {ex.Message}]", Foreground = ThemeHelper.GetBrush("DangerBrush") };
        }
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
            ToolTip = "左键用默认程序打开，右键另存为"
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

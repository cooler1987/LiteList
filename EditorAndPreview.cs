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

public sealed class MarkdownScintilla : Scintilla
{
    public const int StyleDefault = 0;
    public const int StyleHeading = 1;
    public const int StyleBold = 2;
    public const int StyleItalic = 3;
    public const int StyleStrike = 4;
    public const int StyleInlineCode = 5;
    public const int StyleLink = 6;
    public const int StyleQuote = 7;
    public const int StyleTask = 8;
    public const int StyleCodeBlock = 10;
    public const int StyleCodeFenceOpen = 11;
    public const int StyleCodeKeyword = 12;
    public const int StyleCodeString = 13;
    public const int StyleCodeComment = 14;
    public const int StyleCodeFenceClose = 15;

    private const int MarginLineNum = 0;
    private const int MarginFold = 2;
    private const int IndicatorAllMatch = 0;
    private const int IndicatorActiveMatch = 1;
    private const int FoldLevelBase = 1024;
    private const int FoldHeaderFlag = 0x2000;

    // Scintilla 原生消息常量（绕过 ScintillaNET 逐行 UTF8/UTF16 转换瓶颈）
    private const int SCI_GETSTYLEAT = 2010;
    private const int SCI_GETENDSTYLED = 2028;
    private const int SCI_STARTSTYLING = 2032;
    private const int SCI_SETSTYLING = 2033;
    private const int SCI_GETLINEENDPOSITION = 2136;
    private const int SCI_LINEFROMPOSITION = 2166;
    private const int SCI_POSITIONFROMLINE = 2167;
    private const int SCI_GETTEXTLENGTH = 2183;
    private const int SCI_SETFOLDLEVEL = 2222;
    private const int SCI_GETRANGEPOINTER = 2643;

    private static readonly Regex InlineCodeRegex = new(@"`[^`\r\n]+`", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*[^*\r\n]+\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<!\*)\*[^*\s\r\n][^*\r\n]*\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex StrikeRegex = new(@"~~[^~\r\n]+~~", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"!?\[[^\]]*\]\([^\)\r\n]+\)", RegexOptions.Compiled);
    private static readonly Regex TaskPrefixRegex = new(@"^\s*[-*+]\s+\[[ xX]\]", RegexOptions.Compiled);
    private static readonly Regex CodeKeywordRegex = new(
        @"\b(if|else|for|while|switch|case|return|break|continue|func|function|var|let|const|class|struct|interface|public|private|static|void|int|string|bool|true|false|null|nil|None|def|import|from|async|await|new|throw|try|catch|finally|using|namespace|enum|extends|implements|this|self|select|insert|update|delete|create|table)\b",
        RegexOptions.Compiled);
    private static readonly Regex CodeStringRegex = new("\"([^\"\\\\]|\\\\.)*\"|'([^'\\\\]|\\\\.)*'", RegexOptions.Compiled);
    private static readonly Regex CodeCommentRegex = new(@"(//.*$|#.*$|--.*$)", RegexOptions.Compiled);

    public event EventHandler<HandledEventArgs>? PasteIntercepted;
    public event Action<WF.Keys>? ShortcutPressed;

    public MarkdownScintilla()
    {
        BorderStyle = WF.BorderStyle.None;
        // 启用 DirectWrite 硬件加速渲染与自动换行，彻底解决大段中文 GDI 卡顿
        Technology = Technology.DirectWrite;
        BufferedDraw = true;
        WrapMode = WrapMode.Word;
        ScrollWidthTracking = true;
        TabWidth = 2;
        UseTabs = false;
        IndentWidth = 2;
        UsePopup(false);

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
        if (m.Msg == 0x0302) // WM_PASTE
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
    /// 零拷贝 UTF-8 字节级增量着色：跳过纯文本小说行，直接批量 DirectMessage 写入样式
    /// 5 万字中文文档全量着色耗时约 2~5 毫秒（原方案约 60 秒）
    /// </summary>
    private void OnStyleNeeded(object? sender, StyleNeededEventArgs e)
    {
        int startByte = DirectMessage(SCI_GETENDSTYLED, IntPtr.Zero, IntPtr.Zero).ToInt32();
        int docByteLen = DirectMessage(SCI_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (docByteLen <= 0 || startByte >= docByteLen) return;

        int startLine = DirectMessage(SCI_LINEFROMPOSITION, (IntPtr)startByte, IntPtr.Zero).ToInt32();
        int lineStartByte = DirectMessage(SCI_POSITIONFROMLINE, (IntPtr)startLine, IntPtr.Zero).ToInt32();

        int endLine = LineFromPosition(e.Position);
        int totalLines = Lines.Count;
        int endByte = (endLine + 1 < totalLines)
            ? DirectMessage(SCI_POSITIONFROMLINE, (IntPtr)(endLine + 1), IntPtr.Zero).ToInt32()
            : docByteLen;

        int sliceLen = endByte - lineStartByte;
        if (sliceLen <= 0) return;

        IntPtr ptr = DirectMessage(SCI_GETRANGEPOINTER, (IntPtr)lineStartByte, (IntPtr)sliceLen);
        if (ptr == IntPtr.Zero) return;

        byte[] buf = new byte[sliceLen];
        Marshal.Copy(ptr, buf, 0, sliceLen);

        bool insideFence = false;
        if (lineStartByte > 0)
        {
            int prevStyle = DirectMessage(SCI_GETSTYLEAT, (IntPtr)(lineStartByte - 1), IntPtr.Zero).ToInt32();
            insideFence = prevStyle == StyleCodeFenceOpen || prevStyle == StyleCodeBlock;
        }

        DirectMessage(SCI_STARTSTYLING, (IntPtr)lineStartByte, IntPtr.Zero);

        int plainBatchBytes = 0;
        void FlushPlainBatch()
        {
            if (plainBatchBytes > 0)
            {
                DirectMessage(SCI_SETSTYLING, (IntPtr)plainBatchBytes, (IntPtr)StyleDefault);
                plainBatchBytes = 0;
            }
        }

        int pos = 0;
        while (pos < sliceLen)
        {
            int nlIdx = Array.IndexOf(buf, (byte)'\n', pos);
            int lineEnd = nlIdx >= 0 ? nlIdx + 1 : sliceLen;
            int lineByteLen = lineEnd - pos;

            int contentByteLen = lineByteLen;
            if (contentByteLen > 0 && buf[pos + contentByteLen - 1] == (byte)'\n') contentByteLen--;
            if (contentByteLen > 0 && buf[pos + contentByteLen - 1] == (byte)'\r') contentByteLen--;

            bool mayHaveSyntax = insideFence;
            if (!mayHaveSyntax)
            {
                for (int b = pos; b < pos + contentByteLen; b++)
                {
                    byte ch = buf[b];
                    if (ch == (byte)'#' || ch == (byte)'*' || ch == (byte)'`' ||
                        ch == (byte)'~' || ch == (byte)'[' || ch == (byte)'>' ||
                        ch == (byte)'-' || ch == (byte)'+')
                    {
                        mayHaveSyntax = true;
                        break;
                    }
                }
            }

            if (!mayHaveSyntax)
            {
                plainBatchBytes += lineByteLen;
                pos = lineEnd;
                continue;
            }

            FlushPlainBatch();

            string contentText = contentByteLen > 0
                ? Encoding.UTF8.GetString(buf, pos, contentByteLen)
                : string.Empty;
            string trimmed = contentText.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                int fenceStyle = insideFence ? StyleCodeFenceClose : StyleCodeFenceOpen;
                DirectMessage(SCI_SETSTYLING, (IntPtr)lineByteLen, (IntPtr)fenceStyle);
                insideFence = !insideFence;
                pos = lineEnd;
                continue;
            }

            if (insideFence)
            {
                if (contentText.Length == 0)
                {
                    DirectMessage(SCI_SETSTYLING, (IntPtr)lineByteLen, (IntPtr)StyleCodeBlock);
                }
                else
                {
                    int[] charStyles = new int[contentText.Length];
                    for (int k = 0; k < charStyles.Length; k++) charStyles[k] = StyleCodeBlock;
                    ApplyRegexStyle(charStyles, contentText, CodeKeywordRegex, StyleCodeKeyword);
                    ApplyRegexStyle(charStyles, contentText, CodeStringRegex, StyleCodeString);
                    ApplyRegexStyle(charStyles, contentText, CodeCommentRegex, StyleCodeComment);
                    EmitStyledCharsAsUtf8Bytes(contentText, charStyles, lineByteLen - contentByteLen, StyleCodeBlock);
                }
                pos = lineEnd;
                continue;
            }

            if (IsHeadingLine(trimmed, out _))
            {
                DirectMessage(SCI_SETSTYLING, (IntPtr)contentByteLen, (IntPtr)StyleHeading);
                if (lineByteLen > contentByteLen)
                    DirectMessage(SCI_SETSTYLING, (IntPtr)(lineByteLen - contentByteLen), (IntPtr)StyleDefault);
                pos = lineEnd;
                continue;
            }

            bool hasInlineMarker = contentText.IndexOfAny(new[] { '*', '`', '~', '[' }) >= 0;
            bool isQuote = trimmed.StartsWith(">", StringComparison.Ordinal);
            bool hasTask = contentByteLen > 4 && TaskPrefixRegex.IsMatch(contentText);

            if (!hasInlineMarker && !isQuote && !hasTask)
            {
                plainBatchBytes += lineByteLen;
                pos = lineEnd;
                continue;
            }

            int[] mdStyles = new int[contentText.Length];
            if (isQuote)
                for (int k = 0; k < mdStyles.Length; k++) mdStyles[k] = StyleQuote;

            if (hasTask)
            {
                var tm = TaskPrefixRegex.Match(contentText);
                for (int k = 0; k < tm.Length && k < mdStyles.Length; k++) mdStyles[k] = StyleTask;
            }

            if (contentText.IndexOf("**", StringComparison.Ordinal) >= 0)
                ApplyRegexStyle(mdStyles, contentText, BoldRegex, StyleBold);
            if (contentText.IndexOf('*') >= 0)
                ApplyRegexStyle(mdStyles, contentText, ItalicRegex, StyleItalic);
            if (contentText.IndexOf("~~", StringComparison.Ordinal) >= 0)
                ApplyRegexStyle(mdStyles, contentText, StrikeRegex, StyleStrike);
            if (contentText.IndexOf('[') >= 0)
                ApplyRegexStyle(mdStyles, contentText, LinkRegex, StyleLink);
            if (contentText.IndexOf('`') >= 0)
                ApplyRegexStyle(mdStyles, contentText, InlineCodeRegex, StyleInlineCode);

            EmitStyledCharsAsUtf8Bytes(contentText, mdStyles, lineByteLen - contentByteLen, StyleDefault);
            pos = lineEnd;
        }

        FlushPlainBatch();
    }

    private void EmitStyledCharsAsUtf8Bytes(string contentText, int[] charStyles, int eolBytes, int eolStyle)
    {
        char[] contentChars = contentText.ToCharArray();
        int runStart = 0;
        while (runStart < charStyles.Length)
        {
            int st = charStyles[runStart];
            int runEnd = runStart + 1;
            while (runEnd < charStyles.Length && charStyles[runEnd] == st) runEnd++;
            int byteLen = Encoding.UTF8.GetByteCount(contentChars, runStart, runEnd - runStart);
            if (byteLen > 0)
                DirectMessage(SCI_SETSTYLING, (IntPtr)byteLen, (IntPtr)st);
            runStart = runEnd;
        }
        if (eolBytes > 0)
            DirectMessage(SCI_SETSTYLING, (IntPtr)eolBytes, (IntPtr)eolStyle);
    }

    private static void ApplyRegexStyle(int[] styles, string text, Regex rx, int styleId)
    {
        foreach (Match m in rx.Matches(text))
        {
            int end = Math.Min(styles.Length, m.Index + m.Length);
            for (int k = m.Index; k < end; k++) styles[k] = styleId;
        }
    }

    private static bool IsHeadingLine(string trimmed, out int level)
    {
        level = 0;
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '#') return false;
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        return level >= 1 && level <= 6 && (level == trimmed.Length || trimmed[level] == ' ');
    }

    public void RebuildFoldLevelsFromText(string fullText)
    {
        if (string.IsNullOrEmpty(fullText)) return;
        var lines = fullText.Replace("\r\n", "\n").Split('\n');
        int maxLines = Math.Min(lines.Length, Lines.Count);
        bool insideFence = false;

        for (int i = 0; i < maxLines; i++)
        {
            var text = lines[i];
            var trimmed = text.TrimStart();
            int level;
            int flags = 0;

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                level = FoldLevelBase + 20;
                if (!insideFence) { flags = FoldHeaderFlag; insideFence = true; }
                else { insideFence = false; }
            }
            else if (insideFence)
            {
                level = FoldLevelBase + 21;
            }
            else if (IsHeadingLine(trimmed, out int hLvl))
            {
                level = FoldLevelBase + hLvl;
                flags = FoldHeaderFlag;
            }
            else
            {
                int indent = text.Length - trimmed.Length;
                bool isList = trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                              trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                              trimmed.StartsWith("+ ", StringComparison.Ordinal) ||
                              (trimmed.Length >= 3 && char.IsDigit(trimmed[0]) && Regex.IsMatch(trimmed, @"^\d+\.\s"));

                level = FoldLevelBase + 7 + Math.Min(10, indent / 2);
                if (isList && i + 1 < maxLines)
                {
                    var nextRaw = lines[i + 1];
                    var nextTrim = nextRaw.TrimStart();
                    if (!string.IsNullOrWhiteSpace(nextTrim) && !IsHeadingLine(nextTrim, out _) &&
                        (nextRaw.Length - nextTrim.Length) > indent)
                    {
                        flags = FoldHeaderFlag;
                    }
                }
            }

            DirectMessage(SCI_SETFOLDLEVEL, (IntPtr)i, (IntPtr)(level | flags));
        }
    }

    public static ObservableCollection<HeadingNodeVM> ExtractHeadingTreeFromText(string fullText)
    {
        var roots = new ObservableCollection<HeadingNodeVM>();
        if (string.IsNullOrEmpty(fullText)) return roots;

        var stack = new Stack<HeadingNodeVM>();
        bool insideFence = false;
        int lineNum = 0;
        int pos = 0;

        while (pos <= fullText.Length)
        {
            int nl = fullText.IndexOf('\n', pos);
            int end = nl >= 0 ? nl : fullText.Length;
            lineNum++;

            int len = end - pos;
            if (len > 0 && fullText[pos + len - 1] == '\r') len--;

            if (len > 0)
            {
                int firstNonSpace = pos;
                int limit = pos + len;
                while (firstNonSpace < limit && char.IsWhiteSpace(fullText[firstNonSpace]))
                    firstNonSpace++;

                int trimLen = limit - firstNonSpace;
                if (trimLen >= 3 && fullText[firstNonSpace] == '`' &&
                    fullText[firstNonSpace + 1] == '`' && fullText[firstNonSpace + 2] == '`')
                {
                    insideFence = !insideFence;
                }
                else if (!insideFence && trimLen >= 2 && fullText[firstNonSpace] == '#')
                {
                    int lvl = 0;
                    while (lvl < trimLen && lvl < 7 && fullText[firstNonSpace + lvl] == '#') lvl++;
                    if (lvl >= 1 && lvl <= 6 && lvl < trimLen && fullText[firstNonSpace + lvl] == ' ')
                    {
                        string title = fullText.Substring(firstNonSpace + lvl + 1, trimLen - lvl - 1).Trim();
                        var node = new HeadingNodeVM { Level = lvl, Text = title, LineNumber = lineNum };
                        while (stack.Count > 0 && stack.Peek().Level >= lvl) stack.Pop();
                        if (stack.Count == 0) roots.Add(node);
                        else stack.Peek().Children.Add(node);
                        stack.Push(node);
                    }
                }
            }

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

        IndicatorCurrent = IndicatorAllMatch;
        SearchFlags = SearchFlags.None;
        TargetStart = 0;
        TargetEnd = TextLength;
        int count = 0;
        while (count < 120 && SearchInTarget(keyword) >= 0)
        {
            int foundStart = TargetStart;
            int foundEnd = TargetEnd;
            if (foundEnd <= foundStart) break;
            IndicatorFillRange(foundStart, foundEnd - foundStart);
            TargetStart = foundEnd;
            TargetEnd = TextLength;
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

public sealed class PreviewBlockVM : INotifyPropertyChanged
{
    private bool _isCollapsed;
    private bool _isSearchActive;

    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int HeadingLevel { get; set; }
    public WpfBlock AstBlock { get; set; } = null!;

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

    public void ParseDocument(string markdown)
    {
        AllBlocks.Clear();
        VisibleBlocks.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var doc = Markdown.Parse(markdown, Pipeline);
        foreach (var block in doc)
        {
            var vm = new PreviewBlockVM
            {
                AstBlock = block,
                StartLine = block.Line + 1,
                EndLine = Math.Max(block.Line + 1, EstimateEndLine(block)),
                HeadingLevel = block is HeadingBlock hb ? hb.Level : 0
            };
            AllBlocks.Add(vm);
            VisibleBlocks.Add(vm);
        }
    }

    private static int EstimateEndLine(WpfBlock block)
    {
        int max = block.Line + 1;
        if (block is ContainerBlock cb)
        {
            foreach (var child in cb)
                max = Math.Max(max, EstimateEndLine(child));
        }
        return max;
    }

    public void RebuildVisibleBlocks()
    {
        VisibleBlocks.Clear();
        int hiddenUnderLevel = 0;

        foreach (var blk in AllBlocks)
        {
            if (blk.HeadingLevel > 0)
            {
                if (hiddenUnderLevel > 0 && blk.HeadingLevel > hiddenUnderLevel)
                    continue;
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
        PreviewBlockVM? lastHeading = null;
        var activeHeadings = new Stack<PreviewBlockVM>();
        PreviewBlockVM? found = null;

        foreach (var blk in AllBlocks)
        {
            if (blk.HeadingLevel > 0)
            {
                while (activeHeadings.Count > 0 && activeHeadings.Peek().HeadingLevel >= blk.HeadingLevel)
                    activeHeadings.Pop();
                activeHeadings.Push(blk);
                lastHeading = blk;
            }

            if (blk.StartLine <= targetLine && targetLine <= blk.EndLine)
            {
                found = blk;
                break;
            }
            if (blk.StartLine > targetLine)
            {
                found = lastHeading ?? blk;
                break;
            }
        }

        if (found != null)
        {
            bool changed = false;
            foreach (var h in activeHeadings)
            {
                if (h != found && h.IsCollapsed) { h.IsCollapsed = false; changed = true; }
            }
            if (changed) RebuildVisibleBlocks();
        }
        return found;
    }

    public UIElement BuildBlockElement(PreviewBlockVM vm, double fontSize, string? highlightKw)
    {
        var el = RenderAstBlock(vm.AstBlock, vm, fontSize, highlightKw) ?? new TextBlock();
        if (vm.IsSearchActive)
        {
            return new Border
            {
                BorderBrush = ThemeHelper.GetBrush("AccentBrush"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 2, 4, 2),
                Background = ThemeHelper.GetBrush("ActiveBg"),
                CornerRadius = new CornerRadius(3),
                Child = el
            };
        }
        return el;
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
                            int ln = subPb.Line + 1;
                            chk.Click += (_, _) => TaskCheckboxToggled?.Invoke(ln, chk.IsChecked == true);
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
                            var ui = RenderAstBlock(sub, null, fontSize, kw);
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
                return new Border
                {
                    BorderBrush = ThemeHelper.GetBrush("BorderBrush"),
                    BorderThickness = new Thickness(1),
                    Child = grid
                };
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
        if (string.IsNullOrEmpty(kw))
        {
            target.Add(new Run(text));
            return;
        }
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
            return new TextBlock
            {
                Text = $"[图片加载失败: {ex.Message}]",
                Foreground = ThemeHelper.GetBrush("DangerBrush")
            };
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

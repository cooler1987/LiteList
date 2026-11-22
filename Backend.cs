using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace LiteList;

public enum SaveMode
{
    Manual = 0,       // 默认：手动安全模式（Ctrl+S 保存，切换/退出提示）
    IdleAutoSave = 1  // 空闲自动保存（停顿 3 秒后静默写入）
}

public enum BlurAction
{
    None = 0,     // 离开焦点不操作
    Ask = 1,      // 离开焦点弹窗询问是否保存
    AutoSave = 2  // 离开焦点自动静默保存
}

public enum EditorViewMode
{
    Edit = 0,
    Preview = 1
}

public sealed class AppSettings
{
    public SaveMode SaveMode { get; set; } = SaveMode.Manual;
    public BlurAction BlurAction { get; set; } = BlurAction.None;
    public double FontSize { get; set; } = 15.0;
    public bool ShowLeftTree { get; set; } = true;
    public bool ShowRightToc { get; set; } = false; // 默认隐藏右侧正文大纲
    public bool AutoVacuumOnExit { get; set; } = true;
}

public sealed class NodeRecord
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public string Title { get; set; } = "";
    public double SortOrder { get; set; }
    public bool IsExpanded { get; set; } = true;
    public string UpdatedAt { get; set; } = "";
}

public sealed class NodeDetail
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class SearchHit
{
    public long NodeId { get; set; }
    public string NodeTitle { get; set; } = "";
    public string Breadcrumb { get; set; } = "";
    public int LineNumber { get; set; }          // 1-based（0 表示命中节点标题）
    public int CharOffsetInLine { get; set; }
    public int MatchLength { get; set; }
    public string Snippet { get; set; } = "";
}

public sealed class AttachmentResult
{
    public string RelPath { get; set; } = "";
    public string Filename { get; set; } = "";
    public bool IsImage { get; set; }
}

public sealed class HeadingNodeVM : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    public int Level { get; set; }
    public string Text { get; set; } = "";
    public int LineNumber { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public ObservableCollection<HeadingNodeVM> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TreeNodeVM : INotifyPropertyChanged
{
    private string _title = "";
    private bool _isExpanded = true;
    private bool _isSelected;
    private bool _isRenaming;

    public long Id { get; set; }
    public long ParentId { get; set; }
    public double SortOrder { get; set; }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming != value) { _isRenaming = value; OnPropertyChanged(); } }
    }

    public ObservableCollection<TreeNodeVM> Children { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ThemeHelper
{
    public static bool IsDarkMode { get; private set; }

    public static void ApplySystemTheme(ResourceDictionary resources)
    {
        IsDarkMode = DetectWindowsDarkMode();
        if (IsDarkMode)
        {
            SetBrush(resources, "AppBg", "#18181B");
            SetBrush(resources, "SidebarBg", "#202024");
            SetBrush(resources, "SurfaceBg", "#27272C");
            SetBrush(resources, "HoverBg", "#2F2F37");
            SetBrush(resources, "ActiveBg", "#1E3A5F");
            SetBrush(resources, "BorderBrush", "#34343D");
            SetBrush(resources, "TextFg", "#E4E4E7");
            SetBrush(resources, "MutedFg", "#A1A1AA");
            SetBrush(resources, "AccentBrush", "#60A5FA");
            SetBrush(resources, "DangerBrush", "#F87171");
            SetBrush(resources, "CodeBg", "#232329");
            SetBrush(resources, "CaretBrush", "#FFFFFF");
            SetBrush(resources, "SearchMatchBg", "#66EAB308");
            SetBrush(resources, "HeadingColor", "#60A5FA");
            SetBrush(resources, "StrongColor", "#FAFAFA");
            SetBrush(resources, "ItalicColor", "#C084FC");
            SetBrush(resources, "InlineCodeColor", "#F472B6");
        }
        else
        {
            SetBrush(resources, "AppBg", "#FFFFFF");
            SetBrush(resources, "SidebarBg", "#F6F6F7");
            SetBrush(resources, "SurfaceBg", "#FFFFFF");
            SetBrush(resources, "HoverBg", "#EAEAEF");
            SetBrush(resources, "ActiveBg", "#DCE8FF");
            SetBrush(resources, "BorderBrush", "#E2E4E9");
            SetBrush(resources, "TextFg", "#1F2937");
            SetBrush(resources, "MutedFg", "#6B7280");
            SetBrush(resources, "AccentBrush", "#2563EB");
            SetBrush(resources, "DangerBrush", "#DC2626");
            SetBrush(resources, "CodeBg", "#F3F4F6");
            SetBrush(resources, "CaretBrush", "#111827");
            SetBrush(resources, "SearchMatchBg", "#80FACC15");
            SetBrush(resources, "HeadingColor", "#1D4ED8");
            SetBrush(resources, "StrongColor", "#111827");
            SetBrush(resources, "ItalicColor", "#7C3AED");
            SetBrush(resources, "InlineCodeColor", "#BE185D");
        }
    }

    private static bool DetectWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val) return val == 0;
        }
        catch { }
        return false;
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        res[key] = brush;
    }

    public static Brush GetBrush(string key) =>
        Application.Current?.Resources[key] as Brush ?? Brushes.Gray;

    public static System.Drawing.Color ToWinFormsColor(string hex) =>
        System.Drawing.ColorTranslator.FromHtml(hex);
}

/// <summary>
/// 图片与附件统一复制到软件所在目录下的 files/ 文件夹
/// </summary>
public sealed class FileArchiveService
{
    public const string FilesFolderName = "files";

    private static readonly Regex MdLinkRegex = new(
        @"(!?\[[^\]]*\])\(([^)\r\n]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex HtmlImgRegex = new(
        @"<img[^>]+src=[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico"
    };

    public string AppBaseDir { get; }
    public string? CurrentDbDir { get; set; }

    public FileArchiveService()
    {
        AppBaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsImageExtension(string ext) => ImageExts.Contains(ext);

    public string GetTargetFilesDir()
    {
        var primary = Path.Combine(AppBaseDir, FilesFolderName);
        if (IsDirWritable(AppBaseDir)) return primary;
        if (!string.IsNullOrEmpty(CurrentDbDir))
            return Path.Combine(CurrentDbDir, FilesFolderName);
        return primary;
    }

    private static bool IsDirWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".ll_probe_" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public string? ResolveExistingFile(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        var decoded = Uri.UnescapeDataString(rawUrl.Trim().Trim('<', '>'));
        var clean = decoded.Replace('\\', '/').TrimStart('/');
        if (clean.StartsWith("./", StringComparison.Ordinal))
            clean = clean.Substring(2);
        if (clean.StartsWith(FilesFolderName + "/", StringComparison.OrdinalIgnoreCase))
            clean = clean.Substring(FilesFolderName.Length + 1);
        if (string.IsNullOrWhiteSpace(clean) || clean.Contains("..")) return null;

        var nativeRel = clean.Replace('/', Path.DirectorySeparatorChar);
        var p1 = Path.Combine(GetTargetFilesDir(), nativeRel);
        if (File.Exists(p1)) return p1;

        if (!string.IsNullOrEmpty(CurrentDbDir))
        {
            var p2 = Path.Combine(CurrentDbDir, FilesFolderName, nativeRel);
            if (File.Exists(p2)) return p2;
        }
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "file";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(baseName.Select(c =>
            invalid.Contains(c) || c == ' ' || c == '(' || c == ')' || c == '[' || c == ']' ? '_' : c).ToArray());
    }

    private static string MakeUniqueStoredName(string safeName) =>
        $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 4)}_{safeName}";

    public AttachmentResult SaveClipboardImage(BitmapSource image)
    {
        var dir = GetTargetFilesDir();
        Directory.CreateDirectory(dir);
        var storedName = MakeUniqueStoredName("screenshot.png");
        var destPath = Path.Combine(dir, storedName);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(destPath);
        encoder.Save(fs);

        return new AttachmentResult
        {
            RelPath = $"{FilesFolderName}/{storedName}",
            Filename = "screenshot.png",
            IsImage = true
        };
    }

    public AttachmentResult ImportExternalFile(string rawPath)
    {
        var cleanPath = rawPath.Trim();
        if (cleanPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(cleanPath, UriKind.Absolute, out var uri))
        {
            cleanPath = uri.LocalPath;
        }

        var fullSrc = Path.GetFullPath(cleanPath);
        if (!File.Exists(fullSrc))
            throw new FileNotFoundException("找不到本地文件", fullSrc);

        var filesDir = Path.GetFullPath(GetTargetFilesDir());
        Directory.CreateDirectory(filesDir);
        var filesDirWithSep = filesDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (fullSrc.StartsWith(filesDirWithSep, StringComparison.OrdinalIgnoreCase))
        {
            var rel = fullSrc.Substring(filesDirWithSep.Length).Replace('\\', '/');
            return new AttachmentResult
            {
                RelPath = $"{FilesFolderName}/{rel}",
                Filename = Path.GetFileName(fullSrc),
                IsImage = IsImageExtension(Path.GetExtension(fullSrc))
            };
        }

        var safeName = SanitizeFileName(Path.GetFileName(fullSrc));
        var storedName = MakeUniqueStoredName(safeName);
        File.Copy(fullSrc, Path.Combine(filesDir, storedName), overwrite: true);

        return new AttachmentResult
        {
            RelPath = $"{FilesFolderName}/{storedName}",
            Filename = safeName,
            IsImage = IsImageExtension(Path.GetExtension(safeName))
        };
    }

    public (string Content, int Count) ResolveLocalReferencesInFragment(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return (fragment, 0);
        int count = 0;
        var rewritten = MdLinkRegex.Replace(fragment, m =>
        {
            var label = m.Groups[1].Value;
            var rawTarget = m.Groups[2].Value.Trim();
            var pathOnly = rawTarget;
            var suffix = "";

            if (rawTarget.StartsWith("<"))
            {
                int endAngle = rawTarget.IndexOf('>');
                if (endAngle > 1)
                {
                    pathOnly = rawTarget.Substring(1, endAngle - 1);
                    suffix = rawTarget.Substring(endAngle + 1);
                }
            }
            else
            {
                int quoteIdx = rawTarget.IndexOf(" \"", StringComparison.Ordinal);
                if (quoteIdx > 0)
                {
                    pathOnly = rawTarget.Substring(0, quoteIdx);
                    suffix = rawTarget.Substring(quoteIdx);
                }
            }

            if (string.IsNullOrWhiteSpace(pathOnly) ||
                pathOnly.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith("#", StringComparison.Ordinal) ||
                pathOnly.StartsWith("files/", StringComparison.OrdinalIgnoreCase) ||
                pathOnly.StartsWith("/files/", StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            try
            {
                var res = ImportExternalFile(Uri.UnescapeDataString(pathOnly));
                count++;
                return $"{label}({res.RelPath}{suffix})";
            }
            catch { return m.Value; }
        });
        return (rewritten, count);
    }

    public List<string> ExtractReferencedRelPaths(string markdownContent)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(markdownContent)) return list;
        foreach (Match m in MdLinkRegex.Matches(markdownContent))
        {
            var target = Uri.UnescapeDataString(m.Groups[2].Value.Trim());
            var sq = target.IndexOf(" \"", StringComparison.Ordinal);
            if (sq > 0) target = target.Substring(0, sq);
            target = target.Trim('<', '>').Replace('\\', '/').TrimStart('/');
            if (target.StartsWith(FilesFolderName + "/", StringComparison.OrdinalIgnoreCase))
                list.Add(target);
        }
        foreach (Match m in HtmlImgRegex.Matches(markdownContent))
        {
            var target = Uri.UnescapeDataString(m.Groups[1].Value.Trim()).Replace('\\', '/').TrimStart('/');
            if (target.StartsWith(FilesFolderName + "/", StringComparison.OrdinalIgnoreCase))
                list.Add(target);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class DatabaseService : IDisposable
{
    private SqliteConnection? _conn;
    public string CurrentDbPath { get; private set; } = "";
    public bool HasModificationsSinceOpen { get; private set; }

    public string ResolveDefaultDbPath(string appBaseDir)
    {
        try
        {
            var probe = Path.Combine(appBaseDir, ".ll_db_" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return Path.Combine(appBaseDir, "notes.llist");
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "notes.llist");
        }
    }

    public void OpenOrCreate(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var isNew = !File.Exists(fullPath) || new FileInfo(fullPath).Length == 0;
        var connStr = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        var newConn = new SqliteConnection(connStr);
        newConn.Open();

        try { InitAndMigrate(newConn, isNew); }
        catch { newConn.Dispose(); throw; }

        _conn?.Dispose();
        _conn = newConn;
        CurrentDbPath = fullPath;
        HasModificationsSinceOpen = false;
    }

    private static void InitAndMigrate(SqliteConnection conn, bool isNew)
    {
        ExecNonQuery(conn, "PRAGMA journal_mode = DELETE;");
        ExecNonQuery(conn, "PRAGMA synchronous = NORMAL;");
        ExecNonQuery(conn, "PRAGMA foreign_keys = ON;");
        ExecNonQuery(conn, "PRAGMA busy_timeout = 5000;");

        long version;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        if (version == 0)
        {
            if (!isNew)
            {
                using var check = conn.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='nodes';";
                var hasNodes = Convert.ToInt64(check.ExecuteScalar() ?? 0L);
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != 'sqlite_sequence';";
                var tableCount = Convert.ToInt64(check.ExecuteScalar() ?? 0L);
                if (tableCount > 0 && hasNodes == 0)
                    throw new InvalidDataException("所选文件不是有效的 LiteList (.llist) 笔记库。");
            }

            ExecNonQuery(conn, @"
                PRAGMA auto_vacuum = INCREMENTAL;
                CREATE TABLE IF NOT EXISTS nodes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    parent_id INTEGER DEFAULT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                    title TEXT NOT NULL,
                    content TEXT DEFAULT '',
                    sort_order REAL DEFAULT 1000.0,
                    is_expanded INTEGER DEFAULT 1,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS idx_parent ON nodes(parent_id, sort_order);
                CREATE TABLE IF NOT EXISTS app_settings (k TEXT PRIMARY KEY, v TEXT NOT NULL);
                PRAGMA user_version = 2;");

            if (isNew)
            {
                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (NULL, $t, $c, 1000.0, 1);";
                ins.Parameters.AddWithValue("$t", "我的第一篇笔记");
                ins.Parameters.AddWithValue("$c", "# 我的第一篇笔记\n\n点击「编辑」或按 Ctrl+E 开始编写你的 Markdown 内容。\n\n- 拖入或粘贴图片/附件将自动存入程序目录下的 files/ 文件夹\n- [ ] 待办事项示例");
                ins.ExecuteNonQuery();
            }
        }
        else if (version == 1)
        {
            ExecNonQuery(conn, "CREATE TABLE IF NOT EXISTS app_settings (k TEXT PRIMARY KEY, v TEXT NOT NULL); PRAGMA user_version = 2;");
        }
    }

    private static void ExecNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection RequireConn() => _conn ?? throw new InvalidOperationException("未打开笔记库");

    public AppSettings LoadSettings()
    {
        var s = new AppSettings();
        if (_conn == null) return s;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT k, v FROM app_settings;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var k = r.GetString(0);
            var v = r.GetString(1);
            switch (k)
            {
                case "save_mode" when int.TryParse(v, out var sm): s.SaveMode = (SaveMode)sm; break;
                case "blur_action" when int.TryParse(v, out var ba): s.BlurAction = (BlurAction)ba; break;
                case "font_size" when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs): s.FontSize = Math.Max(12.0, Math.Min(28.0, fs)); break;
                case "show_left_tree": s.ShowLeftTree = v != "0"; break;
                case "show_right_toc": s.ShowRightToc = v != "0"; break;
                case "auto_vacuum_exit": s.AutoVacuumOnExit = v != "0"; break;
            }
        }
        return s;
    }

    public void SaveSettings(AppSettings s)
    {
        var conn = RequireConn();
        var pairs = new (string K, string V)[]
        {
            ("save_mode", ((int)s.SaveMode).ToString()),
            ("blur_action", ((int)s.BlurAction).ToString()),
            ("font_size", s.FontSize.ToString(CultureInfo.InvariantCulture)),
            ("show_left_tree", s.ShowLeftTree ? "1" : "0"),
            ("show_right_toc", s.ShowRightToc ? "1" : "0"),
            ("auto_vacuum_exit", s.AutoVacuumOnExit ? "1" : "0")
        };
        using var tx = conn.BeginTransaction();
        foreach (var (k, v) in pairs)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO app_settings (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = excluded.v;";
            cmd.Parameters.AddWithValue("$k", k);
            cmd.Parameters.AddWithValue("$v", v);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<NodeRecord> GetTree()
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at FROM nodes ORDER BY sort_order ASC, id ASC;";
        var list = new List<NodeRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NodeRecord
            {
                Id = r.GetInt64(0),
                ParentId = r.GetInt64(1),
                Title = r.GetString(2),
                SortOrder = r.GetDouble(3),
                IsExpanded = r.GetInt64(4) != 0,
                UpdatedAt = r.GetString(5)
            });
        }
        return list;
    }

    public NodeDetail GetNodeContent(long id)
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, content, updated_at FROM nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"节点 {id} 不存在");
        return new NodeDetail { Id = r.GetInt64(0), Title = r.GetString(1), Content = r.GetString(2), UpdatedAt = r.GetString(3) };
    }

    public NodeRecord CreateNode(long parentId, string title, double? exactSortOrder = null)
    {
        var conn = RequireConn();
        if (string.IsNullOrWhiteSpace(title)) title = "未命名节点";

        double order;
        if (exactSortOrder.HasValue) order = exactSortOrder.Value;
        else
        {
            using var maxCmd = conn.CreateCommand();
            maxCmd.CommandText = parentId <= 0
                ? "SELECT MAX(sort_order) FROM nodes WHERE parent_id IS NULL;"
                : "SELECT MAX(sort_order) FROM nodes WHERE parent_id = $pid;";
            if (parentId > 0) maxCmd.Parameters.AddWithValue("$pid", parentId);
            order = maxCmd.ExecuteScalar() is double d ? d + 1000.0 : 1000.0;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES ($pid, $title, '', $order, 1); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$pid", parentId <= 0 ? DBNull.Value : (object)parentId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$order", order);
        var newId = Convert.ToInt64(cmd.ExecuteScalar()!);

        if (parentId > 0) SetNodeExpanded(parentId, true);
        MaybeRebalance(parentId);
        HasModificationsSinceOpen = true;
        return GetTreeNodeById(newId);
    }

    private NodeRecord GetTreeNodeById(long id)
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at FROM nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new NodeRecord { Id = r.GetInt64(0), ParentId = r.GetInt64(1), Title = r.GetString(2), SortOrder = r.GetDouble(3), IsExpanded = r.GetInt64(4) != 0, UpdatedAt = r.GetString(5) };
    }

    public void RenameNode(long id, string title)
    {
        var conn = RequireConn();
        if (string.IsNullOrWhiteSpace(title)) title = "未命名节点";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET title = $t, updated_at = CURRENT_TIMESTAMP WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        HasModificationsSinceOpen = true;
    }

    public void SaveNode(long id, string title, string content)
    {
        var conn = RequireConn();
        if (string.IsNullOrWhiteSpace(title)) title = "未命名节点";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET title = $t, content = $c, updated_at = CURRENT_TIMESTAMP WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        HasModificationsSinceOpen = true;
    }

    public void SetNodeExpanded(long id, bool expanded)
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET is_expanded = $e WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", expanded ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetAllExpanded(bool expanded)
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET is_expanded = $e;";
        cmd.Parameters.AddWithValue("$e", expanded ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void MoveNode(long id, long newParentId, double newSortOrder)
    {
        var conn = RequireConn();
        if (id == newParentId) throw new InvalidOperationException("不能将节点移动到自身内部");
        if (newParentId > 0)
        {
            using var chk = conn.CreateCommand();
            chk.CommandText = "WITH RECURSIVE sub(nid) AS (SELECT id FROM nodes WHERE id = $id UNION ALL SELECT n.id FROM nodes n JOIN sub s ON n.parent_id = s.nid) SELECT COUNT(*) FROM sub WHERE nid = $np;";
            chk.Parameters.AddWithValue("$id", id);
            chk.Parameters.AddWithValue("$np", newParentId);
            if (Convert.ToInt64(chk.ExecuteScalar() ?? 0L) > 0)
                throw new InvalidOperationException("不能将父节点移动到其子节点内部");
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET parent_id = $np, sort_order = $so, updated_at = CURRENT_TIMESTAMP WHERE id = $id;";
        cmd.Parameters.AddWithValue("$np", newParentId <= 0 ? DBNull.Value : (object)newParentId);
        cmd.Parameters.AddWithValue("$so", newSortOrder);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();

        if (newParentId > 0) SetNodeExpanded(newParentId, true);
        MaybeRebalance(newParentId);
        HasModificationsSinceOpen = true;
    }

    public NodeRecord DuplicateNode(long id, bool withChildren)
    {
        var conn = RequireConn();
        var src = GetTreeNodeById(id);

        using var nextCmd = conn.CreateCommand();
        nextCmd.CommandText = src.ParentId <= 0
            ? "SELECT MIN(sort_order) FROM nodes WHERE parent_id IS NULL AND sort_order > $so;"
            : "SELECT MIN(sort_order) FROM nodes WHERE parent_id = $pid AND sort_order > $so;";
        if (src.ParentId > 0) nextCmd.Parameters.AddWithValue("$pid", src.ParentId);
        nextCmd.Parameters.AddWithValue("$so", src.SortOrder);
        var newOrder = nextCmd.ExecuteScalar() is double d ? (src.SortOrder + d) / 2.0 : src.SortOrder + 1000.0;

        using var tx = conn.BeginTransaction();
        long CloneBranch(long oldId, long targetPid, double order, bool rename)
        {
            string t, c; long exp;
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT title, content, is_expanded FROM nodes WHERE id = $id;";
                q.Parameters.AddWithValue("$id", oldId);
                using var r = q.ExecuteReader();
                r.Read(); t = r.GetString(0); c = r.GetString(1); exp = r.GetInt64(2);
            }
            if (rename) t += " (副本)";
            long createdId;
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES ($pid, $t, $c, $so, $exp); SELECT last_insert_rowid();";
                ins.Parameters.AddWithValue("$pid", targetPid <= 0 ? DBNull.Value : (object)targetPid);
                ins.Parameters.AddWithValue("$t", t);
                ins.Parameters.AddWithValue("$c", c);
                ins.Parameters.AddWithValue("$so", order);
                ins.Parameters.AddWithValue("$exp", exp);
                createdId = Convert.ToInt64(ins.ExecuteScalar()!);
            }
            if (withChildren)
            {
                var kids = new List<(long Id, double Order)>();
                using (var kq = conn.CreateCommand())
                {
                    kq.Transaction = tx;
                    kq.CommandText = "SELECT id, sort_order FROM nodes WHERE parent_id = $id ORDER BY sort_order ASC, id ASC;";
                    kq.Parameters.AddWithValue("$id", oldId);
                    using var kr = kq.ExecuteReader();
                    while (kr.Read()) kids.Add((kr.GetInt64(0), kr.GetDouble(1)));
                }
                foreach (var k in kids) CloneBranch(k.Id, createdId, k.Order, false);
            }
            return createdId;
        }

        var newRootId = CloneBranch(id, src.ParentId, newOrder, true);
        tx.Commit();
        MaybeRebalance(src.ParentId);
        HasModificationsSinceOpen = true;
        return GetTreeNodeById(newRootId);
    }

    public void SortChildren(long parentId)
    {
        var conn = RequireConn();
        var ids = new List<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = parentId <= 0
                ? "SELECT id FROM nodes WHERE parent_id IS NULL ORDER BY title COLLATE NOCASE ASC, id ASC;"
                : "SELECT id FROM nodes WHERE parent_id = $pid ORDER BY title COLLATE NOCASE ASC, id ASC;";
            if (parentId > 0) cmd.Parameters.AddWithValue("$pid", parentId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < ids.Count; i++)
        {
            using var u = conn.CreateCommand();
            u.Transaction = tx;
            u.CommandText = "UPDATE nodes SET sort_order = $so WHERE id = $id;";
            u.Parameters.AddWithValue("$so", (i + 1) * 1000.0);
            u.Parameters.AddWithValue("$id", ids[i]);
            u.ExecuteNonQuery();
        }
        tx.Commit();
        HasModificationsSinceOpen = true;
    }

    public void DeleteNode(long id)
    {
        var conn = RequireConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        HasModificationsSinceOpen = true;
    }

    private void MaybeRebalance(long parentId)
    {
        var conn = RequireConn();
        var items = new List<(long Id, double Order)>();
        bool need = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = parentId <= 0
                ? "SELECT id, sort_order FROM nodes WHERE parent_id IS NULL ORDER BY sort_order ASC, id ASC;"
                : "SELECT id, sort_order FROM nodes WHERE parent_id = $pid ORDER BY sort_order ASC, id ASC;";
            if (parentId > 0) cmd.Parameters.AddWithValue("$pid", parentId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var it = (r.GetInt64(0), r.GetDouble(1));
                if (items.Count > 0 && (it.Item2 - items[items.Count - 1].Order) < 0.001) need = true;
                items.Add(it);
            }
        }
        if (!need) return;
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < items.Count; i++)
        {
            using var u = conn.CreateCommand();
            u.Transaction = tx;
            u.CommandText = "UPDATE nodes SET sort_order = $so WHERE id = $id;";
            u.Parameters.AddWithValue("$so", (i + 1) * 1000.0);
            u.Parameters.AddWithValue("$id", items[i].Id);
            u.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<SearchHit> SearchOccurrences(string keyword, int maxHits = 500)
    {
        var conn = RequireConn();
        var kw = keyword.Trim();
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(kw)) return hits;

        var map = GetTree().ToDictionary(x => x.Id);
        string BuildPath(long id)
        {
            var parts = new List<string>();
            if (!map.TryGetValue(id, out var cur)) return "";
            int guard = 0;
            while (cur != null && cur.ParentId > 0 && guard++ < 50 && map.TryGetValue(cur.ParentId, out var p))
            {
                parts.Insert(0, p.Title);
                cur = p;
            }
            return string.Join(" / ", parts);
        }

        var escaped = kw.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, content FROM nodes WHERE title LIKE $pat ESCAPE '\\' OR content LIKE $pat ESCAPE '\\' ORDER BY sort_order ASC, id ASC;";
        cmd.Parameters.AddWithValue("$pat", $"%{escaped}%");

        using var r = cmd.ExecuteReader();
        while (r.Read() && hits.Count < maxHits)
        {
            var id = r.GetInt64(0);
            var title = r.GetString(1);
            var content = r.GetString(2);
            var path = BuildPath(id);

            if (title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hits.Add(new SearchHit { NodeId = id, NodeTitle = title, Breadcrumb = path, LineNumber = 0, Snippet = "[命中标题] " + title });
            }
            if (!string.IsNullOrEmpty(content))
            {
                var lines = content.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length && hits.Count < maxHits; i++)
                {
                    var line = lines[i];
                    var idx = line.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                    while (idx >= 0 && hits.Count < maxHits)
                    {
                        var start = Math.Max(0, idx - 22);
                        var len = Math.Min(line.Length - start, kw.Length + 60);
                        var snip = (start > 0 ? "..." : "") + line.Substring(start, len).Trim() + (start + len < line.Length ? "..." : "");
                        hits.Add(new SearchHit
                        {
                            NodeId = id,
                            NodeTitle = title,
                            Breadcrumb = path,
                            LineNumber = i + 1,
                            CharOffsetInLine = idx,
                            MatchLength = kw.Length,
                            Snippet = $"第 {i + 1} 行: {snip}"
                        });
                        idx = line.IndexOf(kw, idx + kw.Length, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
        return hits;
    }

    public int ExportMarkdown(long? targetNodeId, bool includeChildren, string destMdPath,
        FileArchiveService fileArchive, bool copyReferencedFiles)
    {
        var conn = RequireConn();
        var sb = new StringBuilder();
        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (targetNodeId.HasValue && !includeChildren)
        {
            var detail = GetNodeContent(targetNodeId.Value);
            sb.Append("# ").AppendLine(detail.Title).AppendLine();
            if (!string.IsNullOrWhiteSpace(detail.Content))
                sb.AppendLine(detail.Content.TrimEnd()).AppendLine();
            foreach (var f in fileArchive.ExtractReferencedRelPaths(detail.Content))
                referencedFiles.Add(f);
        }
        else
        {
            var all = new Dictionary<long, List<(long Id, string Title, string Content)>>();
            (long Id, string Title, string Content)? rootInfo = null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, COALESCE(parent_id, 0), title, content FROM nodes ORDER BY sort_order ASC, id ASC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0); var pid = r.GetInt64(1); var t = r.GetString(2); var c = r.GetString(3);
                if (targetNodeId.HasValue && id == targetNodeId.Value) rootInfo = (id, t, c);
                if (!all.TryGetValue(pid, out var list)) all[pid] = list = new();
                list.Add((id, t, c));
            }

            void WriteItem(long id, string title, string content, int depth)
            {
                sb.Append(new string('#', Math.Max(1, Math.Min(6, depth + 1)))).Append(' ').AppendLine(title).AppendLine();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    sb.AppendLine(content.TrimEnd()).AppendLine();
                    foreach (var f in fileArchive.ExtractReferencedRelPaths(content)) referencedFiles.Add(f);
                }
                if (all.TryGetValue(id, out var kids))
                    foreach (var k in kids) WriteItem(k.Id, k.Title, k.Content, depth + 1);
            }

            if (targetNodeId.HasValue && rootInfo.HasValue)
                WriteItem(rootInfo.Value.Id, rootInfo.Value.Title, rootInfo.Value.Content, 0);
            else if (all.TryGetValue(0, out var roots))
                foreach (var root in roots) WriteItem(root.Id, root.Title, root.Content, 0);
        }

        File.WriteAllText(destMdPath, sb.ToString(), new UTF8Encoding(false));

        int copiedCount = 0;
        if (copyReferencedFiles && referencedFiles.Count > 0)
        {
            var exportFilesDir = Path.Combine(Path.GetDirectoryName(destMdPath)!, FileArchiveService.FilesFolderName);
            Directory.CreateDirectory(exportFilesDir);
            foreach (var rel in referencedFiles)
            {
                var src = fileArchive.ResolveExistingFile(rel);
                if (src == null) continue;
                var fileName = rel.StartsWith(FileArchiveService.FilesFolderName + "/", StringComparison.OrdinalIgnoreCase)
                    ? rel.Substring(FileArchiveService.FilesFolderName.Length + 1)
                    : Path.GetFileName(src);
                var dst = Path.Combine(exportFilesDir, fileName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(src, dst, overwrite: true);
                    copiedCount++;
                }
            }
        }
        return copiedCount;
    }

    public void SmartVacuumIfNeeded()
    {
        if (_conn == null) return;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA freelist_count;";
            var freePages = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            if (freePages > 0 || HasModificationsSinceOpen)
                ExecNonQuery(_conn, "VACUUM;");
        }
        catch { }
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
    }
}

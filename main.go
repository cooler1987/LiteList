package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"embed"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	goruntime "runtime"
	"strings"
	"sync"
	"time"

	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
	"github.com/wailsapp/wails/v2/pkg/options/mac"
	"github.com/wailsapp/wails/v2/pkg/options/windows"
	wailsrt "github.com/wailsapp/wails/v2/pkg/runtime"
	_ "modernc.org/sqlite"
)

//go:embed all:frontend
var rawAssets embed.FS

const (
	currentSchemaVersion = 1
	defaultDBFileName    = "notes.llist"
	filesFolderName      = "files"
)

type Node struct {
	ID         int64   `json:"id"`
	ParentID   int64   `json:"parentId"`
	Title      string  `json:"title"`
	SortOrder  float64 `json:"sortOrder"`
	IsExpanded bool    `json:"isExpanded"`
	UpdatedAt  string  `json:"updatedAt"`
}

type NodeContent struct {
	ID        int64  `json:"id"`
	Title     string `json:"title"`
	Content   string `json:"content"`
	UpdatedAt string `json:"updatedAt"`
}

type SearchResult struct {
	ID       int64  `json:"id"`
	ParentID int64  `json:"parentId"`
	Title    string `json:"title"`
	Snippet  string `json:"snippet"`
}

type AppInfo struct {
	DBPath   string `json:"dbPath"`
	DBName   string `json:"dbName"`
	FilesDir string `json:"filesDir"`
	Platform string `json:"platform"`
}

type AttachmentResult struct {
	RelPath  string `json:"relPath"`
	Filename string `json:"filename"`
	IsImage  bool   `json:"isImage"`
	Size     int64  `json:"size"`
}

type ResolveResult struct {
	Content string `json:"content"`
	Count   int    `json:"count"`
}

type App struct {
	ctx           context.Context
	mu            sync.Mutex
	db            *sql.DB
	dbPath        string
	dirty         bool
	forceQuitting bool
}

func NewApp() *App {
	return &App{}
}

// getAppBaseDir 获取软件所在目录（兼容 macOS .app 与调试目录）
func getAppBaseDir() string {
	exe, err := os.Executable()
	if err != nil {
		wd, _ := os.Getwd()
		return wd
	}
	if strings.Contains(exe, "go-build") || strings.Contains(exe, os.TempDir()) {
		if wd, err := os.Getwd(); err == nil {
			return wd
		}
	}
	dir := filepath.Dir(exe)
	if goruntime.GOOS == "darwin" {
		clean := filepath.ToSlash(dir)
		if strings.HasSuffix(clean, ".app/Contents/MacOS") {
			return filepath.Clean(filepath.Join(dir, "..", "..", ".."))
		}
	}
	return dir
}

func isDirWritable(dir string) bool {
	probe := filepath.Join(dir, ".litelist_write_test")
	f, err := os.Create(probe)
	if err != nil {
		return false
	}
	_ = f.Close()
	_ = os.Remove(probe)
	return true
}

func resolveDefaultDBPath() string {
	if len(os.Args) > 1 {
		arg := strings.TrimSpace(os.Args[1])
		if arg != "" && !strings.HasPrefix(arg, "-") {
			if abs, err := filepath.Abs(arg); err == nil {
				return abs
			}
		}
	}
	base := getAppBaseDir()
	if isDirWritable(base) {
		return filepath.Join(base, defaultDBFileName)
	}
	if home, err := os.UserHomeDir(); err == nil {
		return filepath.Join(home, defaultDBFileName)
	}
	return defaultDBFileName
}

// getFilesDir 严格按照要求将 files 文件夹建在软件所在目录下（若软件目录只读才回退到 .llist 同目录）
func (a *App) getFilesDir() string {
	base := getAppBaseDir()
	if isDirWritable(base) {
		return filepath.Join(base, filesFolderName)
	}
	a.mu.Lock()
	dbp := a.dbPath
	a.mu.Unlock()
	if dbp != "" {
		return filepath.Join(filepath.Dir(dbp), filesFolderName)
	}
	return filepath.Join(base, filesFolderName)
}

// resolveFileFromFilesDir 优先从软件目录/files查找，其次兼容 .llist 同目录/files（方便带走双文件到任意目录）
func (a *App) resolveFileFromFilesDir(relPath string) (string, bool) {
	cleaned := strings.TrimPrefix(filepath.ToSlash(strings.TrimSpace(relPath)), filesFolderName+"/")
	cleaned = strings.TrimPrefix(cleaned, "/")
	cleaned = filepath.Clean(filepath.FromSlash(cleaned))
	if cleaned == "" || cleaned == "." || strings.HasPrefix(cleaned, "..") || filepath.IsAbs(cleaned) {
		return "", false
	}

	candidates := []string{filepath.Join(a.getFilesDir(), cleaned)}
	a.mu.Lock()
	dbp := a.dbPath
	a.mu.Unlock()
	if dbp != "" {
		alt := filepath.Join(filepath.Dir(dbp), filesFolderName, cleaned)
		if alt != candidates[0] {
			candidates = append(candidates, alt)
		}
	}

	for _, c := range candidates {
		if info, err := os.Stat(c); err == nil && !info.IsDir() {
			return c, true
		}
	}
	return "", false
}

// filesHandler 拦截 /files/... 请求并安全输出磁盘中的图片与附件
func (a *App) filesHandler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasPrefix(r.URL.Path, "/"+filesFolderName+"/") {
			http.NotFound(w, r)
			return
		}
		rawRel := strings.TrimPrefix(r.URL.Path, "/"+filesFolderName+"/")
		unescaped, err := url.PathUnescape(rawRel)
		if err != nil {
			unescaped = rawRel
		}
		if fullPath, ok := a.resolveFileFromFilesDir(unescaped); ok {
			http.ServeFile(w, r, fullPath)
			return
		}
		http.NotFound(w, r)
	})
}

func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	target := resolveDefaultDBPath()
	if err := a.OpenDB(target); err != nil {
		fallback := filepath.Join(getAppBaseDir(), defaultDBFileName)
		if target != fallback {
			_ = a.OpenDB(fallback)
		}
	}

	// 监听拖入窗口的本地文件：自动复制到 files/ 并通知前端插入 Markdown 引用
	wailsrt.OnFileDrop(ctx, func(x, y int, paths []string) {
		var imported []AttachmentResult
		for _, p := range paths {
			if res, err := a.ImportExternalFile(p); err == nil {
				imported = append(imported, res)
			}
		}
		if len(imported) > 0 {
			wailsrt.EventsEmit(ctx, "app:files-dropped", imported)
		}
	})
}

func (a *App) beforeClose(ctx context.Context) (prevent bool) {
	a.mu.Lock()
	isDirty := a.dirty
	force := a.forceQuitting
	a.mu.Unlock()

	if isDirty && !force {
		wailsrt.EventsEmit(ctx, "app:request-flush")
		return true
	}
	return false
}

func (a *App) shutdown(_ context.Context) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.db != nil {
		_ = a.db.Close()
		a.db = nil
	}
}

func (a *App) SetDirty(dirty bool) {
	a.mu.Lock()
	a.dirty = dirty
	a.mu.Unlock()
}

func (a *App) ForceQuit() {
	a.mu.Lock()
	a.dirty = false
	a.forceQuitting = true
	a.mu.Unlock()
	wailsrt.Quit(a.ctx)
}

func (a *App) ClipboardGetText() (string, error) {
	return wailsrt.ClipboardGetText(a.ctx)
}

func (a *App) ClipboardSetText(text string) error {
	return wailsrt.ClipboardSetText(a.ctx, text)
}

func (a *App) OpenExternalURL(rawURL string) {
	if strings.HasPrefix(rawURL, "http://") || strings.HasPrefix(rawURL, "https://") {
		wailsrt.BrowserOpenURL(a.ctx, rawURL)
	}
}

func (a *App) GetAppInfo() AppInfo {
	a.mu.Lock()
	dbp := a.dbPath
	a.mu.Unlock()
	name := filepath.Base(dbp)
	if dbp == "" {
		name = "未打开"
	}
	return AppInfo{
		DBPath:   dbp,
		DBName:   name,
		FilesDir: a.getFilesDir(),
		Platform: goruntime.GOOS,
	}
}

// ===== 数据库打开、迁移与空间压缩 =====

func (a *App) OpenDB(filePath string) error {
	if strings.TrimSpace(filePath) == "" {
		return errors.New("文件路径不能为空")
	}
	absPath, err := filepath.Abs(filePath)
	if err != nil {
		return err
	}

	isNewFile := false
	info, statErr := os.Stat(absPath)
	if os.IsNotExist(statErr) || (statErr == nil && info.Size() == 0) {
		isNewFile = true
	}

	if err := os.MkdirAll(filepath.Dir(absPath), 0o755); err != nil {
		return fmt.Errorf("创建目录失败: %w", err)
	}

	newDB, err := sql.Open("sqlite", absPath)
	if err != nil {
		return fmt.Errorf("打开数据库失败: %w", err)
	}
	newDB.SetMaxOpenConns(1)

	if err := newDB.Ping(); err != nil {
		_ = newDB.Close()
		return fmt.Errorf("连接 SQLite 失败: %w", err)
	}

	if err := initAndMigrateDB(newDB, isNewFile); err != nil {
		_ = newDB.Close()
		return err
	}

	a.mu.Lock()
	oldDB := a.db
	a.db = newDB
	a.dbPath = absPath
	a.dirty = false
	a.mu.Unlock()

	if oldDB != nil {
		_ = oldDB.Close()
	}
	if a.ctx != nil {
		wailsrt.WindowSetTitle(a.ctx, fmt.Sprintf("LiteList - %s", filepath.Base(absPath)))
	}
	return nil
}

func initAndMigrateDB(db *sql.DB, isNewFile bool) error {
	pragmas := []string{
		"PRAGMA journal_mode = DELETE;",
		"PRAGMA synchronous = NORMAL;",
		"PRAGMA foreign_keys = ON;",
		"PRAGMA busy_timeout = 5000;",
	}
	for _, p := range pragmas {
		if _, err := db.Exec(p); err != nil {
			return fmt.Errorf("SQLite 参数初始化失败 (%s): %w", p, err)
		}
	}

	var version int
	if err := db.QueryRow("PRAGMA user_version;").Scan(&version); err != nil {
		return fmt.Errorf("文件不是有效的 SQLite 笔记库: %w", err)
	}

	if version == 0 {
		if !isNewFile {
			var tableCount int
			err := db.QueryRow("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name != 'sqlite_sequence';").Scan(&tableCount)
			if err == nil && tableCount > 0 {
				var hasNodes int
				_ = db.QueryRow("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='nodes';").Scan(&hasNodes)
				if hasNodes == 0 {
					return errors.New("所选文件不是有效的 LiteList (.llist) 笔记库")
				}
			}
		}

		schemaSQL := `
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
		PRAGMA user_version = 1;
		`
		if _, err := db.Exec(schemaSQL); err != nil {
			return fmt.Errorf("初始化数据表失败: %w", err)
		}
		if isNewFile {
			seedWelcomeNodes(db)
		}
	}
	return nil
}

func seedWelcomeNodes(db *sql.DB) {
	welcomeMD := `# LiteList v2 使用指南

主文件 `.llist` 保持纯文本 Markdown 结构，所有插入或引用的图片与附件会自动复制到程序同目录的 `files/` 文件夹中。

## 1. 编辑器增强特性
- **行号与语法高亮**：支持 Markdown 语法及常见代码块着色（` + "`go`" + ` / ` + "`python`" + ` / ` + "`js`" + ` / ` + "`sql`" + ` 等）
- **文本多级折叠**：点击行号旁 ` + "`▾`" + ` 折叠标题章节或缩进列表，预览模式同样支持标题折叠
- **三视图切换**：顶部支持「纯编辑 / 双栏对照 / 纯预览」（` + "`Ctrl+E`" + ` 快速切换）
- **完整右键与查找**：编辑器内右键支持剪切复制粘贴、Markdown 排版、插图与折叠；` + "`Ctrl+F`" + ` / ` + "`Ctrl+H`" + ` 文内查找替换

## 2. 图片与附件（自动归档至 files/）
1. 直接截屏后在编辑区按 ` + "`Ctrl+V`" + ` 粘贴图片
2. 直接将本地文件拖入窗口，或点击工具栏「插图/附件」
3. 在 Markdown 中粘贴或引用本地路径，也会自动复制进 ` + "`files/`" + ` 并转为相对路径

## 3. 代码高亮示例
` + "```go\npackage main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"Hello, LiteList!\")\n}\n```" + `

## 4. 交互待办列表
- [x] 行号显示与代码/Markdown 高亮
  - [x] 缩进子项也可在编辑区折叠
- [ ] 在预览模式下直接点击我勾选试试`

	res, err := db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (NULL, ?, ?, 1000.0, 1)`,
		"欢迎使用 LiteList v2",
		welcomeMD,
	)
	if err != nil {
		return
	}
	rootID, _ := res.LastInsertId()
	_, _ = db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (?, ?, ?, 1000.0, 1)`,
		rootID,
		"数据迁移说明",
		"### 带走你的全部笔记\n\n- 只需带走 `.llist` 笔记库文件和同目录下的 `files/` 文件夹即可完整保留所有图文与附件。\n- 主数据库绝不混入 Base64 膨胀文本，始终轻量极速。",
	)
}

func (a *App) SelectAndOpenDB() (string, error) {
	path, err := wailsrt.OpenFileDialog(a.ctx, wailsrt.OpenDialogOptions{
		Title:            "打开 LiteList 笔记库",
		DefaultDirectory: filepath.Dir(a.dbPath),
		Filters: []wailsrt.FileFilter{
			{DisplayName: "LiteList 笔记库 (*.llist)", Pattern: "*.llist"},
			{DisplayName: "所有文件 (*.*)", Pattern: "*.*"},
		},
	})
	if err != nil || path == "" {
		return "", err
	}
	if err := a.OpenDB(path); err != nil {
		return "", err
	}
	return path, nil
}

func (a *App) CreateNewDB() (string, error) {
	path, err := wailsrt.SaveFileDialog(a.ctx, wailsrt.SaveDialogOptions{
		Title:                "新建 LiteList 笔记库",
		DefaultDirectory:     filepath.Dir(a.dbPath),
		DefaultFilename:      "my-notes.llist",
		CanCreateDirectories: true,
		Filters: []wailsrt.FileFilter{
			{DisplayName: "LiteList 笔记库 (*.llist)", Pattern: "*.llist"},
		},
	})
	if err != nil || path == "" {
		return "", err
	}
	if !strings.HasSuffix(strings.ToLower(path), ".llist") {
		path += ".llist"
	}
	_ = os.Remove(path)
	if err := a.OpenDB(path); err != nil {
		return "", err
	}
	return path, nil
}

// VacuumDB 压缩清理数据库空闲页，返回压缩后文件字节数
func (a *App) VacuumDB() (int64, error) {
	db, err := a.getDB()
	if err != nil {
		return 0, err
	}
	if _, err := db.Exec("VACUUM;"); err != nil {
		return 0, err
	}
	a.mu.Lock()
	dbp := a.dbPath
	a.mu.Unlock()
	if info, err := os.Stat(dbp); err == nil {
		return info.Size(), nil
	}
	return 0, nil
}

// ===== 图片与附件管理（全部复制至 files/ 文件夹） =====

func isImageExt(ext string) bool {
	switch strings.ToLower(ext) {
	case ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico":
		return true
	default:
		return false
	}
}

func sanitizeFilename(name string) string {
	base := filepath.Base(strings.TrimSpace(name))
	if base == "" || base == "." || base == ".." {
		base = "file"
	}
	replacer := strings.NewReplacer(
		"/", "_", `\`, "_", ":", "_", "*", "_",
		"?", "_", `"`, "_", "<", "_", ">", "_",
		"|", "_", " ", "_", "(", "_", ")", "_",
		"[", "_", "]", "_",
	)
	return replacer.Replace(base)
}

func randomHex4() string {
	var b [2]byte
	_, _ = rand.Read(b[:])
	return hex.EncodeToString(b[:])
}

func (a *App) prepareFilesDir() (string, error) {
	dir := a.getFilesDir()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", fmt.Errorf("创建 files 目录失败: %w", err)
	}
	return dir, nil
}

// SavePastedAttachment 保存前端粘贴的截图/二进制文件到 files/
func (a *App) SavePastedAttachment(filename string, base64Data string) (AttachmentResult, error) {
	if idx := strings.Index(base64Data, ","); idx >= 0 && strings.Contains(base64Data[:idx], ";base64") {
		base64Data = base64Data[idx+1:]
	}
	data, err := base64.StdEncoding.DecodeString(base64Data)
	if err != nil {
		return AttachmentResult{}, fmt.Errorf("解码附件数据失败: %w", err)
	}
	filesDir, err := a.prepareFilesDir()
	if err != nil {
		return AttachmentResult{}, err
	}
	safeName := sanitizeFilename(filename)
	storedName := fmt.Sprintf("%s_%s_%s", time.Now().Format("20060102_150405"), randomHex4(), safeName)
	destPath := filepath.Join(filesDir, storedName)

	if err := os.WriteFile(destPath, data, 0o644); err != nil {
		return AttachmentResult{}, fmt.Errorf("写入 files 目录失败: %w", err)
	}
	return AttachmentResult{
		RelPath:  filesFolderName + "/" + storedName,
		Filename: safeName,
		IsImage:  isImageExt(filepath.Ext(safeName)),
		Size:     int64(len(data)),
	}, nil
}

// ImportExternalFile 将指定本地文件流式复制到 files/ 目录中
func (a *App) ImportExternalFile(rawPath string) (AttachmentResult, error) {
	cleanPath := strings.TrimSpace(rawPath)
	if strings.HasPrefix(strings.ToLower(cleanPath), "file://") {
		if u, err := url.Parse(cleanPath); err == nil {
			p := u.Path
			if unesc, err := url.PathUnescape(p); err == nil {
				p = unesc
			}
			// Windows file:///C:/... 去掉前导斜杠
			if goruntime.GOOS == "windows" && len(p) >= 3 && p[0] == '/' && p[2] == ':' {
				p = p[1:]
			}
			cleanPath = filepath.FromSlash(p)
		}
	}
	absSrc, err := filepath.Abs(cleanPath)
	if err != nil {
		return AttachmentResult{}, err
	}
	info, err := os.Stat(absSrc)
	if err != nil {
		return AttachmentResult{}, err
	}
	if info.IsDir() {
		return AttachmentResult{}, errors.New("暂不支持直接插入文件夹")
	}

	filesDir, err := a.prepareFilesDir()
	if err != nil {
		return AttachmentResult{}, err
	}
	absFilesDir, _ := filepath.Abs(filesDir)

	// 如果文件本身就已经在 files/ 目录内，直接返回现有相对路径，避免重复拷贝
	if rel, err := filepath.Rel(absFilesDir, absSrc); err == nil && !strings.HasPrefix(rel, "..") {
		return AttachmentResult{
			RelPath:  filesFolderName + "/" + filepath.ToSlash(rel),
			Filename: filepath.Base(absSrc),
			IsImage:  isImageExt(filepath.Ext(absSrc)),
			Size:     info.Size(),
		}, nil
	}

	safeName := sanitizeFilename(filepath.Base(absSrc))
	storedName := fmt.Sprintf("%s_%s_%s", time.Now().Format("20060102_150405"), randomHex4(), safeName)
	destPath := filepath.Join(filesDir, storedName)

	srcFile, err := os.Open(absSrc)
	if err != nil {
		return AttachmentResult{}, err
	}
	defer srcFile.Close()

	dstFile, err := os.Create(destPath)
	if err != nil {
		return AttachmentResult{}, err
	}
	defer dstFile.Close()

	written, err := io.Copy(dstFile, srcFile)
	if err != nil {
		return AttachmentResult{}, err
	}

	return AttachmentResult{
		RelPath:  filesFolderName + "/" + storedName,
		Filename: safeName,
		IsImage:  isImageExt(filepath.Ext(safeName)),
		Size:     written,
	}, nil
}

// SelectAndImportFiles 弹出多选文件对话框并复制进 files/
func (a *App) SelectAndImportFiles() ([]AttachmentResult, error) {
	paths, err := wailsrt.OpenMultipleFilesDialog(a.ctx, wailsrt.OpenDialogOptions{
		Title: "选择要插入的图片或附件（将自动复制到 files 目录）",
	})
	if err != nil || len(paths) == 0 {
		return nil, err
	}
	var out []AttachmentResult
	for _, p := range paths {
		res, impErr := a.ImportExternalFile(p)
		if impErr != nil {
			return out, impErr
		}
		out = append(out, res)
	}
	return out, nil
}

var mdRefPattern = regexp.MustCompile(`(!?\[[^\]]*\])\(([^)\r\n]+)\)`)

// ResolveLocalReferences 扫描 Markdown 中引用的本地磁盘文件路径，自动复制入 files/ 并改写为相对路径
func (a *App) ResolveLocalReferences(content string) (ResolveResult, error) {
	if content == "" {
		return ResolveResult{Content: content, Count: 0}, nil
	}
	count := 0
	updated := mdRefPattern.ReplaceAllStringFunc(content, func(full string) string {
		m := mdRefPattern.FindStringSubmatch(full)
		if len(m) != 3 {
			return full
		}
		labelPart := m[1]
		rawTarget := strings.TrimSpace(m[2])
		// 保留可选 title，如 path "title"
		pathOnly := rawTarget
		titleSuffix := ""
		if strings.HasPrefix(rawTarget, "<") {
			if end := strings.Index(rawTarget, ">"); end > 1 {
				pathOnly = rawTarget[1:end]
				titleSuffix = rawTarget[end+1:]
			}
		} else if idx := strings.Index(rawTarget, ` "`); idx > 0 {
			pathOnly = rawTarget[:idx]
			titleSuffix = rawTarget[idx:]
		}

		lower := strings.ToLower(pathOnly)
		if pathOnly == "" ||
			strings.HasPrefix(lower, "http://") ||
			strings.HasPrefix(lower, "https://") ||
			strings.HasPrefix(lower, "mailto:") ||
			strings.HasPrefix(lower, "data:") ||
			strings.HasPrefix(lower, "#") ||
			strings.HasPrefix(lower, filesFolderName+"/") ||
			strings.HasPrefix(lower, "/"+filesFolderName+"/") {
			return full
		}

		if res, err := a.ImportExternalFile(pathOnly); err == nil {
			count++
			return fmt.Sprintf("%s(%s%s)", labelPart, res.RelPath, titleSuffix)
		}
		return full
	})
	return ResolveResult{Content: updated, Count: count}, nil
}

// OpenAttachment 用系统默认关联软件打开 files/ 中的附件
func (a *App) OpenAttachment(relPath string) error {
	fullPath, ok := a.resolveFileFromFilesDir(relPath)
	if !ok {
		return errors.New("在 files 目录中找不到该附件文件")
	}
	switch goruntime.GOOS {
	case "windows":
		return exec.Command("explorer", fullPath).Start()
	case "darwin":
		return exec.Command("open", fullPath).Start()
	default:
		return exec.Command("xdg-open", fullPath).Start()
	}
}

// SaveAttachmentAs 将附件另存为到指定位置
func (a *App) SaveAttachmentAs(relPath string) (string, error) {
	fullPath, ok := a.resolveFileFromFilesDir(relPath)
	if !ok {
		return "", errors.New("找不到该附件文件")
	}
	savePath, err := wailsrt.SaveFileDialog(a.ctx, wailsrt.SaveDialogOptions{
		Title:           "附件另存为",
		DefaultFilename: filepath.Base(fullPath),
	})
	if err != nil || savePath == "" {
		return "", err
	}
	src, err := os.Open(fullPath)
	if err != nil {
		return "", err
	}
	defer src.Close()
	dst, err := os.Create(savePath)
	if err != nil {
		return "", err
	}
	defer dst.Close()
	if _, err := io.Copy(dst, src); err != nil {
		return "", err
	}
	return savePath, nil
}

// ===== 节点 CRUD 与树操作 =====

func (a *App) getDB() (*sql.DB, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.db == nil {
		return nil, errors.New("当前未打开任何笔记库")
	}
	return a.db, nil
}

func nullableParent(parentID int64) any {
	if parentID <= 0 {
		return nil
	}
	return parentID
}

func (a *App) GetTree() ([]Node, error) {
	db, err := a.getDB()
	if err != nil {
		return nil, err
	}
	rows, err := db.Query(`
		SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at
		FROM nodes
		ORDER BY sort_order ASC, id ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	nodes := make([]Node, 0, 64)
	for rows.Next() {
		var n Node
		var expandedInt int
		if err := rows.Scan(&n.ID, &n.ParentID, &n.Title, &n.SortOrder, &expandedInt, &n.UpdatedAt); err != nil {
			return nil, err
		}
		n.IsExpanded = expandedInt != 0
		nodes = append(nodes, n)
	}
	return nodes, rows.Err()
}

func (a *App) GetNodeContent(id int64) (NodeContent, error) {
	db, err := a.getDB()
	if err != nil {
		return NodeContent{}, err
	}
	var nc NodeContent
	err = db.QueryRow(`SELECT id, title, content, updated_at FROM nodes WHERE id = ?`, id).
		Scan(&nc.ID, &nc.Title, &nc.Content, &nc.UpdatedAt)
	return nc, err
}

func (a *App) CreateNode(parentID int64, title string) (Node, error) {
	db, err := a.getDB()
	if err != nil {
		return Node{}, err
	}
	if strings.TrimSpace(title) == "" {
		title = "未命名节点"
	}

	var maxOrder sql.NullFloat64
	if parentID <= 0 {
		_ = db.QueryRow(`SELECT MAX(sort_order) FROM nodes WHERE parent_id IS NULL`).Scan(&maxOrder)
	} else {
		_ = db.QueryRow(`SELECT MAX(sort_order) FROM nodes WHERE parent_id = ?`, parentID).Scan(&maxOrder)
	}
	nextOrder := 1000.0
	if maxOrder.Valid {
		nextOrder = maxOrder.Float64 + 1000.0
	}

	res, err := db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (?, ?, '', ?, 1)`,
		nullableParent(parentID), title, nextOrder,
	)
	if err != nil {
		return Node{}, err
	}
	id, _ := res.LastInsertId()
	if parentID > 0 {
		_, _ = db.Exec(`UPDATE nodes SET is_expanded = 1 WHERE id = ?`, parentID)
	}

	var n Node
	var expandedInt int
	err = db.QueryRow(`SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at FROM nodes WHERE id = ?`, id).
		Scan(&n.ID, &n.ParentID, &n.Title, &n.SortOrder, &expandedInt, &n.UpdatedAt)
	n.IsExpanded = expandedInt != 0
	return n, err
}

func (a *App) CreateNodeAt(parentID int64, title string, sortOrder float64) (Node, error) {
	db, err := a.getDB()
	if err != nil {
		return Node{}, err
	}
	if strings.TrimSpace(title) == "" {
		title = "未命名节点"
	}

	res, err := db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (?, ?, '', ?, 1)`,
		nullableParent(parentID), title, sortOrder,
	)
	if err != nil {
		return Node{}, err
	}
	id, _ := res.LastInsertId()
	_ = a.maybeRebalance(db, parentID)

	var n Node
	var expandedInt int
	err = db.QueryRow(`SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at FROM nodes WHERE id = ?`, id).
		Scan(&n.ID, &n.ParentID, &n.Title, &n.SortOrder, &expandedInt, &n.UpdatedAt)
	n.IsExpanded = expandedInt != 0
	return n, err
}

func (a *App) RenameNode(id int64, title string) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	if strings.TrimSpace(title) == "" {
		title = "未命名节点"
	}
	_, err = db.Exec(`UPDATE nodes SET title = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?`, title, id)
	return err
}

func (a *App) UpdateNodeContent(id int64, content string) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	_, err = db.Exec(`UPDATE nodes SET content = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?`, content, id)
	if err == nil {
		a.SetDirty(false)
	}
	return err
}

func (a *App) UpdateNode(id int64, title string, content string) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	if strings.TrimSpace(title) == "" {
		title = "未命名节点"
	}
	_, err = db.Exec(`UPDATE nodes SET title = ?, content = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?`, title, content, id)
	if err == nil {
		a.SetDirty(false)
	}
	return err
}

func (a *App) SetNodeExpanded(id int64, expanded bool) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	val := 0
	if expanded {
		val = 1
	}
	_, err = db.Exec(`UPDATE nodes SET is_expanded = ? WHERE id = ?`, val, id)
	return err
}

// SetAllExpanded 一键展开或折叠全部节点
func (a *App) SetAllExpanded(expanded bool) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	val := 0
	if expanded {
		val = 1
	}
	_, err = db.Exec(`UPDATE nodes SET is_expanded = ?`, val)
	return err
}

// DuplicateNode 克隆节点（可选包含所有子孙节点）
func (a *App) DuplicateNode(id int64, withChildren bool) (Node, error) {
	db, err := a.getDB()
	if err != nil {
		return Node{}, err
	}

	var srcParent sql.NullInt64
	var srcTitle, srcContent string
	var srcOrder float64
	err = db.QueryRow(`SELECT parent_id, title, content, sort_order FROM nodes WHERE id = ?`, id).
		Scan(&srcParent, &srcTitle, &srcContent, &srcOrder)
	if err != nil {
		return Node{}, err
	}
	parentID := int64(0)
	if srcParent.Valid {
		parentID = srcParent.Int64
	}

	// 计算紧邻原节点后方的权重
	var nextOrder sql.NullFloat64
	if parentID <= 0 {
		_ = db.QueryRow(`SELECT MIN(sort_order) FROM nodes WHERE parent_id IS NULL AND sort_order > ?`, srcOrder).Scan(&nextOrder)
	} else {
		_ = db.QueryRow(`SELECT MIN(sort_order) FROM nodes WHERE parent_id = ? AND sort_order > ?`, parentID, srcOrder).Scan(&nextOrder)
	}
	newOrder := srcOrder + 1000.0
	if nextOrder.Valid {
		newOrder = (srcOrder + nextOrder.Float64) / 2.0
	}

	tx, err := db.Begin()
	if err != nil {
		return Node{}, err
	}
	defer func() { _ = tx.Rollback() }()

	var cloneBranch func(oldID int64, newParentID int64, order float64, rename bool) (int64, error)
	cloneBranch = func(oldID int64, newParentID int64, order float64, rename bool) (int64, error) {
		var t, c string
		var exp int
		if err := tx.QueryRow(`SELECT title, content, is_expanded FROM nodes WHERE id = ?`, oldID).Scan(&t, &c, &exp); err != nil {
			return 0, err
		}
		if rename {
			t = t + " (副本)"
		}
		res, err := tx.Exec(
			`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (?, ?, ?, ?, ?)`,
			nullableParent(newParentID), t, c, order, exp,
		)
		if err != nil {
			return 0, err
		}
		newID, _ := res.LastInsertId()

		if withChildren {
			rows, err := tx.Query(`SELECT id, sort_order FROM nodes WHERE parent_id = ? ORDER BY sort_order ASC, id ASC`, oldID)
			if err != nil {
				return 0, err
			}
			type kid struct {
				id    int64
				order float64
			}
			var kids []kid
			for rows.Next() {
				var k kid
				if err := rows.Scan(&k.id, &k.order); err == nil {
					kids = append(kids, k)
				}
			}
			_ = rows.Close()
			for _, k := range kids {
				if _, err := cloneBranch(k.id, newID, k.order, false); err != nil {
					return 0, err
				}
			}
		}
		return newID, nil
	}

	newRootID, err := cloneBranch(id, parentID, newOrder, true)
	if err != nil {
		return Node{}, err
	}
	if err := tx.Commit(); err != nil {
		return Node{}, err
	}
	_ = a.maybeRebalance(db, parentID)

	var n Node
	var expandedInt int
	err = db.QueryRow(`SELECT id, COALESCE(parent_id, 0), title, sort_order, is_expanded, updated_at FROM nodes WHERE id = ?`, newRootID).
		Scan(&n.ID, &n.ParentID, &n.Title, &n.SortOrder, &expandedInt, &n.UpdatedAt)
	n.IsExpanded = expandedInt != 0
	return n, err
}

// SortChildren 对指定父节点下的直接子节点按标题字典序排序
func (a *App) SortChildren(parentID int64) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	var rows *sql.Rows
	if parentID <= 0 {
		rows, err = db.Query(`SELECT id FROM nodes WHERE parent_id IS NULL ORDER BY title COLLATE NOCASE ASC, id ASC`)
	} else {
		rows, err = db.Query(`SELECT id FROM nodes WHERE parent_id = ? ORDER BY title COLLATE NOCASE ASC, id ASC`, parentID)
	}
	if err != nil {
		return err
	}
	var ids []int64
	for rows.Next() {
		var id int64
		if err := rows.Scan(&id); err == nil {
			ids = append(ids, id)
		}
	}
	_ = rows.Close()

	tx, err := db.Begin()
	if err != nil {
		return err
	}
	for i, id := range ids {
		_, _ = tx.Exec(`UPDATE nodes SET sort_order = ? WHERE id = ?`, float64(i+1)*1000.0, id)
	}
	return tx.Commit()
}

func (a *App) MoveNode(id int64, newParentID int64, newSortOrder float64) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	if id == newParentID {
		return errors.New("不能将节点移动到自身内部")
	}

	if newParentID > 0 {
		var isDescendant int
		err := db.QueryRow(`
			WITH RECURSIVE sub(nid) AS (
				SELECT id FROM nodes WHERE id = ?
				UNION ALL
				SELECT n.id FROM nodes n JOIN sub s ON n.parent_id = s.nid
			)
			SELECT COUNT(*) FROM sub WHERE nid = ?;
		`, id, newParentID).Scan(&isDescendant)
		if err != nil {
			return err
		}
		if isDescendant > 0 {
			return errors.New("不能将父节点拖拽到其子节点内部")
		}
	}

	_, err = db.Exec(
		`UPDATE nodes SET parent_id = ?, sort_order = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?`,
		nullableParent(newParentID), newSortOrder, id,
	)
	if err != nil {
		return err
	}
	if newParentID > 0 {
		_, _ = db.Exec(`UPDATE nodes SET is_expanded = 1 WHERE id = ?`, newParentID)
	}
	_ = a.maybeRebalance(db, newParentID)
	return nil
}

func (a *App) maybeRebalance(db *sql.DB, parentID int64) error {
	var rows *sql.Rows
	var err error
	if parentID <= 0 {
		rows, err = db.Query(`SELECT id, sort_order FROM nodes WHERE parent_id IS NULL ORDER BY sort_order ASC, id ASC`)
	} else {
		rows, err = db.Query(`SELECT id, sort_order FROM nodes WHERE parent_id = ? ORDER BY sort_order ASC, id ASC`, parentID)
	}
	if err != nil {
		return err
	}
	type item struct {
		id    int64
		order float64
	}
	var items []item
	needRebalance := false
	for rows.Next() {
		var it item
		if err := rows.Scan(&it.id, &it.order); err == nil {
			if len(items) > 0 && (it.order-items[len(items)-1].order) < 0.001 {
				needRebalance = true
			}
			items = append(items, it)
		}
	}
	_ = rows.Close()

	if !needRebalance {
		return nil
	}
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	for i, it := range items {
		_, _ = tx.Exec(`UPDATE nodes SET sort_order = ? WHERE id = ?`, float64(i+1)*1000.0, it.id)
	}
	return tx.Commit()
}

func (a *App) DeleteNode(id int64) (int64, error) {
	db, err := a.getDB()
	if err != nil {
		return 0, err
	}
	var total int64
	_ = db.QueryRow(`
		WITH RECURSIVE sub(nid) AS (
			SELECT id FROM nodes WHERE id = ?
			UNION ALL
			SELECT n.id FROM nodes n JOIN sub s ON n.parent_id = s.nid
		)
		SELECT COUNT(*) FROM sub;
	`, id).Scan(&total)

	if _, err := db.Exec(`DELETE FROM nodes WHERE id = ?`, id); err != nil {
		return 0, err
	}
	return total, nil
}

func (a *App) SearchNodes(keyword string) ([]SearchResult, error) {
	db, err := a.getDB()
	if err != nil {
		return nil, err
	}
	kw := strings.TrimSpace(keyword)
	if kw == "" {
		return []SearchResult{}, nil
	}
	escaped := strings.NewReplacer(`\`, `\\`, `%`, `\%`, `_`, `\_`).Replace(kw)
	pattern := "%" + escaped + "%"

	rows, err := db.Query(`
		SELECT id, COALESCE(parent_id, 0), title, content
		FROM nodes
		WHERE title LIKE ? ESCAPE '\' OR content LIKE ? ESCAPE '\'
		ORDER BY updated_at DESC
		LIMIT 50
	`, pattern, pattern)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var results []SearchResult
	for rows.Next() {
		var r SearchResult
		var content string
		if err := rows.Scan(&r.ID, &r.ParentID, &r.Title, &content); err != nil {
			return nil, err
		}
		r.Snippet = makeSnippet(content, kw)
		results = append(results, r)
	}
	return results, rows.Err()
}

func makeSnippet(content, kw string) string {
	clean := strings.ReplaceAll(strings.TrimSpace(content), "\r\n", " ")
	clean = strings.ReplaceAll(clean, "\n", " ")
	if clean == "" {
		return ""
	}
	runes := []rune(clean)
	lowerRunes := []rune(strings.ToLower(clean))
	lowerKw := []rune(strings.ToLower(kw))

	idx := -1
	if len(lowerKw) > 0 && len(lowerRunes) >= len(lowerKw) {
		for i := 0; i <= len(lowerRunes)-len(lowerKw); i++ {
			match := true
			for j := 0; j < len(lowerKw); j++ {
				if lowerRunes[i+j] != lowerKw[j] {
					match = false
					break
				}
			}
			if match {
				idx = i
				break
			}
		}
	}
	if idx < 0 {
		if len(runes) > 70 {
			return string(runes[:70]) + "..."
		}
		return clean
	}
	start := idx - 20
	if start < 0 {
		start = 0
	}
	end := idx + len(lowerKw) + 45
	if end > len(runes) {
		end = len(runes)
	}
	prefix := ""
	suffix := ""
	if start > 0 {
		prefix = "..."
	}
	if end < len(runes) {
		suffix = "..."
	}
	return prefix + string(runes[start:end]) + suffix
}

func (a *App) ExportMarkdown() (string, error) {
	db, err := a.getDB()
	if err != nil {
		return "", err
	}

	defaultName := strings.TrimSuffix(filepath.Base(a.dbPath), ".llist") + ".md"
	savePath, err := wailsrt.SaveFileDialog(a.ctx, wailsrt.SaveDialogOptions{
		Title:                "导出为 Markdown 文件（保留 files/ 相对引用）",
		DefaultDirectory:     filepath.Dir(a.dbPath),
		DefaultFilename:      defaultName,
		CanCreateDirectories: true,
		Filters: []wailsrt.FileFilter{
			{DisplayName: "Markdown 文件 (*.md)", Pattern: "*.md"},
		},
	})
	if err != nil || savePath == "" {
		return "", err
	}
	if !strings.HasSuffix(strings.ToLower(savePath), ".md") {
		savePath += ".md"
	}

	type fullNode struct {
		ID       int64
		ParentID int64
		Title    string
		Content  string
	}
	rows, err := db.Query(`SELECT id, COALESCE(parent_id, 0), title, content FROM nodes ORDER BY sort_order ASC, id ASC`)
	if err != nil {
		return "", err
	}
	defer rows.Close()

	childrenMap := make(map[int64][]fullNode)
	for rows.Next() {
		var fn fullNode
		if err := rows.Scan(&fn.ID, &fn.ParentID, &fn.Title, &fn.Content); err != nil {
			return "", err
		}
		childrenMap[fn.ParentID] = append(childrenMap[fn.ParentID], fn)
	}

	var sb strings.Builder
	var writeBranch func(parentID int64, depth int)
	writeBranch = func(parentID int64, depth int) {
		for _, n := range childrenMap[parentID] {
			level := depth + 1
			if level > 6 {
				level = 6
			}
			sb.WriteString(strings.Repeat("#", level) + " " + n.Title + "\n\n")
			if strings.TrimSpace(n.Content) != "" {
				sb.WriteString(strings.TrimSpace(n.Content) + "\n\n")
			}
			writeBranch(n.ID, depth+1)
		}
	}
	writeBranch(0, 0)

	if err := os.WriteFile(savePath, []byte(sb.String()), 0o644); err != nil {
		return "", err
	}
	return savePath, nil
}

func main() {
	app := NewApp()

	assetsFS, err := fs.Sub(rawAssets, "frontend")
	if err != nil {
		panic(err)
	}

	var winUserDataPath string
	if goruntime.GOOS == "windows" {
		base := getAppBaseDir()
		if isDirWritable(base) {
			winUserDataPath = filepath.Join(base, ".litelist_webview")
		}
	}

	err = wails.Run(&options.App{
		Title:     "LiteList",
		Width:     1180,
		Height:    760,
		MinWidth:  720,
		MinHeight: 480,
		AssetServer: &assetserver.Options{
			Assets:  assetsFS,
			Handler: app.filesHandler(),
		},
		DragAndDrop: &options.DragAndDrop{
			EnableFileDrop:     true,
			DisableWebViewDrop: true,
		},
		BackgroundColour: &options.RGBA{R: 255, G: 255, B: 255, A: 255},
		OnStartup:        app.startup,
		OnBeforeClose:    app.beforeClose,
		OnShutdown:       app.shutdown,
		Bind: []interface{}{
			app,
		},
		Windows: &windows.Options{
			WebviewIsTransparent: false,
			WindowIsTranslucent:  false,
			WebviewUserDataPath:  winUserDataPath,
		},
		Mac: &mac.Options{
			WebviewIsTransparent: false,
			WindowIsTranslucent:  false,
			About: &mac.AboutInfo{
				Title:   "LiteList",
				Message: "极简绿色单文件大纲笔记",
			},
		},
	})
	if err != nil {
		println("Error:", err.Error())
	}
}

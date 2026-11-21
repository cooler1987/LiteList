package main

import (
	"context"
	"database/sql"
	"embed"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	goruntime "runtime"
	"strings"
	"sync"

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
)

// Node 大纲树节点（不含正文以提升全量加载速度）
type Node struct {
	ID         int64   `json:"id"`
	ParentID   int64   `json:"parentId"`
	Title      string  `json:"title"`
	SortOrder  float64 `json:"sortOrder"`
	IsExpanded bool    `json:"isExpanded"`
	UpdatedAt  string  `json:"updatedAt"`
}

// NodeContent 节点详情
type NodeContent struct {
	ID        int64  `json:"id"`
	Title     string `json:"title"`
	Content   string `json:"content"`
	UpdatedAt string `json:"updatedAt"`
}

// SearchResult 搜索结果
type SearchResult struct {
	ID       int64  `json:"id"`
	ParentID int64  `json:"parentId"`
	Title    string `json:"title"`
	Snippet  string `json:"snippet"`
}

// AppInfo 当前应用与数据库信息
type AppInfo struct {
	DBPath   string `json:"dbPath"`
	DBName   string `json:"dbName"`
	Platform string `json:"platform"`
}

// App 核心业务结构体
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

// getAppBaseDir 获取绿色便携基准目录（兼容 macOS .app 包内路径与 go run 临时路径）
func getAppBaseDir() string {
	exe, err := os.Executable()
	if err != nil {
		wd, _ := os.Getwd()
		return wd
	}
	// 如果是在 go run / 调试临时目录运行，优先使用工作目录
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

// isDirWritable 检测目录是否可写（防止放在 Program Files 或 /Applications 时崩溃）
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
	// 1. 优先检查命令行传入的文件路径（支持系统文件关联双击打开）
	if len(os.Args) > 1 {
		arg := strings.TrimSpace(os.Args[1])
		if arg != "" && !strings.HasPrefix(arg, "-") {
			if abs, err := filepath.Abs(arg); err == nil {
				return abs
			}
		}
	}
	// 2. 绿色便携默认同目录 notes.llist
	base := getAppBaseDir()
	if isDirWritable(base) {
		return filepath.Join(base, defaultDBFileName)
	}
	// 3. 若程序所在目录只读，回退到用户主目录
	home, err := os.UserHomeDir()
	if err == nil {
		return filepath.Join(home, defaultDBFileName)
	}
	return defaultDBFileName
}

func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	target := resolveDefaultDBPath()
	if err := a.OpenDB(target); err != nil {
		// 若命令行文件损坏或打不开，回退到本地默认库
		fallback := filepath.Join(getAppBaseDir(), defaultDBFileName)
		if target != fallback {
			_ = a.OpenDB(fallback)
		}
	}
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

// SetDirty 由前端同步是否有未落盘的防抖编辑
func (a *App) SetDirty(dirty bool) {
	a.mu.Lock()
	a.dirty = dirty
	a.mu.Unlock()
}

// ForceQuit 前端刷盘完成后调用，安全退出
func (a *App) ForceQuit() {
	a.mu.Lock()
	a.dirty = false
	a.forceQuitting = true
	a.mu.Unlock()
	wailsrt.Quit(a.ctx)
}

// OpenExternalURL 使用系统默认浏览器打开 Markdown 中的外部链接
func (a *App) OpenExternalURL(url string) {
	if strings.HasPrefix(url, "http://") || strings.HasPrefix(url, "https://") {
		wailsrt.BrowserOpenURL(a.ctx, url)
	}
}

// GetAppInfo 返回当前打开的笔记库路径与平台信息
func (a *App) GetAppInfo() AppInfo {
	a.mu.Lock()
	defer a.mu.Unlock()
	name := filepath.Base(a.dbPath)
	if a.dbPath == "" {
		name = "未打开"
	}
	return AppInfo{
		DBPath:   a.dbPath,
		DBName:   name,
		Platform: goruntime.GOOS,
	}
}

// OpenDB 打开或初始化指定路径的 .llist 数据库
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
	// 单连接池彻底避免 modernc.org/sqlite 并发锁冲突
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
	// 严格单文件模式：禁用 WAL，防止产生 -wal/-shm 临时文件影响单文件拷贝或网盘同步
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

	// 检查 schema 版本（同时校验是否为合法的 SQLite 文件）
	var version int
	if err := db.QueryRow("PRAGMA user_version;").Scan(&version); err != nil {
		return fmt.Errorf("文件不是有效的 SQLite 笔记库: %w", err)
	}

	if version == 0 {
		// 检验如果是非空老文件，是否意外选中了其他软件的数据库
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
	res, err := db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (NULL, ?, ?, 1000.0, 1)`,
		"欢迎使用 LiteList",
		"# LiteList 极简大纲笔记\n\n这是一个纯绿色、单文件（`.llist`）的跨平台大纲笔记本。\n\n## 核心快捷键\n- `Ctrl+K`：全局快速搜索并跳转节点\n- `Ctrl+E`（或双击预览区）：切换 Markdown 编辑 / 预览\n- `Enter`（树聚焦时）：在下方快速新建同级节点\n- `Tab` / `Shift+Tab`（树聚焦时）：调整节点缩进层级\n- `F2`：重命名当前节点\n\n## 交互待办示例\n- [x] 单文件 SQLite 便携存储\n- [ ] 预览模式下直接点击我试试勾选！",
	)
	if err != nil {
		return
	}
	rootID, _ := res.LastInsertId()
	_, _ = db.Exec(
		`INSERT INTO nodes (parent_id, title, content, sort_order, is_expanded) VALUES (?, ?, ?, 1000.0, 1)`,
		rootID,
		"使用技巧与拖拽排序",
		"1. 左侧大纲支持直接**鼠标拖拽**调整顺序和归属层级。\n2. 编辑区内容支持 **500ms 防抖自动保存**，切换节点或关闭窗口会自动刷盘不丢字。\n3. 顶部工具栏支持一键**导出整库为 Markdown**。",
	)
}

// SelectAndOpenDB 弹出系统文件选择框并打开 .llist
func (a *App) SelectAndOpenDB() (string, error) {
	path, err := wailsrt.OpenFileDialog(a.ctx, wailsrt.OpenDialogOptions{
		Title:            "打开 LiteList 笔记库",
		DefaultDirectory: filepath.Dir(a.dbPath),
		Filters: []wailsrt.FileFilter{
			{DisplayName: "LiteList 笔记库 (*.llist)", Pattern: "*.llist"},
			{DisplayName: "所有文件 (*.*)", Pattern: "*.*"},
		},
	})
	if err != nil {
		return "", err
	}
	if path == "" {
		return "", nil
	}
	if err := a.OpenDB(path); err != nil {
		return "", err
	}
	return path, nil
}

// CreateNewDB 弹出保存框并新建空白 .llist 笔记库
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
	if err != nil {
		return "", err
	}
	if path == "" {
		return "", nil
	}
	if !strings.HasSuffix(strings.ToLower(path), ".llist") {
		path += ".llist"
	}
	// 新建时若同名存在，由系统弹窗确认覆盖，这里先清理确保干净初始化
	_ = os.Remove(path)
	if err := a.OpenDB(path); err != nil {
		return "", err
	}
	return path, nil
}

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

// GetTree 一次性获取全部节点骨架（不加载 content 正文）
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

// GetNodeContent 异步获取指定节点正文
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

// CreateNode 在指定父节点末尾创建新节点
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

	// 确保父节点处于展开状态
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

// CreateNodeAt 指定父节点与精确排序权重创建节点（用于 Enter 快速在下方插入同级）
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

// RenameNode 仅重命名标题（安全隔离，绝不影响正文）
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

// UpdateNodeContent 仅更新正文（安全隔离）
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

// UpdateNode 同时更新标题与正文
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

// SetNodeExpanded 持久化展开/折叠状态
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

// MoveNode 移动节点位置与层级（内置环路检测 + 权重精度自愈）
func (a *App) MoveNode(id int64, newParentID int64, newSortOrder float64) error {
	db, err := a.getDB()
	if err != nil {
		return err
	}
	if id == newParentID {
		return errors.New("不能将节点移动到自身内部")
	}

	// 环路检测：确保 newParentID 不是 id 的子孙节点
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

// maybeRebalance 当同级节点排序权重差值过小时自动按 1000 步长重排
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

// DeleteNode 级联删除节点及其所有子孙节点，返回总删除数量
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

// SearchNodes 检索标题与正文包含关键词的节点并生成摘要
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

// ExportMarkdown 将整棵树导出为单个结构化 Markdown 文件
func (a *App) ExportMarkdown() (string, error) {
	db, err := a.getDB()
	if err != nil {
		return "", err
	}

	defaultName := strings.TrimSuffix(filepath.Base(a.dbPath), ".llist") + ".md"
	savePath, err := wailsrt.SaveFileDialog(a.ctx, wailsrt.SaveDialogOptions{
		Title:                "导出为 Markdown 文件",
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
		ID        int64
		ParentID  int64
		Title     string
		Content   string
		SortOrder float64
	}
	rows, err := db.Query(`SELECT id, COALESCE(parent_id, 0), title, content, sort_order FROM nodes ORDER BY sort_order ASC, id ASC`)
	if err != nil {
		return "", err
	}
	defer rows.Close()

	childrenMap := make(map[int64][]fullNode)
	for rows.Next() {
		var fn fullNode
		if err := rows.Scan(&fn.ID, &fn.ParentID, &fn.Title, &fn.Content, &fn.SortOrder); err != nil {
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

	// Windows 便携化：若程序目录可写，将 WebView2 用户数据放在同目录隐藏缓存中
	var winUserDataPath string
	if goruntime.GOOS == "windows" {
		base := getAppBaseDir()
		if isDirWritable(base) {
			winUserDataPath = filepath.Join(base, ".litelist_webview")
		}
	}

	err = wails.Run(&options.App{
		Title:     "LiteList",
		Width:     1080,
		Height:    700,
		MinWidth:  680,
		MinHeight: 460,
		AssetServer: &assetserver.Options{
			Assets: assetsFS,
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

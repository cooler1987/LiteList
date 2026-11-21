import { EditorState } from "@codemirror/state";
import {
  EditorView,
  keymap,
  lineNumbers,
  highlightActiveLineGutter,
  highlightSpecialChars,
  drawSelection,
  dropCursor,
  rectangularSelection,
  crosshairCursor,
  highlightActiveLine,
} from "@codemirror/view";
import {
  defaultKeymap,
  history,
  historyKeymap,
  indentMore,
  indentLess,
  undo,
  redo,
  selectAll,
} from "@codemirror/commands";
import {
  foldGutter,
  indentOnInput,
  syntaxHighlighting,
  HighlightStyle,
  bracketMatching,
  foldKeymap,
  foldService,
  foldAll,
  unfoldAll,
} from "@codemirror/language";
import {
  markdown,
  markdownLanguage,
  insertNewlineContinueMarkup,
} from "@codemirror/lang-markdown";
import {
  search,
  searchKeymap,
  openSearchPanel,
  highlightSelectionMatches,
} from "@codemirror/search";
import { closeBrackets, closeBracketsKeymap } from "@codemirror/autocomplete";
import { tags } from "@lezer/highlight";

import { javascript } from "@codemirror/lang-javascript";
import { python } from "@codemirror/lang-python";
import { go } from "@codemirror/lang-go";
import { sql } from "@codemirror/lang-sql";
import { json } from "@codemirror/lang-json";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { cpp } from "@codemirror/lang-cpp";
import { java } from "@codemirror/lang-java";
import { rust } from "@codemirror/lang-rust";
import { xml } from "@codemirror/lang-xml";
import { yaml } from "@codemirror/lang-yaml";

import hljs from "highlight.js/lib/common";

// 代码块语言映射（离线内置 15+ 高频语言，保持 bundle 轻量）
const langCache = new Map();
function getCodeLanguage(info) {
  const key = (info || "").trim().toLowerCase().split(/\s+/)[0];
  if (!key) return null;
  if (langCache.has(key)) return langCache.get(key);

  let support = null;
  if (["js", "javascript", "mjs", "cjs"].includes(key)) {
    support = javascript({ jsx: true });
  } else if (["ts", "typescript", "tsx"].includes(key)) {
    support = javascript({ typescript: true, jsx: true });
  } else if (["py", "python"].includes(key)) {
    support = python();
  } else if (["go", "golang"].includes(key)) {
    support = go();
  } else if (["sql", "mysql", "postgres", "sqlite"].includes(key)) {
    support = sql();
  } else if (["json", "jsonc"].includes(key)) {
    support = json();
  } else if (["html", "htm", "vue", "svelte"].includes(key)) {
    support = html();
  } else if (["css", "scss", "less"].includes(key)) {
    support = css();
  } else if (["c", "cpp", "c++", "h", "hpp"].includes(key)) {
    support = cpp();
  } else if (key === "java") {
    support = java();
  } else if (["rs", "rust"].includes(key)) {
    support = rust();
  } else if (["xml", "svg"].includes(key)) {
    support = xml();
  } else if (["yml", "yaml"].includes(key)) {
    support = yaml();
  }

  const lang = support ? support.language : null;
  langCache.set(key, lang);
  return lang;
}

// 扩展折叠服务：支持“缩进列表主从层级折叠”（标题 #..###### 与代码块已由 lang-markdown 原生支持）
const listIndentFold = foldService.of((state, lineStart, lineEnd) => {
  const line = state.doc.lineAt(lineStart);
  const m = line.text.match(/^(\s*)(?:[-*+]|\d+\.)\s/);
  if (!m) return null;

  const baseIndent = m[1].length;
  let lastChildLine = null;
  for (let i = line.number + 1; i <= state.doc.lines; i++) {
    const nextLine = state.doc.line(i);
    if (nextLine.text.trim() === "") continue;
    const nextIndent = nextLine.text.match(/^(\s*)/)[1].length;
    if (nextIndent > baseIndent) {
      lastChildLine = nextLine;
    } else {
      break;
    }
  }
  if (lastChildLine && lastChildLine.to > lineEnd) {
    return { from: lineEnd, to: lastChildLine.to };
  }
  return null;
});

// 基于 CSS 变量的自适应语法高亮（自动跟随系统明暗主题）
const adaptiveHighlight = HighlightStyle.define([
  { tag: tags.heading1, fontWeight: "700", fontSize: "1.35em", color: "var(--hl-heading)" },
  { tag: tags.heading2, fontWeight: "700", fontSize: "1.2em", color: "var(--hl-heading)" },
  { tag: tags.heading3, fontWeight: "700", fontSize: "1.1em", color: "var(--hl-heading)" },
  { tag: [tags.heading4, tags.heading5, tags.heading6], fontWeight: "700", color: "var(--hl-heading)" },
  { tag: tags.strong, fontWeight: "700", color: "var(--hl-strong)" },
  { tag: tags.emphasis, fontStyle: "italic", color: "var(--hl-em)" },
  { tag: tags.strikethrough, textDecoration: "line-through", color: "var(--text-muted)" },
  { tag: tags.link, color: "var(--accent)", textDecoration: "underline" },
  { tag: tags.url, color: "var(--accent)" },
  { tag: tags.quote, color: "var(--text-muted)", fontStyle: "italic" },
  { tag: tags.monospace, fontFamily: "var(--font-mono)", color: "var(--hl-code)" },
  { tag: tags.processingInstruction, color: "var(--text-muted)" },
  { tag: tags.keyword, color: "var(--hl-kw)", fontWeight: "600" },
  { tag: [tags.string, tags.special(tags.string)], color: "var(--hl-str)" },
  { tag: [tags.number, tags.bool, tags.null], color: "var(--hl-num)" },
  { tag: [tags.comment, tags.meta], color: "var(--text-muted)", fontStyle: "italic" },
  { tag: [tags.function(tags.variableName), tags.propertyName], color: "var(--hl-fn)" },
  { tag: [tags.typeName, tags.className, tags.tagName], color: "var(--hl-type)" },
  { tag: tags.attributeName, color: "var(--hl-attr)" },
  { tag: tags.operator, color: "var(--hl-op)" },
]);

const adaptiveTheme = EditorView.theme({
  "&": {
    height: "100%",
    width: "100%",
    backgroundColor: "var(--bg)",
    color: "var(--text)",
    fontSize: "14px",
  },
  ".cm-scroller": {
    fontFamily: "var(--font-mono)",
    lineHeight: "1.7",
    padding: "12px 0 48px",
    overflow: "auto",
  },
  ".cm-content": {
    padding: "0 16px 0 8px",
    caretColor: "var(--accent)",
  },
  ".cm-gutters": {
    backgroundColor: "var(--bg-sidebar)",
    color: "var(--text-muted)",
    borderRight: "1px solid var(--border)",
    minWidth: "46px",
    userSelect: "none",
  },
  ".cm-lineNumbers .cm-gutterElement": {
    padding: "0 6px 0 8px",
    fontSize: "12px",
  },
  ".cm-foldGutter .cm-gutterElement": {
    padding: "0 5px 0 2px",
    cursor: "pointer",
    fontSize: "12px",
    color: "var(--text-muted)",
  },
  ".cm-activeLine": { backgroundColor: "var(--cm-active-line)" },
  ".cm-activeLineGutter": { backgroundColor: "var(--cm-active-gutter)", color: "var(--text)" },
  ".cm-selectionBackground, ::selection": { backgroundColor: "var(--cm-selection) !important" },
  ".cm-searchMatch": { backgroundColor: "rgba(234, 179, 8, 0.3)", outline: "1px solid rgba(234, 179, 8, 0.6)" },
  ".cm-searchMatch.cm-searchMatch-selected": { backgroundColor: "rgba(37, 99, 235, 0.35)" },
  ".cm-panels": {
    backgroundColor: "var(--bg-sidebar)",
    color: "var(--text)",
    borderBottom: "1px solid var(--border)",
  },
  ".cm-panel.cm-search": {
    padding: "8px 12px",
    fontSize: "12px",
  },
  ".cm-panel.cm-search input, .cm-panel.cm-search button": {
    backgroundColor: "var(--bg)",
    color: "var(--text)",
    border: "1px solid var(--border)",
    borderRadius: "4px",
    padding: "3px 7px",
    fontSize: "12px",
  },
  ".cm-panel.cm-search button:hover": {
    backgroundColor: "var(--bg-hover)",
  },
  ".cm-foldPlaceholder": {
    backgroundColor: "var(--bg-code)",
    border: "1px solid var(--border)",
    color: "var(--text-muted)",
    borderRadius: "4px",
    padding: "0 6px",
    margin: "0 4px",
    fontSize: "12px",
  },
});

export function createMarkdownEditor(container, handlers) {
  let suppressChange = false;

  const customKeymap = keymap.of([
    { key: "Tab", run: indentMore, shift: indentLess },
    { key: "Enter", run: insertNewlineContinueMarkup },
    {
      key: "Mod-b",
      run: (v) => {
        wrapSelection(v, "**", "**", "加粗文本");
        return true;
      },
    },
    {
      key: "Mod-i",
      run: (v) => {
        wrapSelection(v, "*", "*", "斜体文本");
        return true;
      },
    },
    {
      key: "Mod-`",
      run: (v) => {
        wrapSelection(v, "`", "`", "code");
        return true;
      },
    },
    {
      key: "Mod-l",
      run: (v) => {
        insertLink(v);
        return true;
      },
    },
    {
      key: "Mod-s",
      run: () => {
        if (handlers.onSave) handlers.onSave();
        return true;
      },
    },
  ]);

  const extensions = [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter({
      openText: "▾",
      closedText: "▸",
    }),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    bracketMatching(),
    closeBrackets(),
    rectangularSelection(),
    crosshairCursor(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    search({ top: true }),
    EditorView.lineWrapping,
    markdown({
      base: markdownLanguage,
      codeLanguages: getCodeLanguage,
    }),
    listIndentFold,
    syntaxHighlighting(adaptiveHighlight),
    adaptiveTheme,
    customKeymap,
    keymap.of([
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...foldKeymap,
    ]),
    EditorView.updateListener.of((update) => {
      if (update.docChanged && !suppressChange && handlers.onChange) {
        handlers.onChange(update.state.doc.toString());
      }
      if ((update.docChanged || update.selectionSet) && handlers.onCursorChange) {
        handlers.onCursorChange(getCursorInfo(update.view));
      }
    }),
    EditorView.domEventHandlers({
      paste: (event, view) => {
        if (handlers.onPaste) return handlers.onPaste(event, view);
        return false;
      },
      contextmenu: (event, view) => {
        if (handlers.onContextMenu) {
          event.preventDefault();
          handlers.onContextMenu(event, view);
          return true;
        }
        return false;
      },
      blur: () => {
        if (handlers.onBlur) handlers.onBlur();
      },
    }),
  ];

  const view = new EditorView({
    state: EditorState.create({ doc: "", extensions }),
    parent: container,
  });

  function wrapSelection(v, before, after, placeholder) {
    const sel = v.state.selection.main;
    const selected = v.state.doc.sliceString(sel.from, sel.to);
    const inner = selected || placeholder;
    const text = before + inner + after;
    v.dispatch({
      changes: { from: sel.from, to: sel.to, insert: text },
      selection: {
        anchor: sel.from + before.length,
        head: sel.from + before.length + inner.length,
      },
    });
    v.focus();
  }

  function insertLink(v) {
    const sel = v.state.selection.main;
    const selected = v.state.doc.sliceString(sel.from, sel.to) || "链接文字";
    const text = `[${selected}](https://)`;
    v.dispatch({
      changes: { from: sel.from, to: sel.to, insert: text },
      selection: {
        anchor: sel.from + selected.length + 3,
        head: sel.from + text.length - 1,
      },
    });
    v.focus();
  }

  function getCursorInfo(v) {
    const sel = v.state.selection.main;
    const line = v.state.doc.lineAt(sel.head);
    return {
      line: line.number,
      col: sel.head - line.from + 1,
      totalLines: v.state.doc.lines,
      length: v.state.doc.length,
    };
  }

  return {
    view,
    getValue: () => view.state.doc.toString(),
    setValue: (text, resetHistory = false) => {
      suppressChange = true;
      try {
        if (resetHistory) {
          view.setState(EditorState.create({ doc: text || "", extensions }));
        } else {
          view.dispatch({
            changes: { from: 0, to: view.state.doc.length, insert: text || "" },
          });
        }
      } finally {
        suppressChange = false;
      }
      if (handlers.onCursorChange) handlers.onCursorChange(getCursorInfo(view));
    },
    insertText: (text) => {
      const sel = view.state.selection.main;
      view.dispatch({
        changes: { from: sel.from, to: sel.to, insert: text },
        selection: { anchor: sel.from + text.length },
      });
      view.focus();
    },
    replaceSelection: (text) => {
      const sel = view.state.selection.main;
      view.dispatch({
        changes: { from: sel.from, to: sel.to, insert: text },
        selection: { anchor: sel.from, head: sel.from + text.length },
      });
      view.focus();
    },
    getSelectedText: () => {
      const sel = view.state.selection.main;
      return view.state.doc.sliceString(sel.from, sel.to);
    },
    replaceLine: (lineNumber, newLineText) => {
      if (lineNumber < 1 || lineNumber > view.state.doc.lines) return;
      const line = view.state.doc.line(lineNumber);
      view.dispatch({
        changes: { from: line.from, to: line.to, insert: newLineText },
      });
    },
    toggleLinePrefix: (prefix) => {
      const sel = view.state.selection.main;
      const line = view.state.doc.lineAt(sel.head);
      if (line.text.startsWith(prefix)) {
        view.dispatch({
          changes: { from: line.from, to: line.from + prefix.length, insert: "" },
        });
      } else {
        view.dispatch({
          changes: { from: line.from, to: line.from, insert: prefix },
        });
      }
      view.focus();
    },
    cycleHeading: () => {
      const sel = view.state.selection.main;
      const line = view.state.doc.lineAt(sel.head);
      const m = line.text.match(/^(#{1,6})\s+/);
      let next = "# ";
      let removeLen = 0;
      if (m) {
        removeLen = m[0].length;
        const lvl = m[1].length;
        next = lvl >= 4 ? "" : "#".repeat(lvl + 1) + " ";
      }
      view.dispatch({
        changes: { from: line.from, to: line.from + removeLen, insert: next },
      });
      view.focus();
    },
    wrap: (before, after, placeholder) => wrapSelection(view, before, after, placeholder),
    insertLink: () => insertLink(view),
    undo: () => { undo(view); view.focus(); },
    redo: () => { redo(view); view.focus(); },
    selectAll: () => { selectAll(view); view.focus(); },
    openSearch: () => { openSearchPanel(view); },
    foldAll: () => { foldAll(view); view.focus(); },
    unfoldAll: () => { unfoldAll(view); view.focus(); },
    focus: () => view.focus(),
    getCursorInfo: () => getCursorInfo(view),
  };
}

export { hljs };

/**
 * Markdown rendering.
 *
 * Mirrors what the C# build supports, including the Luogu extensions:
 *
 *   * headings, emphasis, strikethrough, `==mark==`, H~2~O, x^2^, inline code
 *   * fenced code with language colouring, `line-numbers` and `lines=6-9`
 *   * ordered/unordered/nested lists, `- [x]` task lists
 *   * tables with `:---` alignment, `^` merge-up / `<` merge-left, `::cute-table{tuack}`
 *   * blockquotes, GitHub `> [!NOTE]` alerts, horizontal rules, links, bare URLs
 *   * footnotes, definition lists, `:smile:` emoji, `:::容器`, epigraphs
 *   * LaTeX via `$…$` / `\(…\)` inline and `$$…$$` / `\[…\]` display
 *
 * Raw HTML is never parsed — same choice Luogu makes, so `<div>` shows as text.
 */

import MarkdownIt from 'markdown-it'
import footnote from 'markdown-it-footnote'
import taskLists from 'markdown-it-task-lists'
import deflist from 'markdown-it-deflist'
import { full as emoji } from 'markdown-it-emoji'
import sub from 'markdown-it-sub'
import sup from 'markdown-it-sup'
import mark from 'markdown-it-mark'
import ins from 'markdown-it-ins'
import hljs from 'highlight.js'
import katex from 'katex'

/** Above this length a message is shown verbatim, to keep scrolling smooth. */
const PLAIN_TEXT_LIMIT = 20000

const ESCAPE_MAP = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }

function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, (c) => ESCAPE_MAP[c])
}

// ---------------------------------------------------------------------------
// Maths
// ---------------------------------------------------------------------------

function renderMath(src, displayMode) {
  try {
    return katex.renderToString(src, {
      displayMode,
      throwOnError: false,
      errorColor: 'inherit',
      strict: false,
      trust: false,
      output: 'html',
      macros: { '\\RR': '\\mathbb{R}', '\\NN': '\\mathbb{N}', '\\ZZ': '\\mathbb{Z}' },
    })
  } catch {
    return null
  }
}

/** `$…$` and `\(…\)`. Intraword/currency `$` is left alone. */
function mathInlineRule(state, silent) {
  const start = state.pos
  const src = state.src
  const marker = src[start]

  let open, close, inner
  if (marker === '$') {
    // `$$` belongs to the block rule.
    if (src[start + 1] === '$') return false
    open = 1
    close = '$'
  } else if (marker === '\\' && src[start + 1] === '(') {
    open = 2
    close = '\\)'
  } else {
    return false
  }

  const contentStart = start + open
  // No space directly after the opening delimiter: rules out "$ 5 and $ 6".
  if (src[contentStart] === ' ' || src[contentStart] === '\n') return false

  let end = -1
  for (let i = contentStart; i < src.length; i++) {
    if (src[i] === '\\' && close === '$') {
      i += 1
      continue
    }
    if (close === '$') {
      if (src[i] === '$') {
        // A closing `$` must not be preceded by a space nor followed by a digit.
        if (src[i - 1] === ' ' || /\d/.test(src[i + 1] ?? '')) continue
        end = i
        break
      }
    } else if (src.startsWith('\\)', i)) {
      end = i
      break
    }
  }
  if (end === -1) return false

  inner = src.slice(contentStart, end)
  if (!inner.trim()) return false

  if (silent) return true

  const rendered = renderMath(inner, false)
  const token = state.push('math_inline', 'math', 0)
  token.content = inner
  token.markup = close === '$' ? '$' : '\\)'
  token.meta = { html: rendered }
  state.pos = end + close.length
  return true
}

/** `$$…$$` and `\[…\]`, on their own lines. */
function mathBlockRule(state, startLine, endLine, silent) {
  const start = state.bMarks[startLine] + state.tShift[startLine]
  const max = state.eMarks[startLine]
  const line = state.src.slice(start, max)

  let closer = null
  let first = null
  if (line.startsWith('$$')) {
    closer = '$$'
    first = line.slice(2)
  } else if (line.startsWith('\\[')) {
    closer = '\\]'
    first = line.slice(2)
  } else {
    return false
  }
  if (silent) return true

  // Single-line form: `$$x^2$$`
  const firstTrimmed = first.trim()
  if (firstTrimmed.endsWith(closer) && firstTrimmed.length > closer.length) {
    const body = firstTrimmed.slice(0, firstTrimmed.length - closer.length)
    const token = state.push('math_block', 'math', 0)
    token.content = body
    token.markup = closer
    token.meta = { html: renderMath(body, true) }
    token.map = [startLine, startLine + 1]
    state.line = startLine + 1
    return true
  }

  const body = []
  if (firstTrimmed) body.push(firstTrimmed)
  let nextLine = startLine + 1
  let closed = false
  for (; nextLine < endLine; nextLine++) {
    const s = state.bMarks[nextLine] + state.tShift[nextLine]
    const e = state.eMarks[nextLine]
    const text = state.src.slice(s, e)
    const idx = text.indexOf(closer)
    if (idx !== -1) {
      const tail = text.slice(0, idx).trim()
      if (tail) body.push(tail)
      closed = true
      break
    }
    body.push(text)
  }
  if (!closed) return false

  const source = body.join('\n').trim()
  const token = state.push('math_block', 'math', 0)
  token.content = source
  token.markup = closer
  token.meta = { html: source ? renderMath(source, true) : null }
  token.map = [startLine, nextLine + 1]
  state.line = nextLine + 1
  return true
}

// ---------------------------------------------------------------------------
// Luogu `:::` containers
// ---------------------------------------------------------------------------

const CONTAINER_TYPES = ['info', 'success', 'warning', 'error', 'align', 'epigraph']

const CONTAINER_ICONS = {
  info: 'ℹ',
  success: '✔',
  warning: '⚠',
  error: '✖',
  align: '',
  epigraph: '',
}

/**
 * `:::info[标题]`, `:::info[标题]{open}`, `:::align{center}`, `:::epigraph[——作者]`.
 *
 * The closing `:::` is located by scanning forward with a depth counter, so
 * containers nest the way Luogu's do. The inner region is then tokenized with
 * `lineMax` clamped to the closing marker, which stops the block parser from
 * swallowing the rest of the message.
 */
function containerPlugin(md) {
  const OPEN_RE = /^:::(info|success|warning|error|align|epigraph)\b/
  const CLOSE_RE = /^:::\s*$/

  function makeRule(type) {
    return function rule(state, startLine, endLine, silent) {
      const start = state.bMarks[startLine] + state.tShift[startLine]
      const max = state.eMarks[startLine]
      const line = state.src.slice(start, max)

      const marker = `:::${type}`
      if (!line.startsWith(marker)) return false
      const rest = line.slice(marker.length)
      // `:::informal` must not be treated as `:::info`.
      if (rest && !/^[\s[{]/.test(rest)) return false
      if (silent) return true

      // Find the matching close, tracking nested containers of any type.
      let nextLine = startLine + 1
      let depth = 1
      let haveEndMarker = false
      for (; nextLine < endLine; nextLine++) {
        const s = state.bMarks[nextLine] + state.tShift[nextLine]
        const e = state.eMarks[nextLine]
        const text = state.src.slice(s, e)
        if (CLOSE_RE.test(text)) {
          depth -= 1
          if (depth === 0) {
            haveEndMarker = true
            break
          }
          continue
        }
        if (OPEN_RE.test(text)) depth += 1
      }

      let title = ''
      let open = false
      let arg = ''
      const bracket = /\[([^\]]*)\]/.exec(rest)
      if (bracket) title = bracket[1]
      const brace = /\{([^}]*)\}/.exec(rest)
      if (brace) arg = brace[1].trim()
      if (arg === 'open') open = true

      const openToken = state.push(`container_${type}_open`, 'div', 1)
      openToken.block = true
      openToken.info = type
      openToken.meta = { title, arg, open }
      openToken.map = [startLine, 0]

      const previousParent = state.parentType
      const previousLineMax = state.lineMax
      state.parentType = 'container'
      state.lineMax = nextLine
      state.md.block.tokenize(state, startLine + 1, nextLine)
      state.parentType = previousParent
      state.lineMax = previousLineMax

      state.line = nextLine + (haveEndMarker ? 1 : 0)
      openToken.map[1] = state.line

      const closeToken = state.push(`container_${type}_close`, 'div', -1)
      closeToken.block = true
      closeToken.markup = ':::'
      closeToken.map = [nextLine, state.line]
      return true
    }
  }

  for (const type of CONTAINER_TYPES) {
    md.block.ruler.before('fence', `container_${type}`, makeRule(type), {
      alt: ['paragraph', 'reference', 'blockquote', 'list'],
    })
  }
}

/** Render the open token of a container, choosing class and inner wrapper. */
function renderContainerOpen(type, token) {
  const { title, arg, open } = token.meta ?? {}
  if (type === 'align') {
    const dir = ['center', 'right', 'left'].includes(arg) ? arg : 'left'
    return `<div class="align-${dir}">\n`
  }
  if (type === 'epigraph') {
    return `<div class="epigraph">\n`
  }

  const isOpen = open === true
  const label = title || defaultTitle(type)
  const icon = CONTAINER_ICONS[type] ?? ''
  return (
    `<div class="box box-${type}" data-open="${isOpen ? 'true' : 'false'}">` +
    `<div class="box-title"><span class="caret">▶</span>` +
    (icon ? `<span class="box-icon">${icon}</span>` : '') +
    `<span>${escapeHtml(label)}</span></div>` +
    `<div class="box-body">\n`
  )
}

function defaultTitle(type) {
  switch (type) {
    case 'info':
      return '提示'
    case 'success':
      return '成功'
    case 'warning':
      return '警告'
    case 'error':
      return '错误'
    default:
      return type
  }
}

function renderContainerClose(type) {
  if (type === 'align' || type === 'epigraph') return '</div>\n'
  return '</div></div>\n'
}

// ---------------------------------------------------------------------------
// Fenced code: language banner, line numbers, highlighted ranges
// ---------------------------------------------------------------------------

/**
 * Luogu fence flags: `line-numbers` adds a gutter, `lines=6-9` highlights a
 * range. Both may be combined: ```cpp line-numbers lines=6-9
 */
function parseFenceInfo(info) {
  const raw = String(info ?? '').trim()
  if (!raw) return { lang: '', lineNumbers: false, highlight: null, flags: [] }

  const parts = raw.split(/\s+/)
  let lang = ''
  let lineNumbers = false
  let highlight = null
  const flags = []

  for (const part of parts) {
    if (/^line-numbers$/i.test(part)) {
      lineNumbers = true
      flags.push(part)
      continue
    }
    const range = /^lines=(\d+)(?:-(\d+))?$/i.exec(part)
    if (range) {
      const from = Number(range[1])
      const to = range[2] ? Number(range[2]) : from
      highlight = [Math.min(from, to), Math.max(from, to)]
      flags.push(part)
      continue
    }
    if (!lang) {
      lang = part.replace(/^\{|\}$/g, '').replace(/^\./, '')
      continue
    }
    flags.push(part)
  }
  return { lang, lineNumbers, highlight, flags }
}

function highlightCode(code, lang) {
  const language = lang && hljs.getLanguage(lang) ? lang : null
  try {
    if (language) {
      return { html: hljs.highlight(code, { language, ignoreIllegals: true }).value, language }
    }
    if (lang) return { html: escapeHtml(code), language: lang }
    return { html: escapeHtml(code), language: '' }
  } catch {
    return { html: escapeHtml(code), language }
  }
}

function renderFence(tokens, idx) {
  const token = tokens[idx]
  const { lang, lineNumbers, highlight, flags } = parseFenceInfo(token.info)
  const code = token.content.replace(/\n$/, '')
  const { html } = highlightCode(code, lang)

  const lines = html.split('\n')
  const isPlain = !lang && !lineNumbers && !highlight && flags.length === 0

  // No language and no Luogu flags: emit a bare <pre><code> like vanilla markdown.
  if (isPlain) {
    return `<pre><code>${html}\n</code></pre>\n`
  }

  const inRange = (n) => highlight !== null && n >= highlight[0] && n <= highlight[1]

  // Every line becomes a block-level <span class="ln">. Mixing inline and block
  // line wrappers (the obvious shortcut for highlighting) makes the highlighted
  // rows pick up extra leading from the newlines kept by `white-space: pre`.
  const codeLines = lines
    .map((line, i) => `<span class="ln${inRange(i + 1) ? ' hl' : ''}">${line || ' '}</span>`)
    .join('')

  let body
  if (lineNumbers) {
    const gutter = lines
      .map((_, i) => `<span class="ln${inRange(i + 1) ? ' hl' : ''}">${i + 1}</span>`)
      .join('')
    body = `<div class="scroller"><div class="gutter">${gutter}</div><code>${codeLines}</code></div>`
  } else {
    body = `<div class="scroller"><code>${codeLines}</code></div>`
  }

  const label = [lang || 'text', ...flags].join(' ')
  return (
    `<div class="code-block">` +
    `<div class="banner"><span>${escapeHtml(label)}</span>` +
    `<button type="button" class="copy" data-copy-code>复制</button></div>` +
    `${body}</div>\n`
  )
}

// ---------------------------------------------------------------------------
// Tables: alignment, `^` merge-up, `<` merge-left, `::cute-table{tuack}`
// ---------------------------------------------------------------------------

function parseTableTokens(tokens, start, end) {
  const rows = []
  let current = null
  let align = []
  let inHead = false

  for (let i = start; i < end; i++) {
    const t = tokens[i]
    if (t.type === 'thead_open') inHead = true
    else if (t.type === 'thead_close') inHead = false
    else if (t.type === 'tr_open') current = { head: inHead, cells: [] }
    else if (t.type === 'tr_close') {
      if (current) rows.push(current)
      current = null
    } else if (t.type === 'th_open' || t.type === 'td_open') {
      const style = t.attrGet('style') ?? ''
      const m = /text-align:\s*(left|center|right)/.exec(style)
      const a = m ? m[1] : ''
      if (t.type === 'th_open' && align.length < 64) align.push(a)
      current?.cells.push({ align: a, content: '', raw: '' })
    } else if (t.type === 'inline' && current && current.cells.length) {
      const cell = current.cells[current.cells.length - 1]
      if (!cell.content) {
        cell.content = t.content
        cell.children = t.children
      }
    }
  }
  return { rows, align }
}

/** Apply `^` (merge up) and `<` (merge left) to a parsed table. */
function applyMerges(rows) {
  const grid = rows.map((row) =>
    row.cells.map((cell) => ({
      cell,
      rowspan: 1,
      colspan: 1,
      skip: false,
      literal: false,
    })),
  )

  for (let r = 0; r < grid.length; r++) {
    for (let c = 0; c < grid[r].length; c++) {
      const entry = grid[r][c]
      if (!entry || entry.skip) continue
      const text = String(entry.cell.content ?? '').trim()

      if (text === '^') {
        // Find the entry that physically sits above this column.
        let above = null
        for (let rr = r - 1; rr >= 0 && !above; rr--) {
          let col = 0
          for (let cc = 0; cc < grid[rr].length; cc++) {
            const cand = grid[rr][cc]
            if (!cand || cand.skip) continue
            if (col === c) {
              above = cand
              break
            }
            col += cand.colspan
          }
        }
        if (above) {
          above.rowspan += 1
          entry.skip = true
          continue
        }
        entry.literal = true
      }

      if (text === '<') {
        let left = null
        for (let cc = c - 1; cc >= 0; cc--) {
          const cand = grid[r][cc]
          if (!cand || cand.skip) continue
          left = cand
          break
        }
        if (left) {
          left.colspan += 1
          entry.skip = true
          continue
        }
        entry.literal = true
      }
    }
  }
  return grid
}

function renderCells(md, entry, tag, env) {
  const { cell } = entry
  let inner
  if (entry.literal) {
    inner = escapeHtml(String(cell.content ?? '').trim())
  } else if (cell.children && cell.children.length) {
    inner = md.renderer.renderInline(cell.children, md.options, env)
  } else {
    inner = escapeHtml(cell.content ?? '')
  }
  const attrs = []
  if (entry.rowspan > 1) attrs.push(`rowspan="${entry.rowspan}"`)
  if (entry.colspan > 1) attrs.push(`colspan="${entry.colspan}"`)
  if (cell.align) attrs.push(`style="text-align:${cell.align}"`)
  const suffix = attrs.length ? ` ${attrs.join(' ')}` : ''
  return `<${tag}${suffix}>${inner}</${tag}>`
}

function renderTable(md, tokens, start, end, env, extraClass) {
  const { rows } = parseTableTokens(tokens, start, end)
  if (rows.length === 0) return '<div class="table-wrap"></div>\n'

  const grid = applyMerges(rows)
  const headRows = []
  const bodyRows = []
  rows.forEach((row, r) => (row.head ? headRows : bodyRows).push(r))

  const renderRow = (r) => {
    const cells = grid[r]
      .map((entry) => (entry.skip ? '' : renderCells(md, entry, rows[r].head ? 'th' : 'td', env)))
      .join('')
    return `<tr>${cells}</tr>`
  }

  const head = headRows.length ? `<thead>${headRows.map(renderRow).join('')}</thead>` : ''
  const body = bodyRows.length ? `<tbody>${bodyRows.map(renderRow).join('')}</tbody>` : ''
  const cls = extraClass ? ` class="${extraClass}"` : ''
  return `<div class="table-wrap"><table${cls}>${head}${body}</table></div>\n`
}

/** Rewrite table token ranges into HTML, absorbing any `::cute-table` marker. */
function tableTransformPlugin(md) {
  md.core.ruler.push('dsh_tables', (state) => {
    const tokens = state.tokens
    const out = []
    let pendingCute = null

    for (let i = 0; i < tokens.length; i++) {
      const token = tokens[i]

      // A `::cute-table{tuack}` paragraph immediately before a table styles it.
      if (token.type === 'paragraph_open') {
        const inline = tokens[i + 1]
        const close = tokens[i + 2]
        const text = inline?.type === 'inline' ? inline.content.trim() : ''
        const m = /^::cute-table\{([^}]*)\}$/.exec(text)
        if (m && close?.type === 'paragraph_close') {
          pendingCute = m[1].trim() || 'tuack'
          i += 2
          continue
        }
      }

      if (token.type === 'table_open') {
        let depth = 1
        let j = i + 1
        for (; j < tokens.length && depth > 0; j++) {
          if (tokens[j].type === 'table_open') depth += 1
          else if (tokens[j].type === 'table_close') depth -= 1
        }
        const end = j - 1
        const html = renderTable(md, tokens, i, end, state.env, pendingCute)
        pendingCute = null
        const block = new state.Token('html_block', '', 0)
        block.content = html
        block.block = true
        out.push(block)
        i = end
        continue
      }

      // A marker not followed by a table should not vanish silently.
      if (pendingCute !== null && token.type !== 'paragraph_open') {
        if (token.type !== 'inline' && token.type !== 'paragraph_close') {
          const p = new state.Token('paragraph_open', 'p', 1)
          const inline = new state.Token('inline', '', 0)
          inline.content = `::cute-table{${pendingCute}}`
          inline.children = []
          const pClose = new state.Token('paragraph_close', 'p', -1)
          out.push(p, inline, pClose)
          pendingCute = null
        }
      }

      out.push(token)
    }

    state.tokens = out
    return true
  })
}

// ---------------------------------------------------------------------------
// GitHub-style alerts (`> [!NOTE]`)
// ---------------------------------------------------------------------------

const ALERT_TITLES = {
  note: 'Note',
  tip: 'Tip',
  important: 'Important',
  warning: 'Warning',
  caution: 'Caution',
}

function alertPlugin(md) {
  md.core.ruler.push('dsh_alerts', (state) => {
    const tokens = state.tokens
    const out = []

    for (let i = 0; i < tokens.length; i++) {
      const token = tokens[i]
      if (token.type !== 'blockquote_open') {
        out.push(token)
        continue
      }

      // Locate the matching close.
      let depth = 1
      let j = i + 1
      for (; j < tokens.length && depth > 0; j++) {
        if (tokens[j].type === 'blockquote_open') depth += 1
        else if (tokens[j].type === 'blockquote_close') depth -= 1
      }
      const end = j - 1

      const firstInline = tokens
        .slice(i + 1, end)
        .find((t) => t.type === 'inline')
      const match = firstInline ? /^\[!(\w+)\]\s*/.exec(firstInline.content) : null
      const kind = match ? match[1].toLowerCase() : null

      if (!match || !ALERT_TITLES[kind]) {
        out.push(token)
        continue
      }

      // Strip the marker from the first paragraph so it is not rendered twice.
      firstInline.content = firstInline.content.slice(match[0].length)
      if (Array.isArray(firstInline.children) && firstInline.children.length) {
        const kids = firstInline.children
        if (kids[0] && kids[0].type === 'text') {
          kids[0].content = kids[0].content.replace(/^\[!\w+\]\s*/, '')
        }
        // Drop the empty text run and the softbreak the marker's own line left
        // behind, otherwise the alert body starts with a blank line.
        while (
          kids.length > 0 &&
          ((kids[0].type === 'text' && kids[0].content === '') || kids[0].type === 'softbreak')
        ) {
          kids.shift()
        }
        if (kids.length === 0) firstInline.content = ''
      }

      const open = new state.Token('html_block', '', 0)
      open.content =
        `<div class="alert alert-${kind}">` +
        `<div class="alert-title">${escapeHtml(ALERT_TITLES[kind])}</div>`
      open.block = true
      out.push(open)

      // Everything inside the quote, minus the wrapper tokens.
      for (let k = i + 1; k < end; k++) out.push(tokens[k])

      const close = new state.Token('html_block', '', 0)
      close.content = '</div>'
      close.block = true
      out.push(close)
      i = end
    }

    state.tokens = out
    return true
  })
}

// ---------------------------------------------------------------------------
// Plain-text fast path
// ---------------------------------------------------------------------------

/**
 * A conservative "does this actually contain markdown?" test. Anything that is
 * not clearly a construct renders verbatim, which keeps `3 - 2 = 1`, `1.5 倍`
 * and `a_b_c` looking exactly as typed.
 */
function hasMarkdownSyntax(text) {
  return (
    /^ {0,3}(#{1,6}\s|>\s?|\|.*\|| {0,3}[-*+]\s|\d+[.)]\s|```|~~~|:::|:cute-table)/m.test(text) ||
    /(\*\*|__|~~|==|\|\s*:?-{2,}|`[^`]|\]\(|!\[|\[\^|\$[^$\n]+\$|\\\(|\\\[)/.test(text) ||
    /^\s*[-*+]\s/m.test(text) ||
    /:[a-z0-9_+-]+:/.test(text) ||
    /^ {0,3}\[[^\]]+\]:/m.test(text)
  )
}

/** Escape, keep line breaks, and link bare URLs without altering the text. */
function renderPlainText(text) {
  const escaped = escapeHtml(text)
  const linked = escaped.replace(
    /\b(https?:\/\/[^\s<>"']+)/g,
    (url) => `<a href="${url}" target="_blank" rel="noreferrer noopener">${url}</a>`,
  )
  return `<p>${linked.replace(/\n/g, '<br>\n')}</p>`
}

// ---------------------------------------------------------------------------
// Instance
// ---------------------------------------------------------------------------

const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  typographer: false,
  highlight: null,
})
  .use(footnote)
  .use(taskLists, { enabled: true, label: true })
  .use(deflist)
  .use(emoji)
  .use(sub)
  .use(sup)
  .use(mark)
  .use(ins)
  .use(containerPlugin)
  .use(tableTransformPlugin)
  .use(alertPlugin)

md.block.ruler.before('fence', 'math_block', mathBlockRule, {
  alt: ['paragraph', 'reference', 'blockquote', 'list'],
})
md.inline.ruler.after('escape', 'math_inline', mathInlineRule)

md.renderer.rules.fence = renderFence

md.renderer.rules.math_inline = (tokens, idx) => {
  const token = tokens[idx]
  const html = token.meta?.html
  return html ?? `<span class="math-plain">${escapeHtml(token.content)}</span>`
}

md.renderer.rules.math_block = (tokens, idx) => {
  const token = tokens[idx]
  const html = token.meta?.html
  if (!html) return `<p class="math-error">${escapeHtml(token.content)}</p>\n`
  return `<div class="math-block">${html}</div>\n`
}

for (const type of CONTAINER_TYPES) {
  md.renderer.rules[`container_${type}_open`] = (tokens, idx) =>
    renderContainerOpen(type, tokens[idx])
  md.renderer.rules[`container_${type}_close`] = () => renderContainerClose(type)
}

// Links always open in the system browser; enforced again in the main process.
const defaultLinkOpen =
  md.renderer.rules.link_open ??
  ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  const token = tokens[idx]
  token.attrSet('target', '_blank')
  token.attrSet('rel', 'noreferrer noopener')
  return defaultLinkOpen(tokens, idx, options, env, self)
}

md.renderer.rules.image = (tokens, idx, options, env, self) => {
  const token = tokens[idx]
  token.attrSet('loading', 'lazy')
  return self.renderToken(tokens, idx, options)
}

/**
 * Render one message body to HTML.
 * @param {string} text raw message text
 * @returns {{html: string, plain: boolean}}
 */
export function renderMarkdown(text) {
  const source = String(text ?? '')
  if (source.length === 0) return { html: '', plain: true }

  // Long messages skip parsing entirely so the list stays responsive.
  if (source.length > PLAIN_TEXT_LIMIT || !hasMarkdownSyntax(source)) {
    return { html: renderPlainText(source), plain: true }
  }

  try {
    return { html: md.render(source), plain: false }
  } catch {
    // A parser fault must never blank a message.
    return { html: renderPlainText(source), plain: true }
  }
}

/** Plain-text rendering used by notifications, quotes and search previews. */
export function toPlainText(text) {
  return String(text ?? '')
    .replace(/\$([^$\n]+)\$/g, (_, body) => latexToPlain(body))
    .replace(/\$\$([\s\S]+?)\$\$/g, (_, body) => latexToPlain(body))
    .replace(/\\\(([\s\S]+?)\\\)/g, (_, body) => latexToPlain(body))
    .replace(/\\\[([\s\S]+?)\\\]/g, (_, body) => latexToPlain(body))
    .replace(/```[\s\S]*?```/g, (block) => block.replace(/```\w*\n?/g, '').trim())
    .replace(/`([^`]*)`/g, '$1')
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_~]{1,3}([^*_~]+)[*_~]{1,3}/g, '$1')
    .replace(/^#{1,6}\s+/gm, '')
    .replace(/^>\s?/gm, '')
    .replace(/:::\w*(\[[^\]]*\])?/g, '')
    .trim()
}

/** Readable fallback for LaTeX in notifications and quotes: `\frac{a}{b}` -> `a/b`. */
export function latexToPlain(src) {
  return String(src ?? '')
    .replace(/\\frac\{([^{}]*)\}\{([^{}]*)\}/g, '($1)/($2)')
    .replace(/\\sqrt\{([^{}]*)\}/g, 'sqrt($1)')
    .replace(/\\(?:left|right|displaystyle|textstyle|limits|nolimits)\b/g, '')
    .replace(/\\(?:times|cdot)/g, '*')
    .replace(/\\div/g, '/')
    .replace(/\\(?:leq|le)/g, '<=')
    .replace(/\\(?:geq|ge)/g, '>=')
    .replace(/\\neq/g, '!=')
    .replace(/\\in\b/g, ' in ')
    .replace(/\\sum/g, 'Σ')
    .replace(/\\int/g, '∫')
    .replace(/\\infty/g, '∞')
    .replace(/\\(?:alpha|beta|gamma|theta|pi|lambda|mu|sigma|omega)\b/g, (m) => m.slice(1))
    .replace(/\\[a-zA-Z]+/g, '')
    .replace(/[{}]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
}

/** Exposed for the code-block copy button and tests. */
export { escapeHtml, hasMarkdownSyntax }

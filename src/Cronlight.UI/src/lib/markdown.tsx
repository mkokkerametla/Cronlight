import { Fragment, type ReactNode } from 'react'

/**
 * What a link in an answer does: job: links open the job's panel. Others show as text with their address, never as a
 * link to click: an answer can repeat text from job data or logs, which could carry a link meant to mislead.
 */
export interface MarkdownLinks {
  onOpenJob?: (group: string, name: string) => void
}

/**
 * The Markdown the assistant answers in, as React elements: headings, paragraphs, lists, tables, code, bold, italic
 * and job links. Never as HTML, so nothing in an answer (which may quote job names, errors and logs) can add markup.
 */
export function Markdown({ text, links = {} }: { text: string; links?: MarkdownLinks }) {
  return <div className="space-y-3 text-sm leading-6 break-words">{blocks(text, links)}</div>
}

function blocks(text: string, links: MarkdownLinks): ReactNode[] {
  // Models put narrow no-break spaces (U+202F) in times, as in "2026-10-29 01:56", which some fonts draw so thin
  // that the date and time run together; a no-break space keeps them together, a space apart
  const lines = text
    .replace(/\r\n?/g, '\n')
    .replace(/\u202f/g, '\u00a0')
    .split('\n')
  const out: ReactNode[] = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    const key = out.length
    if (!line.trim()) {
      i++
    } else if (line.trimStart().startsWith('```')) {
      const code: string[] = []
      for (i++; i < lines.length && !lines[i].trimStart().startsWith('```'); i++)
        code.push(lines[i])
      i++
      out.push(
        <pre
          key={key}
          className="overflow-x-auto rounded-lg border bg-muted/50 px-3 py-2.5 font-mono text-xs leading-relaxed"
        >
          {code.join('\n')}
        </pre>
      )
    } else if (/^#{1,6}\s/.test(line)) {
      out.push(
        <p key={key} className="pt-1 font-semibold">
          {inline(line.replace(/^#+\s*/, ''), links)}
        </p>
      )
      i++
    } else if (
      isTableRow(line) &&
      i + 1 < lines.length &&
      /^\s*\|?[\s:|-]+\|[\s:|-]*$/.test(lines[i + 1])
    ) {
      const header = cells(line)
      // ---: and :---: in the |---| line align a column, as for figures
      const align = cells(lines[i + 1]).map(c =>
        c.endsWith(':') ? (c.startsWith(':') ? 'text-center' : 'text-right') : 'text-left'
      )
      const rows: string[][] = []
      for (i += 2; i < lines.length && isTableRow(lines[i]); i++) rows.push(cells(lines[i]))
      out.push(
        <div key={key} className="overflow-x-auto rounded-lg border">
          {/* Code in a cell is set in monospace alone: a chip on every wrapped line of an error is noise */}
          <table className="w-full border-collapse text-xs tabular-nums [&_code]:bg-transparent [&_code]:px-0">
            <thead>
              <tr>
                {header.map((c, n) => (
                  <th
                    key={n}
                    className={`border-b bg-muted/50 px-3 py-2 font-medium ${align[n] ?? 'text-left'}`}
                  >
                    {inline(c, links)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, n) => (
                <tr key={n} className="border-b last:border-0">
                  {r.map((c, m) => (
                    <td key={m} className={`px-3 py-2 align-top ${align[m] ?? ''}`}>
                      {inline(c, links)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )
    } else if (listItem(line)) {
      const ordered = /^\s*\d+[.)]\s/.test(line)
      const items: string[] = []
      for (; i < lines.length && lines[i].trim(); i++) {
        const item = listItem(lines[i])
        if (item !== null) items.push(item)
        // A wrapped line belongs to the item before it
        else if (items.length) items[items.length - 1] += ` ${lines[i].trim()}`
      }
      const List = ordered ? 'ol' : 'ul'
      out.push(
        <List
          key={key}
          className={`space-y-1 pl-5 marker:text-muted-foreground ${ordered ? 'list-decimal' : 'list-disc'}`}
        >
          {items.map((item, n) => (
            <li key={n}>{inline(item, links)}</li>
          ))}
        </List>
      )
    } else {
      // Always the first line, so the loop moves on: a line that only looks like the start of a block, such as a
      // table's header row before its |---| line has streamed in, is text until it is one
      const paragraph = [line.trim()]
      for (i++; i < lines.length && lines[i].trim() && !startsBlock(lines, i); i++)
        paragraph.push(lines[i].trim())
      out.push(<p key={key}>{inline(paragraph.join(' '), links)}</p>)
    }
  }
  return out
}

const listItem = (line: string) => /^\s*(?:[-*+]|\d+[.)])\s+(.*)$/.exec(line)?.[1] ?? null
const isTableRow = (line: string) => line.trim().startsWith('|') && line.trim().length > 1
const cells = (line: string) =>
  line
    .trim()
    .replace(/^\||\|$/g, '')
    .split('|')
    .map(c => c.trim())
const startsBlock = (lines: string[], i: number) =>
  lines[i].trimStart().startsWith('```') ||
  /^#{1,6}\s/.test(lines[i]) ||
  listItem(lines[i]) !== null ||
  isTableRow(lines[i])

// Code first, so nothing inside it is read as emphasis or a link. <br> is the one tag taken, as models break lines
// in table cells with it; any other HTML stays text.
//
// A link's address may hold parentheses in pairs, which URL-encoding leaves, as in job:maint/cleanup%20(daily). Emphasis
// neither starts nor ends at a space, so the stars of a cron expression such as 0 */15 * * * ? stay as they are, and
// what it holds is read again, as models bold their links. _Underscores_ emphasise only clear of letters and digits, so
// a name like nightly_report_job is left alone.
const inlinePattern =
  /(`[^`]+`)|\[([^\]]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)|\*\*([^*\s](?:[^*]*[^*\s])?)\*\*|\*((?:[^*\s]|\*\*[^*]+\*\*)(?:(?:[^*]|\*\*[^*]+\*\*)*(?:[^*\s]|\*\*[^*]+\*\*))?)\*|(?<![\p{L}\p{N}_])_([^_\s](?:[^_]*[^_\s])?)_(?![\p{L}\p{N}_])|(<br\s*\/?>)/giu

function inline(text: string, links: MarkdownLinks): ReactNode {
  const parts: ReactNode[] = []
  let last = 0
  for (const match of text.matchAll(inlinePattern)) {
    if (match.index > last) parts.push(text.slice(last, match.index))
    const key = parts.length
    const [, code, label, href, bold, italic, underscored, lineBreak] = match
    if (code)
      parts.push(
        <code key={key} className="rounded bg-muted px-1 font-mono text-[0.85em]">
          {code.slice(1, -1)}
        </code>
      )
    else if (label) parts.push(<Fragment key={key}>{link(label, href, links)}</Fragment>)
    else if (bold) parts.push(<strong key={key}>{inline(bold, links)}</strong>)
    else if (lineBreak) parts.push(<br key={key} />)
    else parts.push(<em key={key}>{inline(italic ?? underscored, links)}</em>)
    last = match.index + match[0].length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

const linkClass = 'text-left font-medium text-link underline-offset-2 hover:underline'

function link(label: string, href: string, { onOpenJob }: MarkdownLinks): ReactNode {
  const job = /^job:([^/]+)\/(.+)$/.exec(href)
  if (job && onOpenJob) {
    const [group, name] = [plainHyphens(decode(job[1])), plainHyphens(decode(job[2]))]
    return (
      <button type="button" className={linkClass} onClick={() => onOpenJob(group, name)}>
        {label}
      </button>
    )
  }
  return label === href || job ? label : `${label} (${href})`
}

// Models often write job names with typographic hyphens (U+2010 to U+2015), as they do in text
const plainHyphens = (text: string) => text.replace(/[\u2010-\u2015]/g, '-')

function decode(text: string) {
  try {
    return decodeURIComponent(text)
  } catch {
    return text
  }
}

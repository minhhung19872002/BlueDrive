import { Fragment } from 'react'

const ENTITIES: Record<string, string> = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&#x27;': "'" }

/** Decode the few entities the server escapes (SearchText.Encode). */
function decode(text: string): string {
  return text.replace(/&(?:amp|lt|gt|quot|#39|#x27);/g, (m) => ENTITIES[m] ?? m)
}

/**
 * Renders server-highlighted text ("a <mark>b</mark> c", HTML-escaped by the server) WITHOUT innerHTML:
 * only <mark> / </mark> become elements, everything else is decoded and rendered as plain text.
 */
export default function HighlightedText({ html, fallback }: { html: string | null | undefined; fallback?: string | null }) {
  if (!html) return <>{fallback ?? ''}</>
  const parts = html.split(/(<\/?mark>)/i)
  const out: { text: string; marked: boolean }[] = []
  let marked = false
  for (const p of parts) {
    const tag = p.toLowerCase()
    if (tag === '<mark>') marked = true
    else if (tag === '</mark>') marked = false
    else if (p) out.push({ text: decode(p), marked })
  }
  return (
    <>
      {out.map((o, i) => (o.marked ? <mark key={i}>{o.text}</mark> : <Fragment key={i}>{o.text}</Fragment>))}
    </>
  )
}

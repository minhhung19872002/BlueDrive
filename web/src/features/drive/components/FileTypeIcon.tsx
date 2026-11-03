import type { CSSProperties, ReactNode } from 'react'
import type { ItemType, MediaType } from '@/types/drive'

interface FileTypeIconProps {
  type: ItemType
  mediaType?: MediaType | null
  extension?: string | null
  size?: number
  style?: CSSProperties
}

interface Badge {
  color: string
  /** Short text on the badge (W, X, P, PDF...) or a pictogram. */
  text?: string
  glyph?: ReactNode
}

const WHITE = '#fff'

/* Pictograms drawn inside the 14×14 badge (origin = badge top-left). */
const IMAGE_GLYPH = (
  <>
    <circle cx="4.6" cy="4.6" r="1.4" fill={WHITE} />
    <path d="M2 11.5 5.5 7.5l2.2 2.4L9.6 8 12 11.5Z" fill={WHITE} />
  </>
)
const VIDEO_GLYPH = <path d="M5 3.6v6.8L10.6 7Z" fill={WHITE} />
const AUDIO_GLYPH = (
  <path d="M9.8 2.6v6.3a1.9 1.9 0 1 1-1.2-1.8V4.5L5.6 5.2v4.9a1.9 1.9 0 1 1-1.2-1.8V4.2Z" fill={WHITE} />
)
const ZIP_GLYPH = (
  <path d="M6.2 2h1.6v1.4H6.2zm0 2.8h1.6v1.4H6.2zM5.6 7.6h2.8v3.6H5.6zm.8 1v1.6h1.2V8.6z" fill={WHITE} />
)
const BOOK_GLYPH = <path d="M3 3.2c1.6-.4 3 0 4 .8 1-.8 2.4-1.2 4-.8v7.4c-1.6-.4-3 0-4 .8-1-.8-2.4-1.2-4-.8Z" fill={WHITE} />

function badgeFor(mediaType: MediaType, ext: string): Badge {
  switch (mediaType) {
    case 'document':
      if (ext === 'txt') return { color: '#5b6b7f', text: 'TXT' }
      if (ext === 'rtf') return { color: '#5b6b7f', text: 'RTF' }
      if (ext === 'odt') return { color: '#2a6fdb', text: 'ODT' }
      return { color: '#2b62c9', text: 'W' }
    case 'spreadsheet':
      return ext === 'csv' ? { color: '#1d7a45', text: 'CSV' } : { color: '#1d7a45', text: 'X' }
    case 'presentation':
      return { color: '#c9472b', text: 'P' }
    case 'pdf':
      return { color: '#c62828', text: 'PDF' }
    case 'image':
    case 'infographic':
      return { color: '#0f8f8a', glyph: IMAGE_GLYPH }
    case 'video':
      return { color: '#b4235a', glyph: VIDEO_GLYPH }
    case 'audio':
      return { color: '#6d3fc0', glyph: AUDIO_GLYPH }
    case 'archive':
      return { color: '#8a6a1d', glyph: ZIP_GLYPH }
    case 'epub':
      return { color: '#5e4bd1', glyph: BOOK_GLYPH }
    case 'design':
      return { color: '#b0306f', text: ext.slice(0, 3).toUpperCase() || 'AI' }
    default:
      return { color: '#7a8594', text: ext ? ext.slice(0, 3).toUpperCase() : '' }
  }
}

/**
 * File type icon: a sheet with a folded corner and a colored badge (W / X / P / PDF or a pictogram),
 * or a flat folder. Original SVG artwork (no third-party icon set), scales with `size`.
 */
export default function FileTypeIcon({ type, mediaType, extension, size = 20, style }: FileTypeIconProps) {
  const box: CSSProperties = { width: size, height: size, flex: 'none', display: 'inline-block', verticalAlign: 'middle', ...style }
  if (type === 'folder')
    return (
      <svg viewBox="0 0 24 24" style={box} role="img" aria-label="Thư mục">
        <path d="M2 5.5A1.5 1.5 0 0 1 3.5 4h5.3l2 2.2h9.7A1.5 1.5 0 0 1 22 7.7v10.8a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 18.5Z" fill="#e8a317" />
        <path d="M2 8.6A1.5 1.5 0 0 1 3.5 7.1h17A1.5 1.5 0 0 1 22 8.6v9.9a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 18.5Z" fill="#ffc83d" />
      </svg>
    )
  const ext = (extension ?? '').toLowerCase().replace(/^\./, '')
  const badge = badgeFor(mediaType ?? 'other', ext)
  const text = badge.text ?? ''
  const fontSize = text.length >= 3 ? 4.6 : text.length === 2 ? 6 : 8
  return (
    <svg viewBox="0 0 24 24" style={box} role="img" aria-label={ext ? ext.toUpperCase() : 'Tệp'}>
      {/* Sheet with folded corner */}
      <path d="M6 2h8.6L20 7.4V20.5A1.5 1.5 0 0 1 18.5 22h-12.5A1.5 1.5 0 0 1 4.5 20.5v-17A1.5 1.5 0 0 1 6 2Z" fill="#fff" stroke="#c8cdd4" strokeWidth="1" />
      <path d="M14.6 2v4A1.4 1.4 0 0 0 16 7.4h4" fill="#eef0f3" stroke="#c8cdd4" strokeWidth="1" strokeLinejoin="round" />
      <path d="M11 11.5h6M11 14h6M11 16.5h6" stroke={badge.color} strokeOpacity="0.35" strokeWidth="1" strokeLinecap="round" />
      {/* Badge */}
      <g transform="translate(1 9)">
        <rect width="14" height="14" rx="2.2" fill={badge.color} />
        {badge.glyph ??
          (text && (
            <text
              x="7"
              y="7"
              dy="0.36em"
              textAnchor="middle"
              fill={WHITE}
              fontSize={fontSize}
              fontWeight={700}
              fontFamily="'Segoe UI', Roboto, Arial, sans-serif"
            >
              {text}
            </text>
          ))}
      </g>
    </svg>
  )
}

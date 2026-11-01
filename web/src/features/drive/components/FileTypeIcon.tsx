import type { CSSProperties } from 'react'
import {
  AudioOutlined,
  FileExcelOutlined,
  FileImageOutlined,
  FileOutlined,
  FilePdfOutlined,
  FilePptOutlined,
  FileTextOutlined,
  FileWordOutlined,
  FileZipOutlined,
  FolderFilled,
  PictureOutlined,
  ReadOutlined,
  VideoCameraOutlined,
  BgColorsOutlined,
} from '@ant-design/icons'
import type { ItemType, MediaType } from '@/types/drive'

interface FileTypeIconProps {
  type: ItemType
  mediaType?: MediaType | null
  extension?: string | null
  size?: number
  style?: CSSProperties
}

const COLORS: Record<MediaType | 'folder', string> = {
  folder: '#f4b400',
  image: '#13a8a8',
  video: '#cf1322',
  audio: '#722ed1',
  document: '#1d5fbf',
  spreadsheet: '#1f7a3e',
  presentation: '#d4380d',
  pdf: '#c41d1d',
  infographic: '#08979c',
  epub: '#7c4dff',
  archive: '#8c6d1f',
  design: '#c41d7f',
  other: '#8c8c8c',
}

/** Icon (with brand-like color) by item type / media type. */
export default function FileTypeIcon({ type, mediaType, extension, size = 20, style }: FileTypeIconProps) {
  const base: CSSProperties = { fontSize: size, ...style }
  if (type === 'folder') return <FolderFilled style={{ ...base, color: COLORS.folder }} />
  const m = mediaType ?? 'other'
  const color = COLORS[m]
  const s = { ...base, color }
  const ext = (extension ?? '').toLowerCase()
  switch (m) {
    case 'image':
      return <FileImageOutlined style={s} />
    case 'infographic':
      return <PictureOutlined style={s} />
    case 'video':
      return <VideoCameraOutlined style={s} />
    case 'audio':
      return <AudioOutlined style={s} />
    case 'pdf':
      return <FilePdfOutlined style={s} />
    case 'spreadsheet':
      return <FileExcelOutlined style={s} />
    case 'presentation':
      return <FilePptOutlined style={s} />
    case 'document':
      return ext === 'txt' || ext === 'rtf' ? <FileTextOutlined style={s} /> : <FileWordOutlined style={s} />
    case 'epub':
      return <ReadOutlined style={s} />
    case 'archive':
      return <FileZipOutlined style={s} />
    case 'design':
      return <BgColorsOutlined style={s} />
    default:
      return <FileOutlined style={s} />
  }
}

import type { ReactNode } from 'react'
import type { PreviewKind } from '../utils'
import ImageViewer from './ImageViewer'
import TextPreview from './TextPreview'

interface MediaPreviewProps {
  kind: PreviewKind
  /** Content URL (signed or public; supports HTTP Range for video/audio seeking). */
  src: string
  name: string
  sizeBytes: number
  poster?: string | null
  imageTools?: ReactNode
  /** Rendered when the type cannot be previewed in the browser. */
  fallback: ReactNode
}

/** Browser preview by kind: image lightbox, HTML5 video/audio, PDF (built-in viewer), text. */
export default function MediaPreview({ kind, src, name, sizeBytes, poster, imageTools, fallback }: MediaPreviewProps) {
  switch (kind) {
    case 'image':
      return <ImageViewer key={src} src={src} alt={name} extra={imageTools} />
    case 'video':
      return (
        <video key={src} className="bd-media-video" src={src} controls preload="metadata" poster={poster ?? undefined} playsInline>
          {name}
        </video>
      )
    case 'audio':
      return (
        <div className="bd-media-audio">
          {poster && <img src={poster} alt="" />}
          <audio key={src} src={src} controls preload="metadata" style={{ width: '100%' }}>
            {name}
          </audio>
        </div>
      )
    case 'pdf':
      return <iframe key={src} className="bd-media-pdf" src={src} title={name} />
    case 'text':
      return <TextPreview url={src} sizeBytes={sizeBytes} />
    default:
      return <>{fallback}</>
  }
}

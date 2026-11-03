import { useState } from 'react'
import type { DriveItem } from '@/types/drive'
import FileTypeIcon from './FileTypeIcon'

/** Grid tile preview: the signed thumbnail URL from the listing (lazy-loaded by the browser), else the type icon. */
export default function Thumbnail({ item, iconSize = 48 }: { item: DriveItem; iconSize?: number }) {
  const src = item.type === 'file' ? item.thumbnailUrl : null
  // Remember which URL failed: a refreshed listing brings a new signed URL that deserves a new try.
  const [failedSrc, setFailedSrc] = useState<string | null>(null)
  const failed = !!src && failedSrc === src
  return (
    <div className="bd-thumb">
      {src && !failed ? (
        <img src={src} alt="" loading="lazy" decoding="async" draggable={false} onError={() => setFailedSrc(src)} />
      ) : (
        <FileTypeIcon type={item.type} mediaType={item.mediaType} extension={item.extension} size={iconSize} />
      )}
    </div>
  )
}

import type { FileDetail } from '@/types/drive'

// Content tickets are signed per request, so every refetch of a file returns a new preview URL and the
// browser downloads the image again. Keep the first URL per file version (tickets last 6 h) so that
// moving back and forth in the viewer hits the browser cache.
const TTL_MS = 5 * 60 * 60 * 1000
const cache = new Map<string, { url: string; at: number }>()

export function stablePreviewUrl(f: FileDetail): string | null {
  if (!f.previewUrl) return null
  const key = `${f.id}:${f.versions.find((v) => v.isCurrent)?.id ?? ''}`
  const hit = cache.get(key)
  if (hit && Date.now() - hit.at < TTL_MS) return hit.url
  cache.set(key, { url: f.previewUrl, at: Date.now() })
  return f.previewUrl
}

/** Starts downloading and decoding an image so showing it later is instant. */
export function preloadImage(url: string) {
  const img = new Image()
  img.decoding = 'async'
  img.src = url
  img.decode?.().catch(() => undefined)
}

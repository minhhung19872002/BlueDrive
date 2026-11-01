import { isAxiosError } from 'axios'
import { api } from './client'

type ParamValue = string | number | boolean | null | undefined

/** Drop empty values so they are not sent as query parameters. */
export function cleanParams<T extends object>(params: T | undefined): Record<string, string | number | boolean> {
  const out: Record<string, string | number | boolean> = {}
  if (!params) return out
  for (const [key, value] of Object.entries(params) as [string, ParamValue][]) {
    if (value === undefined || value === null) continue
    if (typeof value === 'string' && value.trim() === '') continue
    out[key] = typeof value === 'string' ? value.trim() : value
  }
  return out
}

/** Extract the file name from a Content-Disposition header (supports RFC 5987 filename*). */
function fileNameFromDisposition(header: string | undefined, fallback: string): string {
  if (!header) return fallback
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(header)
  if (star?.[1]) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''))
    } catch {
      // Fall through to the plain filename parameter.
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header)
  return plain?.[1]?.trim() || fallback
}

/** Trigger a browser download for an in-memory blob. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/**
 * GET a file endpoint through the authenticated client and save it using the server-provided name.
 * Error bodies (ProblemDetails) arrive as Blob: they are parsed back to JSON so the error helper can read them.
 */
export async function downloadFile(url: string, params?: object, fallbackName = 'tai-xuong'): Promise<void> {
  try {
    const res = await api.get<Blob>(url, { params: cleanParams(params), responseType: 'blob' })
    const disposition = res.headers['content-disposition'] as string | undefined
    saveBlob(res.data, fileNameFromDisposition(disposition, fallbackName))
  } catch (error) {
    if (isAxiosError(error) && error.response?.data instanceof Blob) {
      try {
        error.response.data = JSON.parse(await error.response.data.text())
      } catch {
        // Not JSON: keep the original error.
      }
    }
    throw error
  }
}

/** POST a single file as multipart/form-data (field "file"). */
export async function uploadFile<T>(url: string, file: File, params?: object): Promise<T> {
  const form = new FormData()
  form.append('file', file)
  const { data } = await api.post<T>(url, form, { params: cleanParams(params) })
  return data
}

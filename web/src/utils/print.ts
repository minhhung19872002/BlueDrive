import { cleanParams } from '@/api/http'

/** Open a print view in a new tab, passing the current filters through the query string. */
export function openPrintView(path: string, filter: object): void {
  const qs = new URLSearchParams(
    Object.entries(cleanParams(filter)).map(([k, v]) => [k, String(v)] as [string, string]),
  ).toString()
  window.open(qs ? `${path}?${qs}` : path, '_blank', 'noopener')
}

/** Read a boolean query parameter ("true"/"false"); undefined when absent. */
export function boolParam(params: URLSearchParams, key: string): boolean | undefined {
  const v = params.get(key)
  return v === 'true' ? true : v === 'false' ? false : undefined
}

export function strParam<T extends string = string>(params: URLSearchParams, key: string): T | undefined {
  return (params.get(key) || undefined) as T | undefined
}

/** Find a node title in a tree by id. */
export function findInTree<T extends { id: string; name: string; children: T[] }>(nodes: T[] | undefined, id?: string): T | undefined {
  if (!nodes || !id) return undefined
  for (const n of nodes) {
    if (n.id === id) return n
    const hit = findInTree(n.children, id)
    if (hit) return hit
  }
  return undefined
}

/** Server-side max page size: print views show at most this many rows. */
export const PRINT_MAX_ROWS = 500

import type { OrganizationTreeNode } from '@/types/admin'

export interface TreeSelectNode {
  value: string
  title: string
  disabled?: boolean
  children?: TreeSelectNode[]
}

/**
 * Convert the organization tree into TreeSelect data.
 * `disableId` disables that node and its whole subtree (a unit cannot become a child of itself).
 */
export function orgTreeToSelect(nodes: OrganizationTreeNode[] | undefined, disableId?: string): TreeSelectNode[] {
  const walk = (list: OrganizationTreeNode[], disabled: boolean): TreeSelectNode[] =>
    list.map((n) => {
      const off = disabled || n.id === disableId
      return {
        value: n.id,
        title: `${n.name} (${n.code})`,
        disabled: off || undefined,
        children: n.children.length ? walk(n.children, off) : undefined,
      }
    })
  return walk(nodes ?? [], false)
}

export type TreeOf<T> = T & { children?: TreeOf<T>[] }

/** Build a parent/child tree from a flat list with parentId. Orphans (parent filtered out) become roots. */
export function buildTree<T extends { id: string; parentId: string | null }>(
  items: T[],
  compare?: (a: T, b: T) => number,
): TreeOf<T>[] {
  type Node = TreeOf<T>
  const map = new Map<string, Node>()
  items.forEach((i) => map.set(i.id, { ...i }))
  const roots: Node[] = []
  map.forEach((node) => {
    const parent = node.parentId ? map.get(node.parentId) : undefined
    if (parent) (parent.children ??= []).push(node)
    else roots.push(node)
  })
  if (compare) {
    const sortRec = (list: Node[]) => {
      list.sort(compare)
      list.forEach((n) => n.children && sortRec(n.children))
    }
    sortRec(roots)
  }
  return roots
}

/** Accent-insensitive lowercase string for client-side matching. */
export function normalizeText(s: string): string {
  return s
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/đ/g, 'd')
    .replace(/Đ/g, 'D')
    .toLowerCase()
}

/** antd TreeSelect filterTreeNode that ignores Vietnamese accents. */
export function filterTreeNodeUnaccent(input: string, node: { title?: unknown }): boolean {
  return normalizeText(String(node.title ?? '')).includes(normalizeText(input))
}

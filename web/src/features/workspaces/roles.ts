import type { WorkspaceMemberRole } from '@/types/drive'

/** Roles assignable to members (the owner changes only through "transfer owner"). */
export const MEMBER_ROLES: WorkspaceMemberRole[] = ['manager', 'editor', 'uploader', 'approver', 'viewer']

export const ROLE_COLORS: Record<WorkspaceMemberRole, string> = {
  owner: 'gold',
  manager: 'purple',
  editor: 'green',
  uploader: 'cyan',
  approver: 'magenta',
  viewer: 'default',
}

/** Colour of a usage bar: orange from 80 %, red from 100 %. */
export function usageColor(percent: number): string | undefined {
  if (percent >= 100) return '#cf1322'
  if (percent >= 80) return '#d48806'
  return undefined
}

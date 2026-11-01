import type { AccessLevel, AccessSource, PrincipalType, ShareLinkStatus } from '@/types/sharing'

/** Levels offered when sharing (#49): Xem / Xem + Tải xuống / Chỉnh sửa. */
export const SHARE_LEVELS: AccessLevel[] = ['view', 'download', 'edit']

/** Levels for direct ACL entries (#56). */
export const ACL_LEVELS: AccessLevel[] = ['view', 'download', 'edit', 'manage']

export const LEVEL_COLORS: Record<AccessLevel, string> = {
  none: 'default',
  view: 'default',
  download: 'blue',
  edit: 'green',
  manage: 'gold',
}

export const SOURCE_COLORS: Record<AccessSource, string> = {
  owner: 'gold',
  member: 'blue',
  inherited: 'purple',
  share: 'cyan',
  direct: 'green',
}

export const LINK_STATUS_COLORS: Record<ShareLinkStatus, string> = {
  active: 'green',
  revoked: 'default',
  expired: 'orange',
  exhausted: 'red',
}

export const PRINCIPAL_TYPES: PrincipalType[] = ['user', 'group', 'organization', 'role']

/** Principal chosen in PrincipalSelect: value is "<type>:<id>", label the display name. */
export interface PrincipalValue {
  value: string
  label: string
}

export function splitPrincipal(value: string): { type: PrincipalType; id: string } {
  const i = value.indexOf(':')
  return { type: value.slice(0, i) as PrincipalType, id: value.slice(i + 1) }
}

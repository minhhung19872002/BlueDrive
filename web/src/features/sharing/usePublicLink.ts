import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getItemSharingPolicy, listPermissionLevels, setItemExternalSharing } from '@/api/permissions'
import { qk } from '@/api/queryKeys'
import { createShareLink, listShareLinks, updateShareLink } from '@/api/sharing'
import { useApiMutation } from '@/hooks/useApiMutation'
import type { ResourceType, SaveShareLinkRequest } from '@/types/sharing'

/**
 * #49 "Bất kỳ ai có liên kết" as one choice of "Quyền truy cập chung": on, the item's link opens for anyone who has it (no sign-in) with
 * the level chosen beside it (Chỉ xem / Chỉ xem (không tải) / Bình luận / Chỉnh sửa); off, only for people who already have access. A tier above (system, Space, folder, label) that forbids it blocks the
 * choice, and says which.
 */
const LINK_LEVEL_CODES = ['viewer', 'view_only', 'commenter', 'editor']

export function usePublicLink(resourceType: ResourceType | null, resourceId: string) {
  const { t } = useTranslation()
  const enabled = resourceType !== null
  const type = resourceType ?? 'file'
  const linksKey = qk.shareLinks(type, resourceId)
  const links = useQuery({ queryKey: linksKey, queryFn: () => listShareLinks(type, resourceId), enabled })
  const policy = useQuery({ queryKey: ['item-sharing-policy', resourceId], queryFn: () => getItemSharingPolicy(resourceId), enabled })
  const levels = useQuery({ queryKey: ['permission-levels'], queryFn: listPermissionLevels, staleTime: 5 * 60_000, enabled })
  // The levels a link can give (the server's LinkLevels), most common first.
  const linkLevels = LINK_LEVEL_CODES.map((code) => levels.data?.find((l) => l.code === code)).filter((l) => !!l)
  const viewerId = linkLevels.find((l) => l.code === 'viewer')?.id ?? null
  const active = [...(links.data ?? [])].filter((l) => l.status === 'active').sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  const current = active.find((l) => !!l.url) ?? active[0]
  const p = policy.data
  // The item's own setting does not count as a block: choosing "anyone" lifts it.
  const blocked = !!p && !p.anyoneLinksAllowed && !(p.itemExternal !== null && p.externalSource === 'Mục này')

  const set = useApiMutation(
    async ({ on: wanted, levelId }: { on: boolean; levelId?: string | null }) => {
      const level = linkLevels.find((l) => l.id === (levelId ?? (current?.scope === 'anyone' ? current.levelId : null) ?? viewerId))
      if (wanted && p?.itemExternal && p.itemExternal !== 'anyone') await setItemExternalSharing(resourceId, null)
      const body: SaveShareLinkRequest = {
        resourceType: type,
        resourceId,
        allowDownload: level?.code !== 'view_only',
        allowEdit: wanted && level?.code === 'editor',
        requireLogin: !wanted,
        scope: wanted ? 'anyone' : 'existing',
        levelId: wanted ? (level?.id ?? viewerId) : null,
        people: [],
        expiresAt: wanted && current?.scope === 'anyone' ? current.expiresAt : null,
        maxAccessCount: wanted ? (current?.maxAccessCount ?? null) : null,
      }
      if (current?.url) await updateShareLink(current.id, body)
      else await createShareLink(body)
    },
    {
      invalidate: [linksKey, ['item-sharing-policy']],
      successMessage: (_, v) => (v.on ? t('sharing.publicOn') : undefined),
    },
  )

  return {
    enabled,
    on: current?.scope === 'anyone',
    levelId: (current?.scope === 'anyone' ? current.levelId : null) ?? viewerId,
    levelOptions: linkLevels.map((l) => ({ value: l.id, label: l.name, title: l.description })),
    blocked,
    blockedText: blocked && p ? t('sharing.publicBlocked', { source: p.externalSource }) : null,
    canChange: !!p?.canChangeItem,
    set,
  }
}

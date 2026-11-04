import { Alert, Typography } from 'antd'
import type { TFunction } from 'i18next'
import { previewMove } from '@/api/drive'
import type { ItemRef } from '@/types/drive'
import { getModal } from '@/utils/antdStatic'

/** Thrown when the user does not go on with a move (callers treat it as no error). */
export class MoveCancelled extends Error {}

/**
 * Spec 1.1: before moving to another place, show who will lose access (what came from the old place) and who will get it; direct grants stay.
 * Resolves when the move may go on (nothing changes, or the user agreed); rejects with MoveCancelled otherwise.
 */
export async function confirmMove(t: TFunction, items: ItemRef[], workspaceId: string, folderId: string | null): Promise<void> {
  const p = await previewMove(items, workspaceId, folderId)
  if (p.blocked) throw new Error(p.blockedReason ?? t('moveWarning.blocked'))
  if (p.lose.length === 0 && p.gain.length === 0 && !p.ownershipToSpace) return
  const list = (names: string[]) => names.slice(0, 12).join(', ') + (names.length > 12 ? t('moveWarning.more', { count: names.length - 12 }) : '')
  await new Promise<void>((resolve, reject) =>
    getModal().confirm({
      title: t('moveWarning.title'),
      width: 560,
      okText: t('moveWarning.ok'),
      content: (
        <>
          {p.ownershipToSpace && <Alert type="info" showIcon message={t('moveWarning.ownership')} style={{ marginBottom: 8 }} />}
          {p.lose.length > 0 && (
            <Typography.Paragraph>
              <strong>{t('moveWarning.lose')}</strong> {list(p.lose)}
            </Typography.Paragraph>
          )}
          {p.gain.length > 0 && (
            <Typography.Paragraph>
              <strong>{t('moveWarning.gain')}</strong> {list(p.gain)}
            </Typography.Paragraph>
          )}
          <Typography.Text type="secondary">{t('moveWarning.direct')}</Typography.Text>
        </>
      ),
      onOk: () => resolve(),
      onCancel: () => reject(new MoveCancelled()),
    }),
  )
}

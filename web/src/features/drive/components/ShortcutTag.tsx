import { Tag, Tooltip } from 'antd'
import { RollbackOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import type { DriveItem } from '@/types/drive'

/** Marks a shortcut row (spec 1.2) and, when it cannot be opened, why. */
export default function ShortcutTag({ item }: { item: DriveItem }) {
  const { t } = useTranslation()
  if (!item.shortcutId) return null
  const state = item.shortcutState ?? 'ok'
  return (
    <Tooltip title={t(`shortcut.state.${state}`)}>
      <Tag icon={<RollbackOutlined rotate={180} />} color={state === 'ok' ? 'default' : state === 'deleted' ? 'red' : 'orange'} style={{ flex: 'none', marginInlineEnd: 0 }}>
        {state === 'deleted' ? t('shortcut.deleted') : t('shortcut.tag')}
      </Tag>
    </Tooltip>
  )
}

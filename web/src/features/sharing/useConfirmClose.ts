import { useRef } from 'react'
import { App } from 'antd'
import { useTranslation } from 'react-i18next'

/** Closing with unsaved permission changes asks first (they are only sent with "Lưu"). */
export function useConfirmClose(onClose: () => void) {
  const { t } = useTranslation()
  const { modal } = App.useApp()
  const dirty = useRef(false)
  return {
    setDirty: (value: boolean) => {
      dirty.current = value
    },
    close: () => {
      if (!dirty.current) return onClose()
      modal.confirm({
        title: t('sharing.people.leaveTitle'),
        content: t('sharing.people.leaveContent'),
        okText: t('sharing.people.leaveOk'),
        okButtonProps: { danger: true },
        onOk: onClose,
      })
    },
  }
}

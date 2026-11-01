import { useCallback } from 'react'
import { App } from 'antd'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { getErrorMessage, showApiError } from '@/api/errors'
import { errorStatus } from '@/api/formErrors'
import { deleteOrganization } from '@/api/organizations'
import { qk } from '@/api/queryKeys'

/**
 * Delete an organization with confirmation. When it is still in use (409), show the server message
 * and offer "Ngừng hoạt động" instead (DELETE ?deactivateIfInUse=true).
 */
export function useDeleteOrganization() {
  const { t } = useTranslation()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()

  return useCallback(
    (org: { id: string; name: string }, onDeleted?: () => void) => {
      const refresh = () => queryClient.invalidateQueries({ queryKey: qk.organizations })

      const offerDeactivate = (reason: string) =>
        modal.confirm({
          title: t('org.cannotDeleteTitle'),
          content: reason,
          okText: t('org.deactivate'),
          cancelText: t('common.close'),
          onOk: async () => {
            try {
              await deleteOrganization(org.id, true)
              void message.success(t('org.deactivated', { name: org.name }))
              await refresh()
            } catch (error) {
              showApiError(error)
            }
          },
        })

      modal.confirm({
        title: t('org.deleteTitle'),
        content: t('org.deleteConfirm', { name: org.name }),
        okText: t('common.delete'),
        okButtonProps: { danger: true },
        cancelText: t('common.cancel'),
        onOk: async () => {
          try {
            const res = await deleteOrganization(org.id)
            void message.success(
              res.result === 'deactivated' ? t('org.deactivated', { name: org.name }) : t('org.deleted', { name: org.name }),
            )
            if (res.result === 'deleted') onDeleted?.()
            await refresh()
          } catch (error) {
            if (errorStatus(error) === 409) offerDeactivate(getErrorMessage(error))
            else showApiError(error)
          }
        },
      })
    },
    [message, modal, queryClient, t],
  )
}

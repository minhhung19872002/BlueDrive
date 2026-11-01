import { useCallback } from 'react'
import { App } from 'antd'
import { useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { showApiError } from '@/api/errors'
import { qk } from '@/api/queryKeys'
import { revokeAllUserSessions } from '@/api/system'
import { deleteUser, lockUser, resetUserPassword, unlockUser } from '@/api/users'

interface UserRef {
  id: string
  username: string
  fullName: string
}

/**
 * Confirmed account actions shared by the users and admin sessions screens.
 * `onTempPassword` receives the temporary password produced by an admin reset.
 */
export function useUserActions(onTempPassword?: (username: string, password: string | null) => void) {
  const { t } = useTranslation()
  const { modal, message } = App.useApp()
  const queryClient = useQueryClient()

  const confirm = useCallback(
    (opts: { title: string; content: string; okText: string; danger?: boolean; action: () => Promise<unknown>; done: string }) =>
      modal.confirm({
        title: opts.title,
        content: opts.content,
        okText: opts.okText,
        okButtonProps: { danger: opts.danger },
        cancelText: t('common.cancel'),
        onOk: async () => {
          try {
            await opts.action()
            void message.success(opts.done)
            await Promise.all([
              queryClient.invalidateQueries({ queryKey: qk.users }),
              queryClient.invalidateQueries({ queryKey: ['sessions'] }),
            ])
          } catch (error) {
            showApiError(error)
          }
        },
      }),
    [message, modal, queryClient, t],
  )

  const lock = useCallback(
    (u: UserRef) =>
      confirm({
        title: t('user.lockTitle'),
        content: t('user.lockConfirm', { name: u.fullName, username: u.username }),
        okText: t('user.lock'),
        danger: true,
        action: () => lockUser(u.id),
        done: t('user.locked', { username: u.username }),
      }),
    [confirm, t],
  )

  const unlock = useCallback(
    (u: UserRef) =>
      confirm({
        title: t('user.unlockTitle'),
        content: t('user.unlockConfirm', { name: u.fullName, username: u.username }),
        okText: t('user.unlock'),
        action: () => unlockUser(u.id),
        done: t('user.unlocked', { username: u.username }),
      }),
    [confirm, t],
  )

  const resetPassword = useCallback(
    (u: UserRef) =>
      confirm({
        title: t('user.resetTitle'),
        content: t('user.resetConfirm', { name: u.fullName, username: u.username }),
        okText: t('user.resetPassword'),
        danger: true,
        action: async () => {
          const res = await resetUserPassword(u.id)
          onTempPassword?.(u.username, res.temporaryPassword)
        },
        done: t('user.resetDone', { username: u.username }),
      }),
    [confirm, onTempPassword, t],
  )

  const revokeSessions = useCallback(
    (u: { id: string; username: string; fullName: string }) =>
      confirm({
        title: t('user.revokeTitle'),
        content: t('user.revokeConfirm', { name: u.fullName, username: u.username }),
        okText: t('user.revokeSessions'),
        danger: true,
        action: () => revokeAllUserSessions(u.id),
        done: t('user.revokeDone', { username: u.username }),
      }),
    [confirm, t],
  )

  const remove = useCallback(
    (u: UserRef) =>
      confirm({
        title: t('user.deleteTitle'),
        content: t('user.deleteConfirm', { name: u.fullName, username: u.username }),
        okText: t('common.delete'),
        danger: true,
        action: () => deleteUser(u.id),
        done: t('user.deleted', { username: u.username }),
      }),
    [confirm, t],
  )

  return { lock, unlock, resetPassword, revokeSessions, remove }
}

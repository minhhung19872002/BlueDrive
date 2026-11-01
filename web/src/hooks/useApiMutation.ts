import { useMutation, useQueryClient, type QueryKey, type UseMutationOptions } from '@tanstack/react-query'
import { showApiError } from '@/api/errors'
import { getMessage } from '@/utils/antdStatic'

export interface ApiMutationOptions<TData, TVars>
  extends Omit<UseMutationOptions<TData, unknown, TVars>, 'mutationFn' | 'onError' | 'onSuccess'> {
  /** antd success message shown after the call succeeds. */
  successMessage?: string | ((data: TData, vars: TVars) => string | undefined)
  /** Query key prefixes to invalidate after success. */
  invalidate?: QueryKey[]
  /** Per-status overrides for the error message. */
  errorOverrides?: Partial<Record<number, string>>
  onSuccess?: (data: TData, vars: TVars) => void
  /** Custom error handler; return true when the error was fully handled (no generic message shown). */
  onError?: (error: unknown, vars: TVars) => boolean | void
}

/**
 * useMutation with the app conventions: Vietnamese success message, query invalidation,
 * and ProblemDetails error display through the shared error helper.
 */
export function useApiMutation<TData = unknown, TVars = void>(
  mutationFn: (vars: TVars) => Promise<TData>,
  options: ApiMutationOptions<TData, TVars> = {},
) {
  const queryClient = useQueryClient()
  const { successMessage, invalidate, errorOverrides, onSuccess, onError, ...rest } = options
  return useMutation<TData, unknown, TVars>({
    ...rest,
    mutationFn,
    onSuccess: async (data, vars) => {
      const text = typeof successMessage === 'function' ? successMessage(data, vars) : successMessage
      if (text) void getMessage().success(text)
      onSuccess?.(data, vars)
      if (invalidate?.length) {
        await Promise.all(invalidate.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
      }
    },
    onError: (error, vars) => {
      if (onError?.(error, vars) === true) return
      showApiError(error, errorOverrides)
    },
  })
}

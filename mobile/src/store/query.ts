import { QueryClient } from '@tanstack/react-query';

import { ApiError } from '@/api/client';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: (count, error) => {
        // Never retry auth/permission/validation errors.
        if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false;
        return count < 2;
      },
    },
    mutations: { retry: false },
  },
});

/** Keys holding short-lived signed URLs / viewer state that must not be refetched on data changes. */
const VOLATILE = new Set(['inline-url', 'office-config', 'text']);

/** Refresh every list/detail after a mutation (rename, move, upload...) without reloading open viewers. */
export function invalidateData(client: QueryClient = queryClient) {
  return client.invalidateQueries({ predicate: (q) => !VOLATILE.has(String(q.queryKey[0])) });
}

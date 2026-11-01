import { ApiError, refreshTokens } from '@/api/client';
import { authApi } from '@/api/endpoints';
import type { AuthResult } from '@/api/types';
import { queryClient } from '@/store/query';
import { useAuth } from '@/store/auth';

/** Restore the session saved in SecureStore: verify it with /auth/me (refreshing tokens if needed). */
export async function bootstrapSession(): Promise<{ error?: string }> {
  const auth = useAuth.getState();
  const { hasSession } = await auth.hydrate();
  if (!hasSession) {
    await useAuth.getState().clearSession();
    return {};
  }
  try {
    const me = await authApi.me();
    useAuth.getState().setUser(me);
    useAuth.getState().markSignedIn();
    return {};
  } catch (e) {
    if (e instanceof ApiError && (e.status === 401 || e.status === 403)) {
      if (useAuth.getState().refreshToken) {
        // 403 on /me is unexpected; try one rotation before giving up.
        try {
          await refreshTokens();
          const me = await authApi.me();
          useAuth.getState().setUser(me);
          useAuth.getState().markSignedIn();
          return {};
        } catch {
          // fall through
        }
      }
      await useAuth.getState().clearSession();
      return {};
    }
    return { error: e instanceof Error ? e.message : String(e) };
  }
}

export async function completeSignIn(result: AuthResult) {
  queryClient.clear();
  await useAuth.getState().setSession(result);
  useAuth.getState().markSignedIn();
}

/** POST /auth/logout (best effort) then wipe tokens and cached data. */
export async function signOut() {
  try {
    if (useAuth.getState().refreshToken) await authApi.logout();
  } catch {
    // Offline or already revoked: still sign out locally.
  }
  await useAuth.getState().clearSession();
  queryClient.clear();
}

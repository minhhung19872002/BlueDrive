import * as SecureStore from 'expo-secure-store';
import { create } from 'zustand';

import type { AuthResult, UserProfile } from '@/api/types';

const K = {
  server: 'bd.server',
  access: 'bd.access',
  accessExp: 'bd.accessExp',
  refresh: 'bd.refresh',
  biometric: 'bd.biometric',
  lastUsername: 'bd.lastUsername',
} as const;

export type AuthStatus = 'loading' | 'signedOut' | 'signedIn';

interface AuthState {
  status: AuthStatus;
  serverUrl: string;
  lastUsername: string;
  accessToken: string | null;
  accessExpiresAt: number;
  refreshToken: string | null;
  user: UserProfile | null;
  mustChangePassword: boolean;
  biometricEnabled: boolean;
  locked: boolean;
  /** Load persisted values from SecureStore (tokens are verified later by bootstrap). */
  hydrate: () => Promise<{ hasSession: boolean }>;
  setServerUrl: (url: string) => Promise<void>;
  setLastUsername: (u: string) => Promise<void>;
  setSession: (r: AuthResult) => Promise<void>;
  setUser: (u: UserProfile) => void;
  setMustChangePassword: (v: boolean) => void;
  markSignedIn: () => void;
  clearSession: () => Promise<void>;
  setBiometricEnabled: (v: boolean) => Promise<void>;
  setLocked: (v: boolean) => void;
}

/** Normalize "drive.example.gov.vn/" → "https://drive.example.gov.vn". */
export function normalizeServerUrl(input: string): string {
  let s = input.trim();
  if (!s) return '';
  if (!/^https?:\/\//i.test(s)) s = `https://${s}`;
  return s.replace(/\/+$/, '').replace(/\/api\/v1$/i, '');
}

export const useAuth = create<AuthState>((set, get) => ({
  status: 'loading',
  serverUrl: '',
  lastUsername: '',
  accessToken: null,
  accessExpiresAt: 0,
  refreshToken: null,
  user: null,
  mustChangePassword: false,
  biometricEnabled: false,
  locked: false,

  hydrate: async () => {
    const [server, access, exp, refresh, bio, lastUser] = await Promise.all([
      SecureStore.getItemAsync(K.server),
      SecureStore.getItemAsync(K.access),
      SecureStore.getItemAsync(K.accessExp),
      SecureStore.getItemAsync(K.refresh),
      SecureStore.getItemAsync(K.biometric),
      SecureStore.getItemAsync(K.lastUsername),
    ]);
    const biometricEnabled = bio === '1';
    set({
      serverUrl: server ?? '',
      lastUsername: lastUser ?? '',
      accessToken: access,
      accessExpiresAt: exp ? Number(exp) : 0,
      refreshToken: refresh,
      biometricEnabled,
      // Lock on cold start when a session exists and biometric unlock is on.
      locked: biometricEnabled && !!refresh,
    });
    return { hasSession: !!refresh && !!server };
  },

  setServerUrl: async (url) => {
    const v = normalizeServerUrl(url);
    await SecureStore.setItemAsync(K.server, v);
    set({ serverUrl: v });
  },

  setLastUsername: async (u) => {
    await SecureStore.setItemAsync(K.lastUsername, u);
    set({ lastUsername: u });
  },

  setSession: async (r) => {
    const exp = Date.parse(r.accessTokenExpiresAt) || Date.now() + 10 * 60_000;
    await Promise.all([
      SecureStore.setItemAsync(K.access, r.accessToken),
      SecureStore.setItemAsync(K.accessExp, String(exp)),
      SecureStore.setItemAsync(K.refresh, r.refreshToken),
    ]);
    set({
      accessToken: r.accessToken,
      accessExpiresAt: exp,
      refreshToken: r.refreshToken,
      user: r.user,
      mustChangePassword: r.user.mustChangePassword,
    });
  },

  setUser: (u) => set({ user: u, mustChangePassword: u.mustChangePassword }),
  setMustChangePassword: (v) => set({ mustChangePassword: v }),
  markSignedIn: () => set({ status: 'signedIn' }),

  clearSession: async () => {
    await Promise.all([
      SecureStore.deleteItemAsync(K.access),
      SecureStore.deleteItemAsync(K.accessExp),
      SecureStore.deleteItemAsync(K.refresh),
    ]);
    set({ accessToken: null, accessExpiresAt: 0, refreshToken: null, user: null, mustChangePassword: false, status: 'signedOut', locked: false });
  },

  setBiometricEnabled: async (v) => {
    await SecureStore.setItemAsync(K.biometric, v ? '1' : '0');
    set({ biometricEnabled: v });
  },

  setLocked: (v) => set({ locked: v && get().biometricEnabled }),
}));

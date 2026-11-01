import * as LocalAuthentication from 'expo-local-authentication';
import { useCallback, useEffect, useRef } from 'react';
import { AppState, StyleSheet, Text, View } from 'react-native';

import { Button, Icon } from '@/components/ui';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';
import { signOut } from './bootstrap';

/** Re-lock after this long in the background (pickers/share sheets briefly background the app). */
const RELOCK_AFTER_MS = 30_000;

export async function authenticateBiometric(prompt: string): Promise<boolean> {
  const res = await LocalAuthentication.authenticateAsync({
    promptMessage: prompt,
    cancelLabel: t('common.cancel'),
    fallbackLabel: t('lock.fallback'),
    disableDeviceFallback: false,
  });
  return res.success;
}

export async function biometricAvailable(): Promise<boolean> {
  const [hw, enrolled] = await Promise.all([LocalAuthentication.hasHardwareAsync(), LocalAuthentication.isEnrolledAsync()]);
  return hw && enrolled;
}

/** Tracks app background/foreground transitions and locks the app when biometric unlock is enabled. */
export function useAutoLock() {
  const backgroundAt = useRef<number | null>(null);
  useEffect(() => {
    const sub = AppState.addEventListener('change', (state) => {
      if (state === 'background') backgroundAt.current = Date.now();
      else if (state === 'active' && backgroundAt.current) {
        const away = Date.now() - backgroundAt.current;
        backgroundAt.current = null;
        const s = useAuth.getState();
        if (away >= RELOCK_AFTER_MS && s.biometricEnabled && s.status === 'signedIn') s.setLocked(true);
      }
    });
    return () => sub.remove();
  }, []);
}

/** Full-screen overlay asking for fingerprint / Face ID. */
export function LockScreen() {
  const c = useColors();
  const setLocked = useAuth((s) => s.setLocked);
  const busy = useRef(false);

  const unlock = useCallback(async () => {
    if (busy.current) return;
    busy.current = true;
    try {
      if (await authenticateBiometric(t('lock.prompt'))) setLocked(false);
    } finally {
      busy.current = false;
    }
  }, [setLocked]);

  useEffect(() => {
    unlock();
  }, [unlock]);

  return (
    <View style={[StyleSheet.absoluteFill, styles.wrap, { backgroundColor: c.primary }]}>
      <View style={styles.logo}>
        <Text style={styles.logoText}>B</Text>
      </View>
      <Text style={styles.title}>{t('lock.title')}</Text>
      <Text style={styles.subtitle}>{t('lock.subtitle')}</Text>
      <Icon name="fingerprint" size={64} color="#fff" style={{ marginVertical: 24 }} />
      <Button title={t('lock.unlock')} onPress={unlock} variant="secondary" style={{ minWidth: 200 }} />
      <Button
        title={t('lock.logout')}
        variant="ghost"
        onPress={() => signOut()}
        style={{ marginTop: 8 }}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { alignItems: 'center', justifyContent: 'center', padding: 24, zIndex: 100, elevation: 100 },
  logo: { width: 84, height: 84, borderRadius: 20, backgroundColor: '#fff', alignItems: 'center', justifyContent: 'center' },
  logoText: { color: '#0b5394', fontSize: 52, fontWeight: '900' },
  title: { color: '#fff', fontSize: 22, fontWeight: '700', marginTop: 20 },
  subtitle: { color: 'rgba(255,255,255,0.85)', marginTop: 6 },
});

import * as Application from 'expo-application';
import { router } from 'expo-router';
import { useEffect, useState } from 'react';
import { Alert, Platform, ScrollView, Switch, Text, useColorScheme } from 'react-native';

import { APP_VERSION } from '@/api/client';
import { ListRow, SectionHeader } from '@/components/ui';
import { signOut } from '@/features/bootstrap';
import { authenticateBiometric, biometricAvailable } from '@/features/LockScreen';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { toast } from '@/store/toast';
import { useColors } from '@/theme';
import { formatDate } from '@/utils/format';

/** Cài đặt: máy chủ, tài khoản, sinh trắc học, giao diện, phiên bản, đăng xuất. */
export default function SettingsScreen() {
  const c = useColors();
  const scheme = useColorScheme();
  const server = useAuth((s) => s.serverUrl);
  const user = useAuth((s) => s.user);
  const biometric = useAuth((s) => s.biometricEnabled);
  const [bioAvailable, setBioAvailable] = useState<boolean | null>(null);

  useEffect(() => {
    biometricAvailable().then(setBioAvailable).catch(() => setBioAvailable(false));
  }, []);

  const toggleBiometric = async (on: boolean) => {
    if (on) {
      if (!bioAvailable) {
        toast.error(t('settings.biometricUnavailable'));
        return;
      }
      // Confirm the user can actually unlock before enabling the lock.
      if (!(await authenticateBiometric(t('settings.biometricConfirm')))) return;
    }
    await useAuth.getState().setBiometricEnabled(on);
  };

  const build = Application.nativeBuildVersion ? ` (${Application.nativeBuildVersion})` : '';

  return (
    <ScrollView style={{ backgroundColor: c.background }} contentContainerStyle={{ paddingBottom: 32 }}>
      <SectionHeader title={t('settings.server')} />
      <ListRow icon="server-network" title={server} subtitle={t('settings.changeServerHint')} />

      <SectionHeader title={t('settings.account')} />
      <ListRow icon="account-circle-outline" title={user?.fullName ?? ''} subtitle={`${t('settings.username')}: ${user?.username ?? ''}`} />
      {user?.email ? <ListRow icon="email-outline" title={user.email} subtitle={t('settings.email')} /> : null}
      {user?.organizationName ? (
        <ListRow icon="office-building-outline" title={user.organizationName} subtitle={[t('settings.organization'), user.position].filter(Boolean).join(' • ')} />
      ) : null}
      {user?.roles?.length ? <ListRow icon="shield-account-outline" title={user.roles.join(', ')} subtitle={t('settings.roles')} /> : null}
      {user?.lastLoginAt ? <ListRow icon="clock-outline" title={formatDate(user.lastLoginAt)} subtitle={t('settings.lastLogin')} /> : null}

      <SectionHeader title={t('settings.security')} />
      <ListRow
        icon={Platform.OS === 'ios' ? 'face-recognition' : 'fingerprint'}
        title={t('settings.biometric')}
        subtitle={bioAvailable === false ? t('settings.biometricUnavailable') : t('settings.biometricHint')}
        right={<Switch value={biometric} onValueChange={toggleBiometric} disabled={bioAvailable === null} trackColor={{ true: c.primary }} />}
      />
      <ListRow icon="lock-reset" title={t('settings.changePassword')} onPress={() => router.push('/change-password')} />

      <SectionHeader title={t('settings.appearance')} />
      <ListRow
        icon="theme-light-dark"
        title={t('settings.darkMode')}
        subtitle={t('settings.darkModeValue', { mode: scheme === 'dark' ? t('settings.dark') : t('settings.light') })}
      />

      <SectionHeader title={t('settings.about')} />
      <ListRow icon="information-outline" title={`${t('app.name')} ${APP_VERSION}${build}`} subtitle={t('settings.version')} />

      <SectionHeader title="" />
      <ListRow
        icon="logout"
        title={t('settings.logout')}
        danger
        onPress={() =>
          Alert.alert(t('settings.logout'), t('settings.logoutConfirm'), [
            { text: t('common.cancel'), style: 'cancel' },
            { text: t('settings.logout'), style: 'destructive', onPress: () => signOut() },
          ])
        }
      />
      <Text style={{ color: c.textMuted, textAlign: 'center', marginTop: 16, fontSize: 12 }}>{t('app.tagline')}</Text>
    </ScrollView>
  );
}

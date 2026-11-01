import { router } from 'expo-router';
import { Alert, ScrollView, Text, View } from 'react-native';

import { APP_VERSION } from '@/api/client';
import { ListRow, SectionHeader } from '@/components/ui';
import { signOut } from '@/features/bootstrap';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { useColors } from '@/theme';

/** Tab "Thêm": Gần đây (#73), Tìm kiếm (#75), Quan trọng, Cài đặt, Đăng xuất. */
export default function MoreScreen() {
  const c = useColors();
  const user = useAuth((s) => s.user);
  return (
    <ScrollView style={{ backgroundColor: c.background }}>
      <View style={{ padding: 16, backgroundColor: c.surface, borderBottomColor: c.border, borderBottomWidth: 0.5 }}>
        <Text style={{ color: c.text, fontSize: 18, fontWeight: '700' }}>{user?.fullName}</Text>
        <Text style={{ color: c.textMuted, marginTop: 2 }}>
          {[user?.username, user?.organizationName].filter(Boolean).join(' • ')}
        </Text>
      </View>
      <SectionHeader title={t('titles.more')} />
      <ListRow icon="history" title={t('more.recent')} onPress={() => router.push('/recent')} />
      <ListRow icon="magnify" title={t('more.search')} onPress={() => router.push('/search')} />
      <ListRow icon="star-outline" title={t('more.starred')} onPress={() => router.push('/starred')} />
      <ListRow icon="cog-outline" title={t('more.settings')} onPress={() => router.push('/settings')} />
      <SectionHeader title={t('settings.account')} />
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
      <Text style={{ color: c.textMuted, textAlign: 'center', marginVertical: 20, fontSize: 12 }}>
        {t('app.name')} {APP_VERSION}
      </Text>
    </ScrollView>
  );
}

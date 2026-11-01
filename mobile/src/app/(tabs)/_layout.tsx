import { router } from 'expo-router';
import { Tabs } from 'expo-router/js-tabs';
import { Pressable, type ColorValue } from 'react-native';

import { Icon, type IconName } from '@/components/ui';
import { t } from '@/i18n';
import { useColors } from '@/theme';

function tabIcon(name: IconName, focusedName: IconName) {
  return function TabIcon({ color, focused, size }: { color: ColorValue; focused: boolean; size: number }) {
    return <Icon name={focused ? focusedName : name} color={color} size={size} />;
  };
}

export default function TabsLayout() {
  const c = useColors();
  const searchButton = () => (
    <Pressable onPress={() => router.push('/search')} hitSlop={10} style={{ paddingHorizontal: 16 }} accessibilityLabel={t('titles.search')}>
      <Icon name="magnify" color={c.text} />
    </Pressable>
  );
  return (
    <Tabs
      screenOptions={{
        tabBarActiveTintColor: c.primary,
        tabBarInactiveTintColor: c.textMuted,
        tabBarStyle: { backgroundColor: c.surface, borderTopColor: c.border },
        headerStyle: { backgroundColor: c.surface },
        headerTintColor: c.text,
        headerRight: searchButton,
      }}
    >
      <Tabs.Screen name="index" options={{ title: t('tabs.home'), headerTitle: t('app.name'), tabBarIcon: tabIcon('home-outline', 'home') }} />
      <Tabs.Screen name="files" options={{ title: t('tabs.files'), headerTitle: t('titles.myFiles'), tabBarIcon: tabIcon('folder-outline', 'folder') }} />
      <Tabs.Screen name="shared" options={{ title: t('tabs.shared'), headerTitle: t('titles.shared'), tabBarIcon: tabIcon('account-multiple-outline', 'account-multiple') }} />
      <Tabs.Screen name="groups" options={{ title: t('tabs.groups'), headerTitle: t('titles.groups'), tabBarIcon: tabIcon('account-group-outline', 'account-group') }} />
      <Tabs.Screen name="more" options={{ title: t('tabs.more'), headerTitle: t('titles.more'), tabBarIcon: tabIcon('dots-horizontal-circle-outline', 'dots-horizontal-circle') }} />
    </Tabs>
  );
}

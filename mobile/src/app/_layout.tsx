import { QueryClientProvider } from '@tanstack/react-query';
import { DarkTheme, DefaultTheme, Stack, ThemeProvider } from 'expo-router';
import * as SplashScreen from 'expo-splash-screen';
import { StatusBar } from 'expo-status-bar';
import { useCallback, useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { GestureHandlerRootView } from 'react-native-gesture-handler';
import { SafeAreaProvider } from 'react-native-safe-area-context';

import { TransferBanner } from '@/components/TransferBanner';
import { Button, Icon, ToastHost } from '@/components/ui';
import { bootstrapSession, signOut } from '@/features/bootstrap';
import { ItemActionsProvider } from '@/features/ItemActions';
import { LockScreen, useAutoLock } from '@/features/LockScreen';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { queryClient } from '@/store/query';
import { useColors } from '@/theme';

SplashScreen.preventAutoHideAsync().catch(() => undefined);

function BootError({ message, onRetry }: { message: string; onRetry: () => void }) {
  const c = useColors();
  return (
    <View style={[StyleSheet.absoluteFill, styles.boot, { backgroundColor: c.background }]}>
      <Icon name="cloud-off-outline" size={64} color={c.textMuted} />
      <Text style={{ color: c.text, fontSize: 16, textAlign: 'center', marginVertical: 16 }}>{message}</Text>
      <Button title={t('common.retry')} onPress={onRetry} icon="refresh" style={{ minWidth: 200 }} />
      <Button title={t('settings.logout')} variant="ghost" onPress={() => signOut()} style={{ marginTop: 8 }} />
    </View>
  );
}

export default function RootLayout() {
  const c = useColors();
  const status = useAuth((s) => s.status);
  const mustChange = useAuth((s) => s.mustChangePassword);
  const locked = useAuth((s) => s.locked);
  const [bootError, setBootError] = useState<string | null>(null);

  const runBoot = useCallback(
    () =>
      bootstrapSession().then((r) => {
        setBootError(r.error ?? null);
        SplashScreen.hideAsync().catch(() => undefined);
      }),
    [],
  );

  useEffect(() => {
    runBoot();
  }, [runBoot]);

  const boot = () => {
    setBootError(null);
    runBoot();
  };

  useAutoLock();

  const signedIn = status === 'signedIn';
  const base = c.dark ? DarkTheme : DefaultTheme;
  const theme = {
    ...base,
    colors: { ...base.colors, primary: c.primary, background: c.background, card: c.surface, text: c.text, border: c.border },
  };

  return (
    <GestureHandlerRootView style={{ flex: 1 }}>
      <SafeAreaProvider>
        <QueryClientProvider client={queryClient}>
          <ThemeProvider value={theme}>
            <ItemActionsProvider>
              <StatusBar style={c.dark ? 'light' : 'dark'} />
              <Stack
                screenOptions={{
                  headerStyle: { backgroundColor: c.surface },
                  headerTintColor: c.text,
                  headerTitleStyle: { fontWeight: '600' },
                  headerBackButtonDisplayMode: 'minimal',
                  contentStyle: { backgroundColor: c.background },
                }}
              >
                <Stack.Protected guard={status !== 'signedIn'}>
                  <Stack.Screen name="login" options={{ headerShown: false }} />
                </Stack.Protected>
                <Stack.Protected guard={signedIn && !mustChange}>
                  <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
                  <Stack.Screen name="folder/[workspaceId]" options={{ title: '' }} />
                  <Stack.Screen name="tree/[workspaceId]" options={{ title: t('titles.tree') }} />
                  <Stack.Screen name="file/[id]" options={{ title: t('titles.fileDetail') }} />
                  <Stack.Screen name="viewer/[id]" options={{ title: t('titles.viewer') }} />
                  <Stack.Screen name="recent" options={{ title: t('titles.recent') }} />
                  <Stack.Screen name="search" options={{ title: t('titles.search') }} />
                  <Stack.Screen name="starred" options={{ title: t('titles.starred') }} />
                  <Stack.Screen name="settings" options={{ title: t('titles.settings') }} />
                </Stack.Protected>
                <Stack.Protected guard={signedIn}>
                  <Stack.Screen name="change-password" options={{ title: t('titles.changePassword') }} />
                </Stack.Protected>
              </Stack>
              <TransferBanner />
              <ToastHost />
              {bootError && status === 'loading' ? <BootError message={bootError} onRetry={boot} /> : null}
              {locked && signedIn ? <LockScreen /> : null}
            </ItemActionsProvider>
          </ThemeProvider>
        </QueryClientProvider>
      </SafeAreaProvider>
    </GestureHandlerRootView>
  );
}

const styles = StyleSheet.create({
  boot: { alignItems: 'center', justifyContent: 'center', padding: 24, zIndex: 90 },
});

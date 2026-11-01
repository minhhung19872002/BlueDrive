import { useState } from 'react';
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { APP_VERSION, errorMessage } from '@/api/client';
import { authApi } from '@/api/endpoints';
import { Button, Icon, TextField } from '@/components/ui';
import { completeSignIn } from '@/features/bootstrap';
import { t } from '@/i18n';
import { normalizeServerUrl, useAuth } from '@/store/auth';
import { useColors } from '@/theme';

/** Đăng nhập: địa chỉ máy chủ (được ghi nhớ) + tên đăng nhập/mật khẩu. */
export default function LoginScreen() {
  const c = useColors();
  const savedServer = useAuth((s) => s.serverUrl);
  const lastUsername = useAuth((s) => s.lastUsername);
  const [server, setServer] = useState(savedServer);
  const [username, setUsername] = useState(lastUsername);
  const [password, setPassword] = useState('');
  const [showPwd, setShowPwd] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErr, setFieldErr] = useState<{ server?: string; username?: string; password?: string }>({});

  const submit = async () => {
    const url = normalizeServerUrl(server);
    const errs: typeof fieldErr = {};
    if (!url) errs.server = t('login.serverRequired');
    else if (!/^https?:\/\/[^\s/]+(\/[^\s]*)?$/i.test(url)) errs.server = t('login.serverInvalid');
    if (!username.trim()) errs.username = t('login.usernameRequired');
    if (!password) errs.password = t('login.passwordRequired');
    setFieldErr(errs);
    setError(null);
    if (Object.keys(errs).length) return;

    setBusy(true);
    try {
      await useAuth.getState().setServerUrl(url);
      setServer(url);
      const result = await authApi.login(username.trim(), password);
      await useAuth.getState().setLastUsername(username.trim());
      setPassword('');
      await completeSignIn(result);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <SafeAreaView style={{ flex: 1, backgroundColor: c.background }}>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1 }}>
        <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps="handled">
          <View style={styles.brand}>
            <View style={[styles.logo, { backgroundColor: c.primary }]}>
              <Text style={styles.logoText}>B</Text>
            </View>
            <Text style={[styles.appName, { color: c.text }]}>{t('app.name')}</Text>
            <Text style={{ color: c.textMuted }}>{t('app.tagline')}</Text>
          </View>

          <View style={[styles.card, { backgroundColor: c.surface, borderColor: c.border }]}>
            <Text style={[styles.title, { color: c.text }]}>{t('login.title')}</Text>
            <TextField
              label={t('login.server')}
              placeholder={t('login.serverPlaceholder')}
              value={server}
              onChangeText={setServer}
              autoCapitalize="none"
              autoCorrect={false}
              keyboardType="url"
              textContentType="URL"
              error={fieldErr.server}
            />
            <TextField
              label={t('login.username')}
              value={username}
              onChangeText={setUsername}
              autoCapitalize="none"
              autoCorrect={false}
              textContentType="username"
              autoComplete="username"
              error={fieldErr.username}
            />
            <View>
              <TextField
                label={t('login.password')}
                value={password}
                onChangeText={setPassword}
                secureTextEntry={!showPwd}
                textContentType="password"
                autoComplete="password"
                returnKeyType="go"
                onSubmitEditing={submit}
                error={fieldErr.password}
                style={{ paddingRight: 44 }}
              />
              <Pressable
                onPress={() => setShowPwd((v) => !v)}
                style={styles.eye}
                hitSlop={10}
                accessibilityLabel={showPwd ? t('login.hidePassword') : t('login.showPassword')}
              >
                <Icon name={showPwd ? 'eye-off-outline' : 'eye-outline'} color={c.textMuted} />
              </Pressable>
            </View>
            {error ? <Text style={[styles.error, { color: c.danger }]}>{error}</Text> : null}
            <Button title={busy ? t('login.submitting') : t('login.submit')} onPress={submit} loading={busy} icon="login" />
            <Text style={{ color: c.textMuted, fontSize: 12, marginTop: 12 }}>{t('login.serverHint')}</Text>
          </View>
          <Text style={{ color: c.textMuted, textAlign: 'center', marginTop: 20, fontSize: 12 }}>
            {t('settings.version')} {APP_VERSION}
          </Text>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flexGrow: 1, justifyContent: 'center', padding: 20 },
  brand: { alignItems: 'center', marginBottom: 24 },
  logo: { width: 76, height: 76, borderRadius: 18, alignItems: 'center', justifyContent: 'center', marginBottom: 12 },
  logoText: { color: '#fff', fontSize: 46, fontWeight: '900' },
  appName: { fontSize: 26, fontWeight: '800' },
  card: { borderRadius: 16, borderWidth: StyleSheet.hairlineWidth, padding: 20 },
  title: { fontSize: 20, fontWeight: '700', marginBottom: 16 },
  eye: { position: 'absolute', right: 12, top: 36 },
  error: { marginBottom: 12, fontSize: 14 },
});

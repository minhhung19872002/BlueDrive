import { useQuery } from '@tanstack/react-query';
import { router } from 'expo-router';
import { useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, Text } from 'react-native';

import { errorMessage } from '@/api/client';
import { authApi } from '@/api/endpoints';
import { Button, TextField } from '@/components/ui';
import { signOut } from '@/features/bootstrap';
import { t } from '@/i18n';
import { useAuth } from '@/store/auth';
import { toast } from '@/store/toast';
import { useColors } from '@/theme';

/** Bắt buộc đổi mật khẩu (lần đầu / hết hạn) hoặc đổi chủ động từ Cài đặt. */
export default function ChangePasswordScreen() {
  const c = useColors();
  const mustChange = useAuth((s) => s.mustChangePassword);
  const policy = useQuery({ queryKey: ['password-policy'], queryFn: authApi.passwordPolicy });
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const p = policy.data;
  const rules = p
    ? t('changePassword.policy', {
        min: p.minLength,
        rules: `${p.requireUpper ? t('changePassword.upper') : ''}${p.requireLower ? t('changePassword.lower') : ''}${p.requireDigit ? t('changePassword.digit') : ''}${p.requireSpecial ? t('changePassword.special') : ''}`,
      })
    : '';

  const submit = async () => {
    setError(null);
    if (!current || !next || !confirm) return setError(t('changePassword.required'));
    if (next !== confirm) return setError(t('changePassword.mismatch'));
    setBusy(true);
    try {
      const result = await authApi.changePassword(current, next);
      await useAuth.getState().setSession(result); // new token pair without the pwd_change claim
      toast.success(t('changePassword.success'));
      // A forced change leaves the user on this screen (still allowed when signed in), so go home explicitly.
      if (mustChange) router.replace('/(tabs)');
      else if (router.canGoBack()) router.back();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1, backgroundColor: c.background }}>
      <ScrollView contentContainerStyle={{ padding: 20 }} keyboardShouldPersistTaps="handled">
        {mustChange ? <Text style={{ color: c.warning, marginBottom: 16, fontSize: 15 }}>{t('changePassword.intro')}</Text> : null}
        <TextField label={t('changePassword.current')} value={current} onChangeText={setCurrent} secureTextEntry textContentType="password" />
        <TextField label={t('changePassword.next')} value={next} onChangeText={setNext} secureTextEntry textContentType="newPassword" />
        <TextField label={t('changePassword.confirm')} value={confirm} onChangeText={setConfirm} secureTextEntry textContentType="newPassword" onSubmitEditing={submit} />
        {rules ? <Text style={{ color: c.textMuted, fontSize: 13, marginBottom: 12 }}>{rules}</Text> : null}
        {error ? <Text style={{ color: c.danger, marginBottom: 12 }}>{error}</Text> : null}
        <Button title={t('changePassword.submit')} onPress={submit} loading={busy} icon="lock-reset" />
        {mustChange ? <Button title={t('changePassword.logout')} variant="ghost" onPress={() => signOut()} style={{ marginTop: 10 }} /> : null}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

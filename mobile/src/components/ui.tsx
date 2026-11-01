import MaterialCommunityIcons from '@expo/vector-icons/MaterialCommunityIcons';
import { useEffect, type ComponentProps, type ReactNode } from 'react';
import {
  ActivityIndicator, Pressable, type ColorValue, StyleSheet, Text, TextInput, View, type StyleProp, type TextInputProps, type TextStyle, type ViewStyle,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { t } from '@/i18n';
import { useToast } from '@/store/toast';
import { useColors } from '@/theme';

export type IconName = ComponentProps<typeof MaterialCommunityIcons>['name'];

export function Icon({ name, size = 22, color, style }: { name: IconName; size?: number; color?: ColorValue; style?: StyleProp<TextStyle> }) {
  const c = useColors();
  return <MaterialCommunityIcons name={name} size={size} color={(color ?? c.text) as string} style={style} />;
}

export function Button({
  title, onPress, variant = 'primary', loading, disabled, icon, style,
}: {
  title: string;
  onPress: () => void;
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost';
  loading?: boolean;
  disabled?: boolean;
  icon?: IconName;
  style?: StyleProp<ViewStyle>;
}) {
  const c = useColors();
  const bg = variant === 'primary' ? c.primary : variant === 'danger' ? c.danger : variant === 'secondary' ? c.surfaceAlt : 'transparent';
  const fg = variant === 'primary' || variant === 'danger' ? c.primaryText : variant === 'ghost' ? c.primary : c.text;
  const off = disabled || loading;
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      disabled={off}
      style={({ pressed }) => [styles.button, { backgroundColor: bg, opacity: off ? 0.6 : pressed ? 0.85 : 1 }, style]}
    >
      {loading ? <ActivityIndicator color={fg} /> : icon ? <Icon name={icon} size={20} color={fg} /> : null}
      <Text style={[styles.buttonText, { color: fg }]}>{title}</Text>
    </Pressable>
  );
}

export function TextField({ label, error, style, ...props }: TextInputProps & { label?: string; error?: string | null }) {
  const c = useColors();
  return (
    <View style={{ marginBottom: 14 }}>
      {label ? <Text style={[styles.label, { color: c.textMuted }]}>{label}</Text> : null}
      <TextInput
        placeholderTextColor={c.textMuted}
        style={[styles.input, { color: c.text, backgroundColor: c.surface, borderColor: error ? c.danger : c.border }, style]}
        {...props}
      />
      {error ? <Text style={[styles.error, { color: c.danger }]}>{error}</Text> : null}
    </View>
  );
}

export function Centered({ children }: { children: ReactNode }) {
  return <View style={styles.centered}>{children}</View>;
}

export function LoadingView({ label }: { label?: string }) {
  const c = useColors();
  return (
    <Centered>
      <ActivityIndicator size="large" color={c.primary} />
      <Text style={{ color: c.textMuted, marginTop: 12 }}>{label ?? t('common.loading')}</Text>
    </Centered>
  );
}

export function EmptyState({ icon = 'folder-open-outline', title, hint }: { icon?: IconName; title: string; hint?: string }) {
  const c = useColors();
  return (
    <View style={styles.empty}>
      <Icon name={icon} size={56} color={c.textMuted} />
      <Text style={[styles.emptyTitle, { color: c.text }]}>{title}</Text>
      {hint ? <Text style={{ color: c.textMuted, textAlign: 'center', marginTop: 4 }}>{hint}</Text> : null}
    </View>
  );
}

export function ErrorState({ message, onRetry }: { message: string; onRetry?: () => void }) {
  const c = useColors();
  return (
    <View style={styles.empty}>
      <Icon name="alert-circle-outline" size={56} color={c.danger} />
      <Text style={[styles.emptyTitle, { color: c.text, textAlign: 'center' }]}>{message}</Text>
      {onRetry ? <Button title={t('common.retry')} onPress={onRetry} variant="secondary" style={{ marginTop: 12 }} icon="refresh" /> : null}
    </View>
  );
}

export function SectionHeader({ title, right }: { title: string; right?: ReactNode }) {
  const c = useColors();
  return (
    <View style={styles.sectionHeader}>
      <Text style={[styles.sectionTitle, { color: c.textMuted }]}>{title.toUpperCase()}</Text>
      {right}
    </View>
  );
}

export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const c = useColors();
  return <View style={[styles.card, { backgroundColor: c.surface, borderColor: c.border }, style]}>{children}</View>;
}

export function ListRow({
  icon, title, subtitle, onPress, right, danger,
}: { icon?: IconName; title: string; subtitle?: string; onPress?: () => void; right?: ReactNode; danger?: boolean }) {
  const c = useColors();
  return (
    <Pressable
      onPress={onPress}
      disabled={!onPress}
      style={({ pressed }) => [styles.listRow, { backgroundColor: pressed ? c.surfaceAlt : c.surface, borderBottomColor: c.border }]}
    >
      {icon ? <Icon name={icon} size={22} color={danger ? c.danger : c.primary} /> : null}
      <View style={{ flex: 1, marginLeft: icon ? 14 : 0 }}>
        <Text style={{ color: danger ? c.danger : c.text, fontSize: 16 }}>{title}</Text>
        {subtitle ? <Text style={{ color: c.textMuted, fontSize: 13, marginTop: 2 }}>{subtitle}</Text> : null}
      </View>
      {right ?? (onPress ? <Icon name="chevron-right" color={c.textMuted} /> : null)}
    </Pressable>
  );
}

export function ProgressBar({ value, color, height = 6 }: { value: number; color?: string; height?: number }) {
  const c = useColors();
  const pct = Math.max(0, Math.min(1, value));
  return (
    <View style={{ height, borderRadius: height / 2, backgroundColor: c.surfaceAlt, overflow: 'hidden' }}>
      <View style={{ width: `${pct * 100}%`, height, backgroundColor: color ?? c.primary }} />
    </View>
  );
}

/** Global toast host (mounted once in the root layout). */
export function ToastHost() {
  const { message, kind, key, hide } = useToast();
  const c = useColors();
  const insets = useSafeAreaInsets();
  useEffect(() => {
    if (!message) return;
    const id = setTimeout(hide, kind === 'error' ? 5000 : 2800);
    return () => clearTimeout(id);
  }, [message, key, kind, hide]);
  if (!message) return null;
  const bg = kind === 'error' ? c.danger : kind === 'success' ? c.success : c.dark ? '#333c47' : '#323a45';
  return (
    <Pressable onPress={hide} style={[styles.toast, { bottom: insets.bottom + 72, backgroundColor: bg }]}>
      <Text style={{ color: '#fff', fontSize: 14 }}>{message}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    minHeight: 46, borderRadius: 10, paddingHorizontal: 18, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8,
  },
  buttonText: { fontSize: 16, fontWeight: '600' },
  label: { fontSize: 13, marginBottom: 6, fontWeight: '500' },
  input: { borderWidth: 1, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 11, fontSize: 16 },
  error: { fontSize: 13, marginTop: 4 },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  empty: { alignItems: 'center', justifyContent: 'center', paddingVertical: 60, paddingHorizontal: 24 },
  emptyTitle: { fontSize: 16, fontWeight: '600', marginTop: 12 },
  sectionHeader: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: 16, paddingTop: 20, paddingBottom: 8 },
  sectionTitle: { fontSize: 12, fontWeight: '700', letterSpacing: 0.5 },
  card: { borderRadius: 14, borderWidth: StyleSheet.hairlineWidth, padding: 16 },
  listRow: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 14, borderBottomWidth: StyleSheet.hairlineWidth },
  toast: {
    position: 'absolute', left: 16, right: 16, borderRadius: 10, paddingHorizontal: 16, paddingVertical: 12, elevation: 6,
    shadowColor: '#000', shadowOpacity: 0.25, shadowRadius: 8, shadowOffset: { width: 0, height: 2 },
  },
});

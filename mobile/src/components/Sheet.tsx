import type { ReactNode } from 'react';
import { KeyboardAvoidingView, Modal, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { useColors } from '@/theme';
import { Icon, type IconName } from './ui';

/** Bottom sheet built on RN Modal (no extra native deps). */
export function Sheet({ visible, onClose, title, children }: { visible: boolean; onClose: () => void; title?: string; children: ReactNode }) {
  const c = useColors();
  const insets = useSafeAreaInsets();
  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose} statusBarTranslucent>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1 }}>
        <Pressable style={[StyleSheet.absoluteFill, { backgroundColor: c.overlay }]} onPress={onClose} />
        <View style={{ flex: 1 }} pointerEvents="box-none" />
        <View style={[styles.sheet, { backgroundColor: c.surface, paddingBottom: insets.bottom + 8 }]}>
          <View style={[styles.grabber, { backgroundColor: c.border }]} />
          {title ? (
            <Text numberOfLines={2} style={[styles.title, { color: c.text, borderBottomColor: c.border }]}>
              {title}
            </Text>
          ) : null}
          {children}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

export function SheetItem({ icon, label, onPress, danger, disabled }: { icon: IconName; label: string; onPress: () => void; danger?: boolean; disabled?: boolean }) {
  const c = useColors();
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      style={({ pressed }) => [styles.item, { backgroundColor: pressed ? c.surfaceAlt : 'transparent', opacity: disabled ? 0.4 : 1 }]}
    >
      <Icon name={icon} color={danger ? c.danger : c.textMuted} />
      <Text style={[styles.itemText, { color: danger ? c.danger : c.text }]}>{label}</Text>
    </Pressable>
  );
}

/** Centered dialog. */
export function Dialog({ visible, onClose, title, children }: { visible: boolean; onClose: () => void; title: string; children: ReactNode }) {
  const c = useColors();
  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onClose} statusBarTranslucent>
      <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : 'height'} style={[styles.dialogWrap, { backgroundColor: c.overlay }]}>
        <View style={[styles.dialog, { backgroundColor: c.surface }]}>
          <Text style={[styles.dialogTitle, { color: c.text }]}>{title}</Text>
          {children}
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  sheet: { borderTopLeftRadius: 18, borderTopRightRadius: 18, paddingTop: 8, maxHeight: '85%' },
  grabber: { alignSelf: 'center', width: 40, height: 4, borderRadius: 2, marginBottom: 6 },
  title: { fontSize: 15, fontWeight: '600', paddingHorizontal: 20, paddingVertical: 10, borderBottomWidth: StyleSheet.hairlineWidth },
  item: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 14 },
  itemText: { fontSize: 16, marginLeft: 18 },
  dialogWrap: { flex: 1, justifyContent: 'center', padding: 24 },
  dialog: { borderRadius: 16, padding: 20 },
  dialogTitle: { fontSize: 18, fontWeight: '700', marginBottom: 16 },
});

import { useState } from 'react';
import { View } from 'react-native';

import { errorMessage } from '@/api/client';
import { t } from '@/i18n';
import { Dialog } from './Sheet';
import { Button, TextField } from './ui';

interface Props {
  visible: boolean;
  title: string;
  label: string;
  initialValue?: string;
  submitLabel?: string;
  onSubmit: (value: string) => Promise<void>;
  onClose: () => void;
}

/** Body is mounted only while visible, so its state starts fresh every time. */
function PromptBody({ label, initialValue, submitLabel, onSubmit, onClose }: Omit<Props, 'visible' | 'title'>) {
  const [value, setValue] = useState(initialValue ?? '');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const v = value.trim();
    if (!v) {
      setError(t('common.required', { field: label.toLowerCase() }));
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSubmit(v);
      onClose();
    } catch (e) {
      setError(errorMessage(e));
      setBusy(false);
    }
  };

  return (
    <>
      <TextField label={label} value={value} onChangeText={setValue} autoFocus error={error} returnKeyType="done" onSubmitEditing={submit} />
      <View style={{ flexDirection: 'row', justifyContent: 'flex-end', gap: 10 }}>
        <Button title={t('common.cancel')} variant="ghost" onPress={onClose} />
        <Button title={submitLabel ?? t('common.save')} onPress={submit} loading={busy} />
      </View>
    </>
  );
}

/** Cross-platform text prompt (Alert.prompt is iOS-only). */
export function PromptDialog({ visible, title, ...rest }: Props) {
  return (
    <Dialog visible={visible} onClose={rest.onClose} title={title}>
      {visible ? <PromptBody {...rest} /> : null}
    </Dialog>
  );
}

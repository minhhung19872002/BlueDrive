import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';

import { useAuth } from '@/store/auth';

/**
 * Spec 11.9: what is viewed "Chỉ xem" or under a label requiring it carries the viewer's name, account and the time, repeated over the
 * content. It does not stop a photo of the screen; it makes one traceable. Touches pass through.
 */
export function WatermarkOverlay() {
  const user = useAuth((s) => s.user);
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 60_000);
    return () => clearInterval(id);
  }, []);
  const text = `${user?.fullName ?? ''} · ${user?.email ?? user?.username ?? ''} · ${now.toLocaleString('vi-VN')}`;
  return (
    <View pointerEvents="none" style={styles.layer}>
      {Array.from({ length: 14 }, (_, i) => (
        <Text key={i} numberOfLines={1} style={[styles.line, { marginLeft: (i % 2) * 60 - 40 }]}>
          {`${text}     ${text}`}
        </Text>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  layer: {
    ...StyleSheet.absoluteFill,
    overflow: 'hidden',
    justifyContent: 'space-around',
    transform: [{ rotate: '-22deg' }, { scale: 1.4 }],
  },
  line: { color: 'rgba(120,120,120,0.30)', fontSize: 13, width: 900 },
});

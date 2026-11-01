import { Text, type StyleProp, type TextStyle } from 'react-native';

import { useColors } from '@/theme';

const ENTITIES: Record<string, string> = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'", '&#x27;': "'" };
const decode = (s: string) => s.replace(/&(amp|lt|gt|quot|#39|#x27);/g, (m) => ENTITIES[m] ?? m);

/** Renders server highlights: parts wrapped in <mark>…</mark> are shown bold. */
export function HighlightedText({ html, style, numberOfLines }: { html: string; style?: StyleProp<TextStyle>; numberOfLines?: number }) {
  const c = useColors();
  const parts = html.split(/(<mark>[\s\S]*?<\/mark>)/gi).filter(Boolean);
  return (
    <Text style={style} numberOfLines={numberOfLines}>
      {parts.map((p, i) => {
        const m = /^<mark>([\s\S]*?)<\/mark>$/i.exec(p);
        if (m) {
          return (
            <Text key={i} style={{ fontWeight: '800', backgroundColor: c.highlight }}>
              {decode(m[1].replace(/<[^>]+>/g, ''))}
            </Text>
          );
        }
        return <Text key={i}>{decode(p.replace(/<[^>]+>/g, ''))}</Text>;
      })}
    </Text>
  );
}

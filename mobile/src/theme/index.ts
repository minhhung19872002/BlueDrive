import { useColorScheme } from 'react-native';

export const BRAND = '#0b5394';

const light = {
  dark: false,
  primary: BRAND,
  primaryText: '#ffffff',
  background: '#f4f6f9',
  surface: '#ffffff',
  surfaceAlt: '#eef2f7',
  text: '#1b1f24',
  textMuted: '#5f6b7a',
  border: '#dde3ea',
  danger: '#c62828',
  success: '#2e7d32',
  warning: '#b26a00',
  star: '#f2a900',
  overlay: 'rgba(0,0,0,0.45)',
  folder: '#f0b429',
  highlight: '#fff3b0',
};

const dark: typeof light = {
  dark: true,
  primary: '#5b9bd5',
  primaryText: '#ffffff',
  background: '#0f1318',
  surface: '#1a2028',
  surfaceAlt: '#242c36',
  text: '#e8edf2',
  textMuted: '#9aa7b5',
  border: '#2e3742',
  danger: '#ef5350',
  success: '#66bb6a',
  warning: '#ffb74d',
  star: '#ffca28',
  overlay: 'rgba(0,0,0,0.6)',
  folder: '#f5c34d',
  highlight: '#5c4d00',
};

export type Colors = typeof light;

/** Palette following the system appearance (dark mode). */
export function useColors(): Colors {
  return useColorScheme() === 'dark' ? dark : light;
}

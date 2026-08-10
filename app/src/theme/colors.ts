export type Theme = {
  bg: string;
  surface: string;
  text: string;
  textMuted: string;
  border: string;
  accent: string;
  good: string;
  goodBg: string;
  bad: string;
  badBg: string;
  neutralBg: string;
};

export const light: Theme = {
  bg: '#f2f2f7',
  surface: '#ffffff',
  text: '#1c1c1e',
  textMuted: '#6e6e73',
  border: '#e5e5ea',
  accent: '#0a84ff',
  good: '#1f9254',
  goodBg: '#e3f6ea',
  bad: '#d5372e',
  badBg: '#fbe6e4',
  neutralBg: '#ececef',
};

export const dark: Theme = {
  bg: '#000000',
  surface: '#1c1c1e',
  text: '#f2f2f7',
  textMuted: '#98989d',
  border: '#38383a',
  accent: '#0a84ff',
  good: '#30d158',
  goodBg: 'rgba(48, 209, 88, 0.16)',
  bad: '#ff453a',
  badBg: 'rgba(255, 69, 58, 0.16)',
  neutralBg: '#2c2c2e',
};

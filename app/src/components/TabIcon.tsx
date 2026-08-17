import React from 'react';
import Svg, { Circle, Line, Path } from 'react-native-svg';

// Line-style tab-bar icons, ported 1:1 from the inline <svg> markup in
// mocks/02-debit-list.html / 04-payee-rules.html / 06-settings.html.
// stroke="currentColor" in the mocks maps to the `color` prop here — state
// (active/inactive) is communicated purely via that color, never fill.

export type TabIconName = 'debits' | 'rules' | 'settings';

const COMMON = {
  viewBox: '0 0 24 24',
  fill: 'none',
  strokeWidth: 1.75,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
};

export default function TabIcon({
  name,
  color,
  size = 22,
}: {
  name: TabIconName;
  color: string;
  size?: number;
}) {
  switch (name) {
    case 'debits':
      return (
        <Svg width={size} height={size} stroke={color} {...COMMON}>
          <Line x1="4" y1="7" x2="20" y2="7" />
          <Line x1="4" y1="12" x2="20" y2="12" />
          <Line x1="4" y1="17" x2="14" y2="17" />
        </Svg>
      );
    case 'rules':
      return (
        <Svg width={size} height={size} stroke={color} {...COMMON}>
          <Path d="M12 3l7 3v6c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6l7-3z" />
        </Svg>
      );
    case 'settings':
      return (
        <Svg width={size} height={size} stroke={color} {...COMMON}>
          <Circle cx="12" cy="12" r="6.5" />
          <Circle cx="12" cy="12" r="2.4" />
          <Path d="M18.6 12h2M16.7 16.7l1.4 1.4M12 18.6v2M7.3 16.7l-1.4 1.4M5.4 12h-2M7.3 7.3L5.9 5.9M12 5.4v-2M16.7 7.3l1.4-1.4" />
        </Svg>
      );
  }
}

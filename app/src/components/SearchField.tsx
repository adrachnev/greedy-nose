import React from 'react';
import { Pressable, StyleSheet, TextInput, useColorScheme, View } from 'react-native';
import Svg, { Line } from 'react-native-svg';
import { dark, light } from '../theme/colors';

/**
 * The pinned search box both list screens use (R23), ported from
 * mocks/style.css's `.search-field`.
 *
 * "Pinned" is the point: it is rendered as a sibling of the list, never inside
 * it, so it cannot scroll away. A filtered list whose query has scrolled out of
 * sight is indistinguishable from an empty one.
 */
export default function SearchField({
  value,
  onChangeText,
  placeholder,
}: {
  value: string;
  onChangeText: (value: string) => void;
  placeholder: string;
}) {
  const theme = useColorScheme() === 'dark' ? dark : light;

  return (
    <View style={[styles.field, { backgroundColor: theme.neutralBg, borderColor: theme.border }]}>
      <TextInput
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={theme.textMuted}
        style={[styles.input, { color: theme.text }]}
        autoCapitalize="none"
        autoCorrect={false}
        returnKeyType="search"
        // Android draws its own underline and inner padding; both fight the
        // box we just drew around the field.
        underlineColorAndroid="transparent"
      />
      {value !== '' && (
        <Pressable
          onPress={() => onChangeText('')}
          style={styles.clear}
          // The glyph is 18px in a 24px box so the field's height never
          // changes; the slop is what makes the *tap* target 44px. A 44px View
          // here would make the whole search field 64px tall.
          hitSlop={10}
          accessibilityRole="button"
          accessibilityLabel="Clear search"
        >
          <ClearIcon color={theme.textMuted} />
        </Pressable>
      )}
    </View>
  );
}

/**
 * Line-style ✕, drawn like every other icon in the app: stroke-only,
 * `currentColor` via the `color` prop, 1.75 weight, round caps. No emoji and
 * no icon library — see the design-language note in CLAUDE.md.
 */
function ClearIcon({ color }: { color: string }) {
  return (
    <Svg
      width={18}
      height={18}
      viewBox="0 0 24 24"
      fill="none"
      stroke={color}
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <Line x1="6" y1="6" x2="18" y2="18" />
      <Line x1="18" y1="6" x2="6" y2="18" />
    </Svg>
  );
}

const styles = StyleSheet.create({
  field: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    marginHorizontal: 16,
    marginBottom: 12,
    paddingHorizontal: 14,
    paddingVertical: 6,
    minHeight: 44,
    borderRadius: 14,
    // The fill alone is nearly the same grey as the screen behind it, and the
    // box has no white cards around it to frame it — so it needs an edge to
    // read as a field you tap.
    borderWidth: 1,
  },
  input: {
    flex: 1,
    minWidth: 0,
    fontSize: 15,
    paddingVertical: 0,
  },
  clear: {
    width: 24,
    height: 24,
    alignItems: 'center',
    justifyContent: 'center',
  },
});

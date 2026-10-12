import React from 'react';
import ReactTestRenderer from 'react-test-renderer';
import { Text } from 'react-native';
import ConnectBankScreen from '../ConnectBankScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));

function renderedText(): string[] {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  ReactTestRenderer.act(() => {
    tree = ReactTestRenderer.create(<ConnectBankScreen />);
  });
  return tree.root
    .findAllByType(Text)
    .map(t => [t.props.children].flat().join(''));
}

test('names the bank neutrally and never by brand', () => {
  const texts = renderedText();
  expect(texts).toContain('Your bank');
  expect(texts).toContain('Continue with your bank');
  expect(texts.join('\n')).not.toMatch(/N26|DKB|ING-DiBa/i);
});

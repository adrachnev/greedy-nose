import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import ReleaseStatusLine from '../ReleaseStatusLine';
import { light } from '../../theme/colors';

const mockUseDataSource = jest.fn();
const mockUseRegistrationState = jest.fn();
jest.mock('../../data/hooks', () => ({
  useDataSource: () => mockUseDataSource(),
  useRegistrationState: () => mockUseRegistrationState(),
}));

const globals = globalThis as unknown as { __DEV__: boolean };
const devBefore = globals.__DEV__;

function render(
  feed: object,
  live = true,
  registration = 'registered',
): ReactTestRenderer.ReactTestRenderer {
  mockUseRegistrationState.mockReturnValue(registration);
  mockUseDataSource.mockReturnValue({ live, feed, refresh: jest.fn() });
  let tree!: ReactTestRenderer.ReactTestRenderer;
  act(() => {
    tree = ReactTestRenderer.create(<ReleaseStatusLine theme={light} />);
  });
  return tree;
}

afterEach(() => {
  globals.__DEV__ = devBefore;
});

describe('ReleaseStatusLine in release', () => {
  beforeEach(() => {
    globals.__DEV__ = false;
  });

  it.each([
    ['unreachable', "Can't reach the server"],
    ['unauthorized', 'Not authorized'],
    ['empty', 'No debits yet'],
    ['unreadable', "Can't read the server's answer"],
    ['notConfigured', 'App not configured'],
  ])('shows the %s line', (problem, text) => {
    const tree = render({ status: 'error', problem, dropped: 0, skipped: 0 });
    expect(JSON.stringify(tree.toJSON())).toContain(text);
  });

  it('renders nothing when healthy with debits', () => {
    expect(render({ status: 'ready', dropped: 0, skipped: 0 }).toJSON()).toBeNull();
  });

  it('shows the alerts line when registration failed and the feed is fine', () => {
    const tree = render({ status: 'ready', dropped: 0, skipped: 0 }, true, 'failed');
    expect(JSON.stringify(tree.toJSON())).toContain('Alerts are not set up on this phone');
  });

  it('does not show the alerts line while registration is pending', () => {
    expect(render({ status: 'ready', dropped: 0, skipped: 0 }, true, 'pending').toJSON()).toBeNull();
  });

  it('renders nothing in fixture mode', () => {
    const feed = { status: 'ready', problem: 'empty', dropped: 0, skipped: 0 };
    expect(render(feed, false).toJSON()).toBeNull();
  });
});

it('renders nothing in dev, where the badge owns the status', () => {
  globals.__DEV__ = true;
  const feed = { status: 'error', problem: 'unreachable', dropped: 0, skipped: 0 };
  expect(render(feed, true, 'failed').toJSON()).toBeNull();
});

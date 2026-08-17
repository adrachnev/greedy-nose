/**
 * The alert limit decides whether the user is alerted at all (R5), so mis-
 * reading the field they typed is a silent failure of the whole product. These
 * pin the two ways `parseFloat` failed at it — locale and prefix parsing — and
 * the blank case, which means "no limit" rather than "invalid" (R4a).
 */

import { parseAmountEUR } from '../parseAmount';

function amountOf(text: string): number | undefined | 'invalid' {
  const parsed = parseAmountEUR(text);
  return parsed.ok ? parsed.amountEUR : 'invalid';
}

describe('parseAmountEUR', () => {
  it('treats a blank field as "no limit", not as an error (R4a)', () => {
    expect(amountOf('')).toBeUndefined();
    expect(amountOf('   ')).toBeUndefined();
  });

  it('reads a plain amount', () => {
    expect(amountOf('30')).toBe(30);
    expect(amountOf('45.50')).toBe(45.5);
    expect(amountOf(' 45.50 ')).toBe(45.5);
  });

  it('reads a comma as the decimal separator — decimal-pad shows one on a German phone (R15)', () => {
    // parseFloat('45,50') is 45: a €45.50 limit silently became €45.
    expect(amountOf('45,50')).toBe(45.5);
    expect(amountOf('0,99')).toBe(0.99);
  });

  it('handles grouping separators in either convention', () => {
    expect(amountOf('1.234,56')).toBe(1234.56);
    expect(amountOf('1,234.56')).toBe(1234.56);
    expect(amountOf('1.234.567')).toBe(1234567);
    expect(amountOf('1 234,56')).toBe(1234.56);
  });

  it('rejects anything that is not fully numeric instead of parsing its prefix', () => {
    // parseFloat('45abc') is 45 — a typo accepted as a limit.
    expect(amountOf('45abc')).toBe('invalid');
    expect(amountOf('€30')).toBe('invalid');
    expect(amountOf('30 EUR')).toBe('invalid');
    expect(amountOf('1e3')).toBe('invalid');
    expect(amountOf('abc')).toBe('invalid');
    expect(amountOf('.')).toBe('invalid');
    expect(amountOf('30.')).toBe('invalid');
  });

  it('rejects an ambiguous single separator with three following digits', () => {
    // "1,234" is 1234 to an English reader and 1.234 to a German one; guessing
    // either way would set a limit off by a factor of a thousand.
    expect(amountOf('1,234')).toBe('invalid');
  });

  it('rejects amounts that are not positive', () => {
    expect(amountOf('0')).toBe('invalid');
    expect(amountOf('0,00')).toBe('invalid');
    expect(amountOf('-5')).toBe('invalid');
  });

  it('rejects more precision than a euro amount has', () => {
    // Otherwise the stored limit and the "€46.00" the app shows disagree.
    expect(amountOf('45.999')).toBe('invalid');
  });
});

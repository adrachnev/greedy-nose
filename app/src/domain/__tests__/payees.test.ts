/**
 * The placeholder payee is R1's last line of defence: it is what stops a
 * charge disappearing when the app has no record of who took the money. Three
 * layers depend on it (the feed, the debit list, the debit detail), so its
 * behaviour is pinned here once rather than three times in three screens.
 */

import { Payee } from '../model';
import { createPayeeLookup, toInitials, unknownPayee, UNKNOWN_PAYEE_NAME } from '../payees';

const NETFLIX: Payee = {
  id: 'payee-netflix',
  name: 'Netflix',
  initials: 'NE',
  iban: 'NL46 INGB 0006 8890 78',
};

describe('toInitials', () => {
  it('takes the first letter of up to two words', () => {
    expect(toInitials('Bäckerei Müller')).toBe('BM');
    expect(toInitials('ScamyLoans GmbH International')).toBe('SG');
  });

  it('uppercases, and lets a digits-only word contribute nothing', () => {
    // Bank names carry branch numbers ("REWE 4711"), and the backend's own
    // ToInitials skips a word with no letter in it rather than substituting
    // something. Same rule here, so the same name gets the same avatar on both
    // sides of the seam.
    expect(toInitials('lidl connect')).toBe('LC');
    expect(toInitials('4711 rewe')).toBe('R');
    expect(toInitials('rewe 4711 markt')).toBe('RM');
  });

  it('handles the accented names a European bank actually sends', () => {
    // The letter test is an explicit Latin range rather than a Unicode
    // property escape, so the alphabets it claims to cover are worth naming:
    // German, French, Polish, Czech — R22 makes any EU bank a valid first
    // connection.
    expect(toInitials('Bäckerei Öztürk')).toBe('BÖ');
    expect(toInitials('Żabka Ćwikła')).toBe('ŻĆ');
    expect(toInitials('Édouard Leclerc')).toBe('ÉL');
  });

  /**
   * The letter range itself, which nothing covered while it was wrong: the
   * first version spanned U+00C0-U+024F flat and therefore counted × and ÷ as
   * letters, where the backend's char.IsLetter does not. Boundaries are pinned
   * on both sides of each hole, since that is the only place an off-by-one in
   * a character range can hide.
   */
  it('does not count the maths symbols that sit inside Latin-1 as letters', () => {
    expect(toInitials('× ÷')).toBe('?');
    expect(toInitials('× Rewe')).toBe('R');
    // The letters immediately either side of both holes still count.
    expect(toInitials('Ö Ø ö ø')).toBe('ÖØ');
  });

  it('covers the Latin range end to end, and stops there', () => {
    // 'Ɏ', not 'ɏ': the range test comes first, the uppercasing after.
    expect(toInitials('À ɏ')).toBe('ÀɎ');
    // Just outside: Greek and Cyrillic need transliteration, not a range —
    // the same line src/utils/search.ts draws.
    expect(toInitials('Δέλτα')).toBe('?');
    expect(toInitials('Сбербанк')).toBe('?');
  });

  it('falls back to ? rather than an empty avatar', () => {
    expect(toInitials('')).toBe('?');
    expect(toInitials('123 456')).toBe('?');
  });
});

describe('unknownPayee', () => {
  it('derives its initials from its name instead of hardcoding them', () => {
    // The backend spells the same placeholder and computes "UP" from it with
    // its own ToInitials. Deriving on both sides is what keeps them equal by
    // construction rather than by coincidence.
    expect(unknownPayee('name:GONE')).toEqual({
      id: 'name:GONE',
      name: UNKNOWN_PAYEE_NAME,
      initials: toInitials(UNKNOWN_PAYEE_NAME),
      iban: '',
    });
    expect(unknownPayee('name:GONE').initials).toBe('UP');
  });

  it('keeps the debit’s own payee id, so two unknown payees never merge (R3b)', () => {
    // Merging would let one inherit the other's rule — including "good", which
    // is the direction R3b calls dangerous.
    expect(unknownPayee('a').id).not.toBe(unknownPayee('b').id);
  });
});

describe('createPayeeLookup', () => {
  it('returns the real payee when there is one', () => {
    const lookup = createPayeeLookup([NETFLIX]);

    expect(lookup('payee-netflix')).toBe(NETFLIX);
  });

  it('never returns undefined, so no caller can render nothing', () => {
    const lookup = createPayeeLookup([]);

    expect(lookup('payee-gone').name).toBe(UNKNOWN_PAYEE_NAME);
  });

  it('hands back the same placeholder object every time', () => {
    // A new object per call would give a memoized list row a new `payee` prop
    // on every keystroke, which is most of what memoizing the row bought.
    const lookup = createPayeeLookup([]);

    expect(lookup('payee-gone')).toBe(lookup('payee-gone'));
  });

  it('reports each invented payee once, not once per debit', () => {
    const onUnknown = jest.fn();
    const lookup = createPayeeLookup([NETFLIX], onUnknown);

    lookup('payee-gone');
    lookup('payee-gone');
    lookup('payee-netflix');
    lookup('payee-other');

    expect(onUnknown.mock.calls).toEqual([['payee-gone'], ['payee-other']]);
  });
});

/**
 * R23/R23a, which is really one question: does the app find what the user
 * meant on a German keyboard? Every case below is a spelling someone will
 * actually type, and a miss looks to them like the charge is not there.
 */

import {
  contractUmlautDigraphs,
  expandUmlauts,
  foldForSearch,
  matchesDebitSearch,
  matchesNameSearch,
  normalizeAmountQuery,
} from '../search';

const BAKERY = 'Bäckerei Müller';
/** The same payee as the SEPA name field actually delivers it. */
const BAKERY_SEPA = 'BAECKEREI MUELLER 4711';

describe('foldForSearch', () => {
  it('lowercases and strips diacritics', () => {
    expect(foldForSearch(BAKERY)).toBe('backerei muller');
    expect(foldForSearch('ÉCOLE Crèche')).toBe('ecole creche');
  });

  it('folds ß to ss', () => {
    expect(foldForSearch('Straßenbahn')).toBe('strassenbahn');
  });

  /**
   * R22 makes the app bank-agnostic, so the fold table is not a German one.
   * Each case below is a letter that exists on a real EU payee name and that
   * nobody types on a German or English keyboard.
   */
  it.each([
    ['Żabka', 'zabka'],
    ['Łukasz Śliwiński', 'lukasz sliwinski'],
    ['Đuro', 'duro'],
    ['Škoda Přerov', 'skoda prerov'],
    ['Ærø Købmand', 'aero kobmand'],
    ['Rīgas Ķīmija', 'rigas kimija'],
    ['București Șos. Țării', 'bucuresti sos. tarii'],
    ['Gyűrű Étterem', 'gyuru etterem'],
  ])('folds %s to %s', (name, expected) => {
    expect(foldForSearch(name)).toBe(expected);
  });
});

describe('contractUmlautDigraphs', () => {
  it('closes the direction expandUmlauts cannot reach', () => {
    expect(contractUmlautDigraphs('baeckerei mueller')).toBe('backerei muller');
  });

  it('leaves a name with nothing to contract alone', () => {
    expect(contractUmlautDigraphs('netflix')).toBe('netflix');
  });
});

describe('expandUmlauts', () => {
  it('spells umlauts out the way banks transliterate them', () => {
    expect(expandUmlauts(BAKERY)).toBe('Baeckerei Mueller');
    expect(expandUmlauts('ÖPNV')).toBe('OePNV');
  });

  it('leaves everything else alone', () => {
    expect(expandUmlauts('Netflix')).toBe('Netflix');
  });
});

/**
 * The whole point of the helper: three spellings the user might type, two
 * spellings the data might carry, and every one of the six pairings has to
 * land. The fixture payee happens to be spelled with the umlaut, so testing
 * only against it would pass while the live bank feed — which is the
 * transliterated side — quietly missed half the queries.
 */
describe('matchesNameSearch — every spelling of Müller, from either side (R23a)', () => {
  const spellings = ['müller', 'mueller', 'muller'];
  const names = [BAKERY, BAKERY_SEPA];

  it.each(spellings.flatMap(query => names.map(name => [query, name] as const)))(
    'finds "%s" in "%s"',
    (query, name) => {
      expect(matchesNameSearch(query, name)).toBe(true);
    },
  );

  it.each(['MÜLLER', 'Bäckerei', 'baeckerei', 'backerei'])(
    'finds "%s" in Bäckerei Müller',
    query => {
      expect(matchesNameSearch(query, BAKERY)).toBe(true);
    },
  );

  it('is a substring match, not a prefix one', () => {
    expect(matchesNameSearch('kere', BAKERY)).toBe(true);
  });

  it('does not match an unrelated query', () => {
    expect(matchesNameSearch('aldi', BAKERY)).toBe(false);
  });

  it('finds a non-German payee typed on an unaccented keyboard (R22)', () => {
    expect(matchesNameSearch('zabka', 'Żabka Polska')).toBe(true);
    expect(matchesNameSearch('duro', 'Đuro d.o.o.')).toBe(true);
    expect(matchesNameSearch('skoda', 'ŠKODA AUTO')).toBe(true);
  });

  it('treats an empty or blank query as "everything matches"', () => {
    expect(matchesNameSearch('', BAKERY)).toBe(true);
    expect(matchesNameSearch('   ', BAKERY)).toBe(true);
  });
});

describe('normalizeAmountQuery', () => {
  it('turns a comma into a decimal point', () => {
    expect(normalizeAmountQuery('12,99')).toBe('12.99');
  });

  it('strips the currency symbol and any kind of space', () => {
    expect(normalizeAmountQuery('€ 12,99')).toBe('12.99');
    // Non-breaking space: what Intl emits, and therefore what a copy-paste
    // from this very app carries.
    expect(normalizeAmountQuery('1 234,56')).toBe('1234.56');
  });

  it('keeps only the last separator, so a pasted grouped amount still works', () => {
    expect(normalizeAmountQuery('1.234,56')).toBe('1234.56');
    expect(normalizeAmountQuery('1,234.56')).toBe('1234.56');
  });

  it('leaves a bare fragment untouched — a query is not a complete amount', () => {
    expect(normalizeAmountQuery('9')).toBe('9');
  });
});

describe('matchesDebitSearch', () => {
  it('matches on the amount as a substring: "9" finds both 9.99 and 19.90', () => {
    expect(matchesDebitSearch('9', 'Netflix', 9.99)).toBe(true);
    expect(matchesDebitSearch('9', 'Netflix', 19.9)).toBe(true);
    expect(matchesDebitSearch('9', 'Netflix', 12.5)).toBe(false);
  });

  it('finds 12.99 when typed with a comma (R23a)', () => {
    expect(matchesDebitSearch('12,99', 'Netflix', 12.99)).toBe(true);
    expect(matchesDebitSearch('12.99', 'Netflix', 12.99)).toBe(true);
  });

  /**
   * The point of matching against the raw value rather than the rendered
   * string: on a German phone this row reads "12,99 €" (R17), and search still
   * has to work.
   */
  it('matches the unformatted amount, so R17 formatting cannot break search', () => {
    expect(matchesDebitSearch('12.99', 'Netflix', 12.99)).toBe(true);
    expect(matchesDebitSearch('13', 'Netflix', 12.99)).toBe(false);
  });

  it('matches whole euros written without decimals', () => {
    expect(matchesDebitSearch('39', 'Vodafone GmbH', 39.99)).toBe(true);
  });

  it('never matches the payment type — "card" would return half the list', () => {
    expect(matchesDebitSearch('card', BAKERY, 6.4)).toBe(false);
    expect(matchesDebitSearch('subscription', 'Netflix', 12.99)).toBe(false);
  });

  it('ignores case', () => {
    expect(matchesDebitSearch('NETFLIX', 'Netflix', 12.99)).toBe(true);
    expect(matchesDebitSearch('netflix', 'NETFLIX', 12.99)).toBe(true);
  });

  it('does not match everything on a query that normalizes to nothing', () => {
    expect(matchesDebitSearch('€', 'Netflix', 12.99)).toBe(false);
  });

  it('shows everything while the query is empty', () => {
    expect(matchesDebitSearch('', 'Netflix', 12.99)).toBe(true);
  });
});

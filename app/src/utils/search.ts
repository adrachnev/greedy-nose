/**
 * Matching for the two list searches (R23/R23a). Pure string work — no React,
 * no I/O — which is deliberate: every rule in here is a guess about how a
 * person types, and guesses are the thing worth pinning with tests.
 *
 * R23a is the whole job: a German keyboard produces a comma and an umlaut, and
 * neither may cost the user a result.
 */

/**
 * Lowercase plus an explicit Latin fold.
 *
 * Deliberately NOT `normalize('NFD')` + a combining-mark strip. That trick
 * leans on the engine's Unicode tables, and Hermes' Unicode surface is a build
 * option that has been trimmed before — a search that silently stops folding
 * on one platform is exactly the kind of bug nobody reports. A table of the
 * characters a European payee name actually contains is predictable
 * everywhere, and it documents which alphabets we claim to handle. Extending
 * it is a one-line change.
 *
 * Scope is the **Latin alphabets of the EU**, because R22 makes the app
 * bank-agnostic: a Polish or Croatian ASPSP is as valid a first connection as
 * a German one, and "żabka" not being findable as "zabka" is the same bug as
 * "müller" not being findable as "muller". Greek and Cyrillic are out — they
 * are not a diacritic problem, they need transliteration, and nobody types a
 * Latin query hoping to hit them.
 *
 * Keys are lowercase only: folding lowercases first, so 'Ä' never reaches the
 * table as 'Ä'.
 */
const FOLD: Record<string, string> = {
  // a — West/Nordic, Baltic (ā), Romanian (ă), Polish (ą)
  à: 'a',
  á: 'a',
  â: 'a',
  ã: 'a',
  ä: 'a',
  å: 'a',
  ā: 'a',
  ă: 'a',
  ą: 'a',
  æ: 'ae',
  // c — Polish (ć), Czech/Croatian (č)
  ç: 'c',
  ć: 'c',
  č: 'c',
  // d — Czech/Slovak (ď), Croatian/Serbian (đ)
  ď: 'd',
  đ: 'd',
  // e — Baltic (ē), Czech (ě), Polish (ę)
  è: 'e',
  é: 'e',
  ê: 'e',
  ë: 'e',
  ē: 'e',
  ě: 'e',
  ę: 'e',
  // g/k/l/n — Latvian cedillas, Slovak ľ, Polish ł
  ģ: 'g',
  ķ: 'k',
  ľ: 'l',
  ļ: 'l',
  ł: 'l',
  ì: 'i',
  í: 'i',
  î: 'i',
  ï: 'i',
  ī: 'i',
  ñ: 'n',
  ń: 'n',
  ň: 'n',
  ņ: 'n',
  // o — Nordic (ø), Baltic (ō), Hungarian (ő)
  ò: 'o',
  ó: 'o',
  ô: 'o',
  õ: 'o',
  ö: 'o',
  ø: 'o',
  ō: 'o',
  ő: 'o',
  œ: 'oe',
  ř: 'r',
  // s — Polish (ś), Czech/Croatian (š), Romanian (ș comma-below, ş cedilla).
  // Both Romanian codepoints are listed on purpose: the comma-below letters
  // are the correct ones, but legacy systems emit the cedilla variants and
  // SEPA name fields are full of legacy systems.
  ś: 's',
  š: 's',
  ș: 's',
  ş: 's',
  ß: 'ss',
  ť: 't',
  ț: 't',
  ţ: 't',
  // u — Baltic (ū), Czech (ů), Hungarian (ű)
  ù: 'u',
  ú: 'u',
  û: 'u',
  ü: 'u',
  ū: 'u',
  ů: 'u',
  ű: 'u',
  ý: 'y',
  ÿ: 'y',
  // z — Polish (ź, ż), Czech/Croatian (ž)
  ź: 'z',
  ż: 'z',
  ž: 'z',
};

const FOLDABLE = new RegExp(`[${Object.keys(FOLD).join('')}]`, 'g');

/** Lowercased and stripped of diacritics, per the table above. */
export function foldForSearch(text: string): string {
  return text.toLowerCase().replace(FOLDABLE, character => FOLD[character]);
}

const UMLAUT_EXPANSIONS: Record<string, string> = {
  ä: 'ae',
  ö: 'oe',
  ü: 'ue',
  ß: 'ss',
  Ä: 'Ae',
  Ö: 'Oe',
  Ü: 'Ue',
};

const EXPANDABLE = new RegExp(`[${Object.keys(UMLAUT_EXPANSIONS).join('')}]`, 'g');

/**
 * The *other* German spelling: 'ä' -> 'ae'. Banks routinely transliterate this
 * way in the SEPA name field ("BAECKEREI MUELLER"), while the user types the
 * umlaut — so both spellings have to be reachable from either side.
 */
export function expandUmlauts(text: string): string {
  return text.replace(EXPANDABLE, character => UMLAUT_EXPANSIONS[character]);
}

const CONTRACTIBLE = /([aou])e/g;

/**
 * The reverse reading, on already-folded text: 'mueller' -> 'muller'.
 *
 * `expandUmlauts` only covers the side that still *has* the umlaut. When the
 * transliteration is in the data — which is the normal case, since that is
 * what SEPA name fields carry — the plain spelling has nothing to expand:
 * "muller" stays "muller" and never reaches "BAECKEREI MUELLER". Contracting
 * closes that direction.
 *
 * `[aou]e -> [aou]` over-fires by design: it also rewrites "bauer" to "baur",
 * which is not a word. That is the safe direction for a search box — a form
 * can only *add* candidate spellings, never remove one, so the worst case is a
 * stray extra row, and both query and data go through the same function so an
 * over-fire on one side is mirrored on the other.
 */
export function contractUmlautDigraphs(foldedText: string): string {
  return foldedText.replace(CONTRACTIBLE, '$1');
}

/**
 * Every spelling of one string, folded.
 *
 * No canonical form satisfies all readings of 'ü': folding it to 'u' loses
 * "mueller", expanding it to 'ue' loses "muller", and a plain 'u' in the data
 * has neither. So we keep up to three forms and match any against any. The
 * cost is at most nine `includes` per row instead of one — nothing next to
 * getting "müller" wrong, and if a real history ever makes it measurable the
 * fix is to fold the data once at ingest, not to drop a form.
 */
function foldedForms(text: string): string[] {
  const folded = foldForSearch(text);
  return [
    ...new Set([
      folded,
      foldForSearch(expandUmlauts(text)),
      contractUmlautDigraphs(folded),
    ]),
  ];
}

/**
 * Substring match on a payee's name, tolerant of case and of any umlaut
 * spelling — on the query *and* on the data, since the umlaut may sit on
 * either side and may already be gone from both: the user types "müller",
 * "mueller" or "muller", the bank sends "Müller" or "MUELLER", and all six
 * pairings have to land.
 */
export function matchesNameSearch(query: string, name: string): boolean {
  const trimmed = query.trim();
  if (trimmed === '') {
    return true;
  }
  const needles = foldedForms(trimmed);
  const haystacks = foldedForms(name);
  return needles.some(needle => haystacks.some(haystack => haystack.includes(needle)));
}

/**
 * The typed query as a plain machine-readable amount: '€ 12,99' -> '12.99'.
 *
 * Not `parseAmountEUR`: that one validates a *complete* amount because it sets
 * a limit the app then alerts on. A search query is a fragment by definition —
 * '9' has to survive as '9' and match both 9.99 and 19.90 — so this only
 * normalizes separators and never judges the result.
 */
export function normalizeAmountQuery(text: string): string {
  // \s covers the non-breaking and narrow spaces some locales group with,
  // which is what a pasted, formatted amount arrives with.
  const compact = text.replace(/[€\s]/g, '').replace(/,/g, '.');
  const lastSeparator = compact.lastIndexOf('.');
  if (lastSeparator === -1) {
    return compact;
  }
  // More than one separator left means the earlier ones grouped thousands:
  // a pasted "1.234,56" is 1234.56, whichever locale wrote it.
  return compact.slice(0, lastSeparator).replace(/\./g, '') + compact.slice(lastSeparator);
}

/**
 * R23: a debit matches on its payee's name or its amount. The payment type is
 * deliberately not matched — "card" would return half the list and teach the
 * user nothing.
 *
 * The amount is matched against the raw value, not the rendered string (R23a),
 * so R17's device-locale formatting cannot break search: on a German phone the
 * row reads "12,99 €" and the query "12,99" still lands, because both sides
 * are reduced to "12.99" first.
 */
export function matchesDebitSearch(
  query: string,
  payeeName: string,
  amountEUR: number,
): boolean {
  const trimmed = query.trim();
  if (trimmed === '') {
    return true;
  }
  if (matchesNameSearch(trimmed, payeeName)) {
    return true;
  }
  const amountQuery = normalizeAmountQuery(trimmed);
  if (amountQuery === '') {
    // A query of just "€" would otherwise match every row: '' is a substring
    // of everything.
    return false;
  }
  // Amounts are stored positive (R2a); Math.abs keeps a stray sign from ever
  // becoming a matchable character.
  return Math.abs(amountEUR).toFixed(2).includes(amountQuery);
}

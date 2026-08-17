/**
 * Parsing the alert-limit field the user types on PayeeEditScreen.
 *
 * `parseFloat` cannot do this job: it is locale-blind and prefix-parsing, so
 * `parseFloat('45,50')` is 45 and `parseFloat('45abc')` is 45 too. Both are
 * silent — the user sets a €45.50 limit and gets a €45 one, or a typo is
 * accepted as a limit they never meant. Either way the limit that decides
 * whether they are alerted (R5) is not the one they entered.
 *
 * Every target bank is in the eurozone (R15) and `keyboardType="decimal-pad"`
 * renders a comma on a German device, so a comma is a decimal separator here,
 * not noise. Grouping separators can still arrive by paste, so they are
 * handled — but the result is validated as a whole against a regex rather than
 * trusted to a lenient parser.
 */

/** Digits, an optional decimal point, at most two decimals: a euro amount. */
const EURO_AMOUNT = /^\d+(\.\d{1,2})?$/;

export type ParsedAmount =
  /** A valid entry. `amountEUR: undefined` is a blank field: no limit (R4a). */
  | { ok: true; amountEUR: number | undefined }
  | { ok: false };

function occurrences(text: string, character: string): number {
  return text.split(character).length - 1;
}

/**
 * Normalizes separators, then accepts only a fully numeric positive amount.
 * Anything else is rejected outright — never partially parsed.
 */
export function parseAmountEUR(text: string): ParsedAmount {
  // \s covers the non-breaking and narrow spaces some locales group with.
  const compact = text.replace(/\s/g, '');
  if (compact === '') {
    return { ok: true, amountEUR: undefined };
  }
  if (!/^[\d.,]+$/.test(compact)) {
    return { ok: false };
  }

  const commas = occurrences(compact, ',');
  const dots = occurrences(compact, '.');
  let normalized: string;
  if (commas > 0 && dots > 0) {
    // Both present: the *last* one is the decimal separator and the other one
    // groups — "1.234,56" (de) and "1,234.56" (en) both mean 1234.56.
    const decimal = compact.lastIndexOf(',') > compact.lastIndexOf('.') ? ',' : '.';
    const grouping = decimal === ',' ? '.' : ',';
    normalized = compact.split(grouping).join('').replace(decimal, '.');
  } else if (commas > 1 || dots > 1) {
    // The same separator repeated can only be grouping: "1.234.567".
    normalized = compact.split(commas > 1 ? ',' : '.').join('');
  } else {
    // One separator: the decimal one. "1,234" is genuinely ambiguous (1234 to
    // an English reader, 1.234 to a German one) and is therefore rejected by
    // the check below rather than guessed at — the user retypes it without the
    // separator, which is cheap; a silently misread limit is not.
    normalized = compact.replace(',', '.');
  }

  if (!EURO_AMOUNT.test(normalized)) {
    return { ok: false };
  }
  const amountEUR = Number(normalized);
  // A limit of 0 would alert on every charge while claiming to be a limit —
  // that is what marking the payee bad is for, so it is rejected as invalid.
  if (amountEUR <= 0) {
    return { ok: false };
  }
  return { ok: true, amountEUR };
}

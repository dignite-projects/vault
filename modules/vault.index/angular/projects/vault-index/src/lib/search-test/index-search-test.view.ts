import type { DocumentFieldFilter } from '../proxy/dignite/vault/extract/documents/models';
import type { IndexSearchTestCandidateDto, IndexSearchTestInput } from '../proxy/search/models';

/** One field-condition row as the form holds it: every box is text, and an empty box means "not set". */
export interface FieldFilterRow {
  name: string;
  value: string;
  min: string;
  max: string;
}

/** The retrieval test form. Empty settings take the host's values. */
export interface SearchTestForm {
  query: string;
  documentTypeCode: string;
  fieldFilters: FieldFilterRow[];
  keywordWeight: string;
  vectorWeight: string;
  candidatesPerLeg: string;
  rerank: boolean;
  rerankCandidateCount: string;
}

export type CandidateOrder = 'final' | 'fused' | 'keyword' | 'vector';

export function emptyFieldFilter(): FieldFilterRow {
  return { name: '', value: '', min: '', max: '' };
}

/**
 * The request for one test run: text trimmed, rows without a field name dropped, empty bounds left out, and a setting
 * sent only when its box holds a number. Check {@link invalidSettings} first: a box that holds something else would
 * otherwise fall back to the host's value without a word.
 */
export function buildTestInput(form: SearchTestForm): IndexSearchTestInput {
  const filters: DocumentFieldFilter[] = form.fieldFilters
    .filter(row => row.name.trim() !== '')
    .map(row => ({
      name: row.name.trim(),
      value: blankToNull(row.value),
      min: blankToNull(row.min),
      max: blankToNull(row.max),
    }));

  return {
    query: form.query.trim(),
    documentTypeCode: blankToNull(form.documentTypeCode),
    fieldFilters: filters.length > 0 ? filters : null,
    keywordWeight: toNumber(form.keywordWeight),
    vectorWeight: toNumber(form.vectorWeight),
    candidatesPerLeg: toNumber(form.candidatesPerLeg),
    rerank: form.rerank,
    rerankCandidateCount: toNumber(form.rerankCandidateCount),
  };
}

/**
 * The candidates in the order the operator picked: as search returns them, as fusion ranked them, or as one leg
 * did. A candidate the chosen leg did not find goes after those it did, in final order.
 */
export function orderCandidates(
  candidates: readonly IndexSearchTestCandidateDto[],
  order: CandidateOrder,
): IndexSearchTestCandidateDto[] {
  const rankOf = (c: IndexSearchTestCandidateDto): number | null | undefined => {
    switch (order) {
      case 'fused':
        return c.fusedRank;
      case 'keyword':
        return c.keywordRank;
      case 'vector':
        return c.vectorRank;
      default:
        return c.rank;
    }
  };

  return [...candidates].sort((a, b) => {
    const ra = rankOf(a) ?? Number.MAX_SAFE_INTEGER;
    const rb = rankOf(b) ?? Number.MAX_SAFE_INTEGER;
    return ra !== rb ? ra - rb : (a.rank ?? 0) - (b.rank ?? 0);
  });
}

function blankToNull(text: string): string | null {
  const trimmed = text.trim();
  return trimmed === '' ? null : trimmed;
}

/** The settings whose box is filled with something that is not a number. */
export function invalidSettings(form: SearchTestForm): (keyof SearchTestForm)[] {
  const settings = ['keywordWeight', 'vectorWeight', 'candidatesPerLeg', 'rerankCandidateCount'] as const;
  return settings.filter(key => form[key].trim() !== '' && !Number.isFinite(Number(form[key].trim())));
}

function toNumber(text: string): number | null {
  const value = Number(text.trim());
  return text.trim() === '' || !Number.isFinite(value) ? null : value;
}

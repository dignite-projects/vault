import type { IndexSearchTestCandidateDto } from '../proxy/search/models';
import { buildTestInput, emptyFieldFilter, invalidSettings, orderCandidates, type SearchTestForm } from './index-search-test.view';

describe('retrieval test view', () => {
  const form: SearchTestForm = {
    query: '  賃料  ',
    documentTypeCode: '',
    fieldFilters: [emptyFieldFilter()],
    keywordWeight: '',
    vectorWeight: '',
    candidatesPerLeg: '',
    rerank: true,
    rerankCandidateCount: '',
  };

  it('sends only what the operator filled in', () => {
    expect(buildTestInput(form)).toEqual({
      query: '賃料',
      documentTypeCode: null,
      fieldFilters: null,
      keywordWeight: null,
      vectorWeight: null,
      candidatesPerLeg: null,
      rerank: true,
      rerankCandidateCount: null,
    });
  });

  it('keeps a filter row with a field name, with only the bounds it has', () => {
    const input = buildTestInput({
      ...form,
      documentTypeCode: ' lease ',
      fieldFilters: [
        { name: ' monthly_rent ', value: '', min: '150000', max: ' ' },
        { name: '', value: 'ignored', min: '', max: '' },
      ],
    });

    expect(input.documentTypeCode).toBe('lease');
    expect(input.fieldFilters).toEqual([{ name: 'monthly_rent', value: null, min: '150000', max: null }]);
  });

  it('sends settings as numbers, 0 included', () => {
    const input = buildTestInput({ ...form, keywordWeight: '0', vectorWeight: '1.5', candidatesPerLeg: '20', rerank: false });

    expect(input.keywordWeight).toBe(0);
    expect(input.vectorWeight).toBe(1.5);
    expect(input.candidatesPerLeg).toBe(20);
    expect(input.rerank).toBe(false);
  });

  it('names each filled setting that is not a number', () => {
    expect(invalidSettings(form)).toEqual([]);
    expect(invalidSettings({ ...form, keywordWeight: 'abc', candidatesPerLeg: '10', rerankCandidateCount: '1e' })).toEqual([
      'keywordWeight',
      'rerankCandidateCount',
    ]);
  });

  it('orders candidates by the leg asked for, those it did not find last', () => {
    const candidates = [
      { rank: 1, fusedRank: 2, keywordRank: null, vectorRank: 1, pointer: 'a' },
      { rank: 2, fusedRank: 1, keywordRank: 1, vectorRank: 3, pointer: 'b' },
      { rank: 3, fusedRank: 3, keywordRank: 2, vectorRank: 2, pointer: 'c' },
    ] as IndexSearchTestCandidateDto[];

    expect(orderCandidates(candidates, 'final').map(c => c.pointer)).toEqual(['a', 'b', 'c']);
    expect(orderCandidates(candidates, 'fused').map(c => c.pointer)).toEqual(['b', 'a', 'c']);
    expect(orderCandidates(candidates, 'keyword').map(c => c.pointer)).toEqual(['b', 'c', 'a']);
    expect(orderCandidates(candidates, 'vector').map(c => c.pointer)).toEqual(['a', 'c', 'b']);
  });
});

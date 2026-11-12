import { IndexedDocumentStatus } from '../proxy/indexing/indexed-document-status.enum';
import { IndexRunStatus } from '../proxy/indexing/index-run-status.enum';
import { documentStatusBadge, runStatusBadge, summaryWarnings } from './index-status.view';

describe('index status view', () => {
  const healthy = {
    configuredModelId: 'BAAI/bge-m3',
    servingModelId: 'BAAI/bge-m3',
    keywordIndexAvailable: true,
    vectorCount: 120,
    exactSearchChunkLimit: 50000,
  };

  it('raises nothing for a healthy index', () => {
    expect(summaryWarnings(healthy)).toEqual([]);
  });

  it('says nothing is indexed without a model, and does not also report a mismatch', () => {
    expect(summaryWarnings({ ...healthy, configuredModelId: null }).map(w => w.key)).toEqual([
      'VaultIndex::IndexStatus:NoModelConfigured',
    ]);
  });

  it('reports a model other than the serving one, but not before anything was indexed', () => {
    expect(summaryWarnings({ ...healthy, configuredModelId: 'text-embedding-3-small' }).map(w => w.key)).toEqual([
      'VaultIndex::IndexStatus:ModelMismatch',
    ]);
    expect(summaryWarnings({ ...healthy, servingModelId: null })).toEqual([]);
  });

  it('reports a missing keyword index and a store past the exact-search size', () => {
    const keys = summaryWarnings({ ...healthy, keywordIndexAvailable: false, vectorCount: 50001 }).map(w => w.key);
    expect(keys).toEqual([
      'VaultIndex::IndexStatus:KeywordIndexUnavailable',
      'VaultIndex::IndexStatus:ExactSearchLimitExceeded',
    ]);
  });

  it('pins the enum values the badges switch on to the server', () => {
    expect(IndexedDocumentStatus.Failed).toBe(3);
    expect(IndexRunStatus.Skipped).toBe(3);
    expect(documentStatusBadge(IndexedDocumentStatus.Failed)).toBe('bg-danger');
    expect(documentStatusBadge(IndexedDocumentStatus.Indexed)).toBe('bg-success');
    expect(runStatusBadge(IndexRunStatus.Skipped)).toBe('bg-secondary');
  });
});

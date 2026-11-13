import { IndexedDocumentStatus } from '../proxy/indexing/indexed-document-status.enum';
import { IndexRunStatus } from '../proxy/indexing/index-run-status.enum';
import { IndexArtifactState } from '../proxy/indexing/index-artifact-state.enum';
import { IndexRunTrigger } from '../proxy/indexing/index-run-trigger.enum';
import { buildingPercent, canRecompile, documentStatusBadge, runStatusBadge, summariesBadge, summaryWarnings } from './index-status.view';

describe('index status view', () => {
  const healthy = {
    configuredModelId: 'BAAI/bge-m3',
    servingModelId: 'BAAI/bge-m3',
    servingModelAvailable: true,
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

  it('reports a model other than the serving one until its rebuild starts, but not before anything was indexed', () => {
    const changed = { ...healthy, configuredModelId: 'Qwen/Qwen3-Embedding-4B' };
    expect(summaryWarnings(changed).map(w => w.key)).toEqual(['VaultIndex::IndexStatus:ModelMismatch']);
    expect(summaryWarnings({ ...changed, buildingModelId: 'Qwen/Qwen3-Embedding-4B' })).toEqual([]);
    expect(summaryWarnings({ ...healthy, servingModelId: null })).toEqual([]);
  });

  it('says search is refused while the host lacks the serving model, and names it', () => {
    const building = { ...healthy, configuredModelId: 'Qwen/Qwen3-Embedding-4B', buildingModelId: 'Qwen/Qwen3-Embedding-4B' };
    expect(summaryWarnings({ ...building, servingModelAvailable: false })).toEqual([
      { key: 'VaultIndex::IndexStatus:ServingModelUnavailable', args: ['BAAI/bge-m3'] },
    ]);
  });

  it('reports documents the rebuild could not embed, and how far it is', () => {
    const building = {
      ...healthy,
      configuredModelId: 'Qwen/Qwen3-Embedding-4B',
      buildingModelId: 'Qwen/Qwen3-Embedding-4B',
      buildingDocumentCount: 2,
      buildingTotalCount: 3,
      buildingFailedCount: 1,
    };
    expect(summaryWarnings(building).map(w => w.key)).toEqual(['VaultIndex::IndexStatus:BuildingFailed']);
    expect(buildingPercent(building)).toBe(66);
    expect(buildingPercent({ ...building, buildingTotalCount: 0 })).toBe(0);
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
    expect(IndexArtifactState.Outdated).toBe(3);
    expect(IndexRunTrigger.ModelChange).toBe(4);
    expect(summariesBadge(IndexArtifactState.Current)).toBe('bg-success');
    expect(summariesBadge(IndexArtifactState.Outdated)).toBe('bg-warning text-dark');
  });

  it('offers a recompile only where summaries exist or are due and a model can compile them', () => {
    expect(canRecompile({ summariesState: IndexArtifactState.Outdated }, true)).toBe(true);
    expect(canRecompile({ summariesState: IndexArtifactState.Pending }, true)).toBe(true);
    expect(canRecompile({ summariesState: IndexArtifactState.None }, true)).toBe(false);
    expect(canRecompile({ summariesState: IndexArtifactState.Current }, false)).toBe(false);
  });
});

import { ChunkingStrategy } from '../proxy/indexing/chunking-strategy.enum';
import { IndexEnrichment } from '../proxy/indexing/index-enrichment.enum';
import { IndexCompilation } from '../proxy/indexing/index-compilation.enum';
import {
  enrichmentCallText,
  enrichmentKeys,
  estimateText,
  formError,
  formFor,
  hasSummaries,
  resetInput,
  summaryText,
  toInput,
} from './index-profiles.view';

describe('index profiles view', () => {
  const layerDefault = {
    documentTypeId: null,
    chunkingStrategy: ChunkingStrategy.Token,
    maxTokensPerChunk: 600,
    overlapTokens: 60,
    enrichment: IndexEnrichment.Questions,
    compilation: IndexCompilation.Summaries,
  };

  it('accepts sizes within the bounds and an overlap smaller than the chunk', () => {
    const form = formFor({ documentTypeId: 't', chunkingStrategy: ChunkingStrategy.Heading, maxTokensPerChunk: 400, overlapTokens: 0 });

    expect(formError(form)).toBeNull();
    expect(formError({ ...form, maxTokensPerChunk: 99 })).toBe('VaultIndex::Profiles:InvalidChunkSize');
    expect(formError({ ...form, maxTokensPerChunk: 4001 })).toBe('VaultIndex::Profiles:InvalidChunkSize');
    expect(formError({ ...form, maxTokensPerChunk: null })).toBe('VaultIndex::Profiles:InvalidChunkSize');
    expect(formError({ ...form, overlapTokens: 400 })).toBe('VaultIndex::Profiles:InvalidOverlap');
    expect(formError({ ...form, overlapTokens: -1 })).toBe('VaultIndex::Profiles:InvalidOverlap');
  });

  it('sends the layer default without a type, and a reset as the default settings for the type', () => {
    expect(toInput(formFor(layerDefault)).documentTypeId).toBeNull();
    expect(resetInput({ documentTypeId: 't' }, layerDefault)).toEqual({
      documentTypeId: 't',
      chunkingStrategy: ChunkingStrategy.Token,
      maxTokensPerChunk: 600,
      overlapTokens: 60,
      enrichment: IndexEnrichment.Questions,
      compilation: IndexCompilation.Summaries,
    });
  });

  it('reads and writes the summaries switch', () => {
    const form = formFor(layerDefault);

    expect(form.summaries).toBe(true);
    expect(toInput(form).compilation).toBe(IndexCompilation.Summaries);
    expect(toInput({ ...form, summaries: false }).compilation).toBe(IndexCompilation.None);
    expect(hasSummaries({ compilation: IndexCompilation.None })).toBe(false);
    expect(hasSummaries({})).toBe(false);
  });

  it('mentions the summaries only when saving compiles some', () => {
    expect(summaryText({ summaryDocumentCount: 0 })).toBeNull();
    expect(summaryText({ summaryDocumentCount: 3, summaryCallCount: 12 })).toEqual({
      key: 'VaultIndex::Profiles:EstimateSummaries',
      args: ['3', '12'],
    });
  });

  it('reads and writes the enrichment switches as flags', () => {
    const form = formFor({ ...layerDefault, enrichment: IndexEnrichment.Keywords | IndexEnrichment.Questions });

    expect([form.keywords, form.questions]).toEqual([true, true]);
    expect(toInput({ ...form, questions: false }).enrichment).toBe(IndexEnrichment.Keywords);
    expect(toInput({ ...form, keywords: false, questions: false }).enrichment).toBe(IndexEnrichment.None);
    expect(enrichmentKeys(IndexEnrichment.Questions)).toEqual(['VaultIndex::Profiles:Questions']);
    expect(enrichmentKeys(undefined)).toEqual([]);
  });

  it('mentions the model calls only when enrichment adds some', () => {
    expect(enrichmentCallText({ enrichmentCallCount: 0 })).toBeNull();
    expect(enrichmentCallText({ enrichmentCallCount: 12 })).toEqual({ key: 'VaultIndex::Profiles:EstimateEnrichment', args: ['12'] });
  });

  it('says when nothing would be rebuilt, and counts documents with no recorded tokens', () => {
    expect(estimateText({ documentCount: 0 }).key).toBe('VaultIndex::Profiles:EstimateNothing');
    expect(estimateText({ documentCount: 2, chunkCount: 6, embeddingTokenCount: 1200, documentsWithoutTokenCount: 0 })).toEqual({
      key: 'VaultIndex::Profiles:Estimate',
      args: ['2', '6', (1200).toLocaleString()],
    });
    expect(estimateText({ documentCount: 3, chunkCount: 6, embeddingTokenCount: 60, documentsWithoutTokenCount: 1 }).key).toBe(
      'VaultIndex::Profiles:EstimateWithUnknown',
    );
  });
});

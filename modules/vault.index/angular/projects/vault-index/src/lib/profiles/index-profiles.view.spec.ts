import { ChunkingStrategy } from '../proxy/indexing/chunking-strategy.enum';
import { estimateText, formError, formFor, resetInput, toInput } from './index-profiles.view';

describe('index profiles view', () => {
  const layerDefault = { documentTypeId: null, chunkingStrategy: ChunkingStrategy.Token, maxTokensPerChunk: 600, overlapTokens: 60 };

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
    });
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

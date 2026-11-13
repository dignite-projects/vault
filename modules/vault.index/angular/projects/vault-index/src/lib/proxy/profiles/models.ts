import type { ChunkingStrategy } from '../indexing/chunking-strategy.enum';
import type { IndexEnrichment } from '../indexing/index-enrichment.enum';
import type { IndexCompilation } from '../indexing/index-compilation.enum';
import type { ListResultDto } from '@abp/ng.core';

export interface IndexProfileDto {
  documentTypeId?: string | null;
  documentTypeCode?: string | null;
  documentTypeName?: string | null;
  hasOwnProfile?: boolean;
  chunkingStrategy?: ChunkingStrategy;
  maxTokensPerChunk?: number;
  overlapTokens?: number;
  enrichment?: IndexEnrichment;
  compilation?: IndexCompilation;
  indexedDocumentCount?: number;
}

export interface IndexProfileInput {
  documentTypeId?: string | null;
  chunkingStrategy?: ChunkingStrategy;
  maxTokensPerChunk?: number;
  overlapTokens?: number;
  enrichment?: IndexEnrichment;
  compilation?: IndexCompilation;
}

export interface IndexProfileListDto extends ListResultDto<IndexProfileDto> {
  chatModelAvailable?: boolean;
}

export interface IndexRebuildEstimateDto {
  documentCount?: number;
  chunkCount?: number;
  embeddingTokenCount?: number;
  documentsWithoutTokenCount?: number;
  enrichmentCallCount?: number;
  summaryDocumentCount?: number;
  summaryCallCount?: number;
}

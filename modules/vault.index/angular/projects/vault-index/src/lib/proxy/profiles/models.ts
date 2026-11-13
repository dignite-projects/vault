import type { ChunkingStrategy } from '../indexing/chunking-strategy.enum';
import type { IndexEnrichment } from '../indexing/index-enrichment.enum';
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
  indexedDocumentCount?: number;
}

export interface IndexProfileInput {
  documentTypeId?: string | null;
  chunkingStrategy?: ChunkingStrategy;
  maxTokensPerChunk?: number;
  overlapTokens?: number;
  enrichment?: IndexEnrichment;
}

export interface IndexProfileListDto extends ListResultDto<IndexProfileDto> {
  enrichmentAvailable?: boolean;
}

export interface IndexRebuildEstimateDto {
  documentCount?: number;
  chunkCount?: number;
  embeddingTokenCount?: number;
  documentsWithoutTokenCount?: number;
  enrichmentCallCount?: number;
}

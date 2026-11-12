import type { ChunkingStrategy } from '../indexing/chunking-strategy.enum';

export interface IndexProfileDto {
  documentTypeId?: string | null;
  documentTypeCode?: string | null;
  documentTypeName?: string | null;
  hasOwnProfile?: boolean;
  chunkingStrategy?: ChunkingStrategy;
  maxTokensPerChunk?: number;
  overlapTokens?: number;
  indexedDocumentCount?: number;
}

export interface IndexProfileInput {
  documentTypeId?: string | null;
  chunkingStrategy?: ChunkingStrategy;
  maxTokensPerChunk?: number;
  overlapTokens?: number;
}

export interface IndexRebuildEstimateDto {
  documentCount?: number;
  chunkCount?: number;
  embeddingTokenCount?: number;
  documentsWithoutTokenCount?: number;
}

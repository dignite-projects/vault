import type { DocumentFieldFilter } from '../dignite/vault/extract/documents/models';

export interface DocumentOutlineDto {
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
  documentSummary?: string | null;
  nodes?: OutlineNodeDto[];
  truncated?: boolean;
  totalNodeCount?: number;
  chunkCount?: number;
}

export interface IndexReadInput {
  pointer: string;
}

export interface IndexReadResultDto {
  pointer?: string;
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
  headingPath?: string | null;
  text?: string;
  textTruncated?: boolean;
  textTotalLength?: number;
  chunkCount?: number;
  previousPointer?: string | null;
  nextPointer?: string | null;
}

export interface IndexSearchHitDto {
  pointer?: string;
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
  headingPath?: string | null;
  text?: string;
  textTruncated?: boolean;
  textTotalLength?: number;
  score?: number;
  rerankScore?: number | null;
  keywordRank?: number | null;
  vectorRank?: number | null;
}

export interface IndexSearchInput {
  query: string;
  documentTypeCode?: string | null;
  maxResultCount?: number | null;
  fieldFilters?: DocumentFieldFilter[] | null;
}

export interface IndexSearchResultDto {
  hits?: IndexSearchHitDto[];
  reranked?: boolean;
}

export interface IndexSearchTestCandidateDto {
  rank?: number;
  fusedRank?: number;
  fusedScore?: number;
  keywordRank?: number | null;
  keywordScore?: number | null;
  vectorRank?: number | null;
  vectorDistance?: number | null;
  rerankScore?: number | null;
  chunkId?: string;
  pointer?: string;
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
  headingPath?: string | null;
  text?: string;
  textTruncated?: boolean;
}

export interface IndexSearchTestInput {
  query: string;
  documentTypeCode?: string | null;
  fieldFilters?: DocumentFieldFilter[] | null;
  keywordWeight?: number | null;
  vectorWeight?: number | null;
  candidatesPerLeg?: number | null;
  rerank?: boolean | null;
  rerankCandidateCount?: number | null;
}

export interface IndexSearchTestResultDto {
  keywordWeight?: number;
  vectorWeight?: number;
  candidatesPerLeg?: number;
  rerankCandidateCount?: number;
  rerankAvailable?: boolean;
  reranked?: boolean;
  rerankModelId?: string | null;
  keywordCandidateCount?: number;
  vectorCandidateCount?: number;
  keywordMilliseconds?: number;
  vectorMilliseconds?: number;
  rerankMilliseconds?: number;
  candidates?: IndexSearchTestCandidateDto[];
}

export interface OutlineNodeDto {
  pointer?: string;
  title?: string;
  summary?: string | null;
  depth?: number;
  level?: number;
  chunkCount?: number;
}


export interface DocumentOutlineDto {
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
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
  keywordRank?: number | null;
  vectorRank?: number | null;
}

export interface IndexSearchInput {
  query: string;
  documentTypeCode?: string | null;
  maxResultCount?: number | null;
}

export interface IndexSearchResultDto {
  hits?: IndexSearchHitDto[];
}

export interface OutlineNodeDto {
  pointer?: string;
  title?: string;
  depth?: number;
  level?: number;
  chunkCount?: number;
}

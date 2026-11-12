import type { PagedResultDto } from '@abp/ng.core';
import type { IndexedDocumentStatus } from '../indexing/indexed-document-status.enum';

export interface GetIndexChunkListInput {
  documentId: string;
  skipCount?: number;
  maxResultCount?: number;
}

export interface IndexChunkDto {
  id?: string;
  pointer?: string;
  ordinal?: number;
  headingPath?: string | null;
  text?: string;
  isDisabled?: boolean;
}

export interface IndexChunkListDto extends PagedResultDto<IndexChunkDto> {
  documentId?: string;
  documentTitle?: string | null;
  documentTypeCode?: string | null;
  status?: IndexedDocumentStatus;
  isHidden?: boolean;
  disabledCount?: number;
}

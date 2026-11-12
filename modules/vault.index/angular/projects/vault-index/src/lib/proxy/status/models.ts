import type { PagedAndSortedResultRequestDto } from '@abp/ng.core';
import type { IndexedDocumentStatus } from '../indexing/indexed-document-status.enum';
import type { IndexRunTrigger } from '../indexing/index-run-trigger.enum';
import type { IndexRunStatus } from '../indexing/index-run-status.enum';

export interface GetIndexedDocumentListInput extends PagedAndSortedResultRequestDto {
  status?: IndexedDocumentStatus | null;
  filter?: string | null;
}

export interface IndexRunDto {
  id?: string;
  trigger?: IndexRunTrigger;
  status?: IndexRunStatus;
  attemptNumber?: number;
  startedTime?: string;
  finishedTime?: string | null;
  errorMessage?: string | null;
  chunkCount?: number | null;
  embeddingTokenCount?: number | null;
}

export interface IndexStatusSummaryDto {
  pendingCount?: number;
  indexingCount?: number;
  indexedCount?: number;
  failedCount?: number;
  archivedCount?: number;
  withheldCount?: number;
  chunkCount?: number;
  vectorCount?: number;
  exactSearchChunkLimit?: number;
  servingModelId?: string | null;
  servingDimensions?: number | null;
  servingSince?: string | null;
  configuredModelId?: string | null;
  keywordIndexAvailable?: boolean;
  keywordIndexPendingChanges?: number | null;
  keywordIndexPopulating?: boolean | null;
}

export interface IndexedDocumentStatusDto {
  documentId?: string;
  title?: string | null;
  documentTypeId?: string;
  documentTypeCode?: string | null;
  status?: IndexedDocumentStatus;
  isArchived?: boolean;
  isWithheld?: boolean;
  chunkCount?: number;
  failedAttempts?: number;
  lastIndexedTime?: string | null;
  lastModificationTime?: string | null;
  lastError?: string | null;
}

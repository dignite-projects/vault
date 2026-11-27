import type { SubmissionFileKind } from './submission-file-kind.enum';
import type { EntityDto, PagedAndSortedResultRequestDto } from '@abp/ng.core';
import type { DocumentLifecycleStatus } from './document-lifecycle-status.enum';
import type { DocumentReviewDisposition } from './document-review-disposition.enum';
import type { DocumentReviewReasons } from './document-review-reasons.enum';
import type { DocumentTextOrigin } from './document-text-origin.enum';
import type { IRemoteStreamContent } from '../volo/abp/content/models';

export interface ConfirmClassificationInput {
  documentTypeId: string;
}

export interface CreateSubmissionUploadInput {
  kind?: SubmissionFileKind;
}

export interface DocumentDto extends EntityDto<string> {
  tenantId?: string | null;
  fileOrigin?: FileOriginDto | null;
  cabinetId?: string | null;
  originDocumentId?: string | null;
  isContainer?: boolean;
  documentTypeCode?: string | null;
  lifecycleStatus?: DocumentLifecycleStatus;
  reviewDisposition?: DocumentReviewDisposition;
  reviewReasons?: DocumentReviewReasons;
  requiresReview?: boolean;
  reviewReasonDetails?: ReviewReasonDetailDto[] | null;
  rejectionReason?: string | null;
  classificationConfidence?: number;
  title?: string | null;
  markdown?: string | null;
  language?: string | null;
  extractionIsComplete?: boolean;
  extractionIncompleteReason?: string | null;
  textOrigin?: DocumentTextOrigin;
  extractedFields?: Record<string, any> | null;
  fieldSources?: Record<string, DocumentTextSpanDto[]> | null;
  creationTime?: string;
  rights?: DocumentRightsDto;
}

export interface DocumentFieldFilter {
  name: string | null;
  value?: string | null;
  min?: string | null;
  max?: string | null;
}

export interface DocumentListItemDto extends EntityDto<string> {
  tenantId?: string | null;
  fileOrigin?: FileOriginDto | null;
  cabinetId?: string | null;
  originDocumentId?: string | null;
  isContainer?: boolean;
  textOrigin?: DocumentTextOrigin;
  documentTypeCode?: string | null;
  lifecycleStatus?: DocumentLifecycleStatus;
  reviewDisposition?: DocumentReviewDisposition;
  reviewReasons?: DocumentReviewReasons;
  requiresReview?: boolean;
  classificationConfidence?: number;
  title?: string | null;
  creationTime?: string;
  deletionTime?: string | null;
  extractedFields?: Record<string, any> | null;
  rights?: DocumentRightsDto;
}

export interface DocumentRightsDto {
  canRead?: boolean;
  canEdit?: boolean;
  canReview?: boolean;
  canDelete?: boolean;
  canRestore?: boolean;
  canRetry?: boolean;
}

export interface DocumentStatisticsDto {
  totalCount?: number;
  uploadedCount?: number;
  processingCount?: number;
  pendingReviewCount?: number;
  readyCount?: number;
  failedCount?: number;
  needsReviewCount?: number;
  totalStorageBytes?: number;
}

export interface DocumentTextSpanDto {
  offset?: number;
  length?: number;
}

export interface DuplicateCandidateDto {
  id?: string;
  title?: string | null;
  fileName?: string | null;
  creationTime?: string;
}

export interface FieldValidationWarningDto {
  fieldDefinitionId?: string;
  fieldName?: string | null;
  fieldDisplayName?: string | null;
  message?: string;
}

export interface FileOriginDto {
  uploadedByUserName?: string;
  originalFileName?: string | null;
  contentType?: string;
  fileSize?: number;
}

export interface GetDocumentListInput extends PagedAndSortedResultRequestDto {
  lifecycleStatus?: DocumentLifecycleStatus | null;
  documentTypeCode?: string | null;
  reviewDisposition?: DocumentReviewDisposition | null;
  hasReviewReasons?: boolean | null;
  isDeleted?: boolean | null;
  cabinetId?: string | null;
  originDocumentId?: string | null;
  fieldFilters?: DocumentFieldFilter[] | null;
}

export interface ReclassifyDocumentInput {
  documentTypeId: string;
}

export interface RejectReviewInput {
  reason: string;
}

export interface ResolveFieldValidationWarningsInput {
  fieldDefinitionIds: string[];
}

export interface RetryPipelineInput {
  pipelineCode: string;
}

export interface ReviewReasonDetailDto {
  reason?: DocumentReviewReasons;
  isBlocking?: boolean;
  missingFieldNames?: string[] | null;
  duplicateCandidates?: DuplicateCandidateDto[] | null;
  hiddenDuplicateCandidateCount?: number | null;
  fieldValidationWarnings?: FieldValidationWarningDto[] | null;
}

export interface SubmissionUploadDto {
  fileId?: string;
  uploadUrl?: string;
  expirationTime?: string;
  maxSize?: number;
}

export interface SubmitDocumentInput {
  markdown?: string | null;
  markdownFileId?: string | null;
  originalFileId?: string | null;
  title?: string | null;
  language?: string | null;
  documentTypeCode?: string | null;
  fields?: Record<string, any> | null;
  fieldSources?: Record<string, any> | null;
  cabinetId?: string | null;
}

export interface UpdateDocumentCabinetInput {
  cabinetId?: string | null;
}

export interface UpdateExtractedFieldsInput {
  fields?: Record<string, any>;
}

export interface UpdateMarkdownInput {
  markdown: string;
  reprocess?: boolean;
}

export interface UploadDocumentInput {
  file: IRemoteStreamContent;
  cabinetId?: string | null;
  documentTypeId?: string | null;
}

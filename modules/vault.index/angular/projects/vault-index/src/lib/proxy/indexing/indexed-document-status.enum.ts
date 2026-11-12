import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexedDocumentStatus {
  Pending = 0,
  Indexing = 1,
  Indexed = 2,
  Failed = 3,
}

export const indexedDocumentStatusOptions = mapEnumToOptions(IndexedDocumentStatus);

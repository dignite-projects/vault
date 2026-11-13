import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexEnrichment {
  None = 0,
  Keywords = 1,
  Questions = 2,
}

export const indexEnrichmentOptions = mapEnumToOptions(IndexEnrichment);

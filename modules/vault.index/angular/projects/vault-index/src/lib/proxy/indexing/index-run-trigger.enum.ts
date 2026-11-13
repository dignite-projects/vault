import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexRunTrigger {
  DocumentReady = 0,
  Backfill = 1,
  Rebuild = 2,
  Manual = 3,
  ModelChange = 4,
}

export const indexRunTriggerOptions = mapEnumToOptions(IndexRunTrigger);

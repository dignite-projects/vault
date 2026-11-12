import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexRunStatus {
  Running = 0,
  Succeeded = 1,
  Failed = 2,
  Skipped = 3,
}

export const indexRunStatusOptions = mapEnumToOptions(IndexRunStatus);

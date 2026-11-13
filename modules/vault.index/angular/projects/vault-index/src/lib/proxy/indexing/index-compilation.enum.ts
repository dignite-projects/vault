import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexCompilation {
  None = 0,
  Summaries = 1,
}

export const indexCompilationOptions = mapEnumToOptions(IndexCompilation);

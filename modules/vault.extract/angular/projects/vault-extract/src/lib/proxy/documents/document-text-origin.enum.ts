import { mapEnumToOptions } from '@abp/ng.core';

export enum DocumentTextOrigin {
  Parsed = 0,
  Submitted = 1,
}

export const documentTextOriginOptions = mapEnumToOptions(DocumentTextOrigin);

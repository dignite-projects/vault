import { mapEnumToOptions } from '@abp/ng.core';

export enum IndexArtifactState {
  None = 0,
  Pending = 1,
  Current = 2,
  Outdated = 3,
}

export const indexArtifactStateOptions = mapEnumToOptions(IndexArtifactState);

import { mapEnumToOptions } from '@abp/ng.core';

export enum ChunkContent {
  Text = 0,
  Table = 1,
  Figure = 2,
}

export const chunkContentOptions = mapEnumToOptions(ChunkContent);

import { mapEnumToOptions } from '@abp/ng.core';

export enum ChunkingStrategy {
  Heading = 0,
  Token = 1,
  Whole = 2,
  TableRow = 3,
  Page = 4,
}

export const chunkingStrategyOptions = mapEnumToOptions(ChunkingStrategy);

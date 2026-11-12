import { mapEnumToOptions } from '@abp/ng.core';

export enum ChunkingStrategy {
  Heading = 0,
  Token = 1,
}

export const chunkingStrategyOptions = mapEnumToOptions(ChunkingStrategy);

import type { IndexProfileDto, IndexProfileInput, IndexRebuildEstimateDto } from '../proxy/profiles/models';
import { ChunkingStrategy } from '../proxy/indexing/chunking-strategy.enum';

/** The bounds the server enforces (IndexProfileConsts); the form checks them first so the operator sees why. */
export const MIN_CHUNK_TOKENS = 100;
export const MAX_CHUNK_TOKENS = 4000;

/** The edit form: the row being edited and the settings as typed. */
export interface ProfileForm {
  documentTypeId: string | null;
  chunkingStrategy: ChunkingStrategy;
  maxTokensPerChunk: number | null;
  overlapTokens: number | null;
}

export function formFor(row: IndexProfileDto): ProfileForm {
  return {
    documentTypeId: row.documentTypeId ?? null,
    chunkingStrategy: row.chunkingStrategy ?? ChunkingStrategy.Heading,
    maxTokensPerChunk: row.maxTokensPerChunk ?? null,
    overlapTokens: row.overlapTokens ?? 0,
  };
}

/** Why the settings cannot be saved, as a localization key; <c>null</c> when they can. */
export function formError(form: ProfileForm): string | null {
  const max = form.maxTokensPerChunk;
  const overlap = form.overlapTokens ?? 0;
  if (max === null || !Number.isInteger(max) || max < MIN_CHUNK_TOKENS || max > MAX_CHUNK_TOKENS) {
    return 'VaultIndex::Profiles:InvalidChunkSize';
  }

  if (!Number.isInteger(overlap) || overlap < 0 || overlap >= max) {
    return 'VaultIndex::Profiles:InvalidOverlap';
  }

  return null;
}

export function toInput(form: ProfileForm): IndexProfileInput {
  return {
    documentTypeId: form.documentTypeId,
    chunkingStrategy: form.chunkingStrategy,
    maxTokensPerChunk: form.maxTokensPerChunk ?? 0,
    overlapTokens: form.overlapTokens ?? 0,
  };
}

/** The settings a type falls back to when its own profile is removed: the layer default row's. */
export function resetInput(row: IndexProfileDto, layerDefault: IndexProfileDto): IndexProfileInput {
  return {
    documentTypeId: row.documentTypeId ?? null,
    chunkingStrategy: layerDefault.chunkingStrategy ?? ChunkingStrategy.Heading,
    maxTokensPerChunk: layerDefault.maxTokensPerChunk ?? 0,
    overlapTokens: layerDefault.overlapTokens ?? 0,
  };
}

/** The estimate as a localization key and its arguments. */
export function estimateText(estimate: IndexRebuildEstimateDto): { key: string; args: string[] } {
  const documents = estimate.documentCount ?? 0;
  if (documents === 0) {
    return { key: 'VaultIndex::Profiles:EstimateNothing', args: [] };
  }

  const args = [
    documents.toLocaleString(),
    (estimate.chunkCount ?? 0).toLocaleString(),
    (estimate.embeddingTokenCount ?? 0).toLocaleString(),
  ];
  const unknown = estimate.documentsWithoutTokenCount ?? 0;
  return unknown > 0
    ? { key: 'VaultIndex::Profiles:EstimateWithUnknown', args: [...args, unknown.toLocaleString()] }
    : { key: 'VaultIndex::Profiles:Estimate', args };
}

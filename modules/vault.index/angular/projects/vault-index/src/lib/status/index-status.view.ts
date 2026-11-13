import { IndexedDocumentStatus } from '../proxy/indexing/indexed-document-status.enum';
import { IndexRunStatus } from '../proxy/indexing/index-run-status.enum';
import { IndexArtifactState } from '../proxy/indexing/index-artifact-state.enum';
import type { IndexStatusSummaryDto, IndexedDocumentStatusDto } from '../proxy/status/models';

/** A warning the summary raises, as a localization key and its arguments. */
export interface SummaryWarning {
  key: string;
  args: string[];
}

/**
 * What an operator must act on, from the summary alone: no model (nothing is indexed); a new model whose rebuild has
 * not started; a serving index whose model the host no longer has (search is refused until the rebuilt one takes
 * over); documents the rebuild could not embed (it cannot finish); no keyword index (search runs on vectors only); and
 * more chunks than exact vector search was sized for.
 */
export function summaryWarnings(summary: IndexStatusSummaryDto): SummaryWarning[] {
  const warnings: SummaryWarning[] = [];
  if (!summary.configuredModelId) {
    warnings.push({ key: 'VaultIndex::IndexStatus:NoModelConfigured', args: [] });
  } else if (summary.servingModelId) {
    if (summary.servingModelId !== summary.configuredModelId && summary.buildingModelId !== summary.configuredModelId) {
      warnings.push({ key: 'VaultIndex::IndexStatus:ModelMismatch', args: [] });
    }

    if (summary.servingModelAvailable === false) {
      warnings.push({ key: 'VaultIndex::IndexStatus:ServingModelUnavailable', args: [summary.servingModelId] });
    }
  }

  const failed = summary.buildingFailedCount ?? 0;
  if (failed > 0) {
    warnings.push({ key: 'VaultIndex::IndexStatus:BuildingFailed', args: [failed.toLocaleString()] });
  }

  if (!summary.keywordIndexAvailable) {
    warnings.push({ key: 'VaultIndex::IndexStatus:KeywordIndexUnavailable', args: [] });
  }

  const limit = summary.exactSearchChunkLimit ?? 0;
  if (limit > 0 && (summary.vectorCount ?? 0) > limit) {
    warnings.push({ key: 'VaultIndex::IndexStatus:ExactSearchLimitExceeded', args: [limit.toLocaleString()] });
  }

  return warnings;
}

/** The share of documents with chunks already in the generation being built, as a whole percent. */
export function buildingPercent(summary: IndexStatusSummaryDto): number {
  const total = summary.buildingTotalCount ?? 0;
  return total > 0 ? Math.floor(((summary.buildingDocumentCount ?? 0) * 100) / total) : 0;
}

export function documentStatusBadge(status: IndexedDocumentStatus | undefined): string {
  switch (status) {
    case IndexedDocumentStatus.Indexed:
      return 'bg-success';
    case IndexedDocumentStatus.Failed:
      return 'bg-danger';
    case IndexedDocumentStatus.Indexing:
      return 'bg-info';
    default:
      return 'bg-secondary';
  }
}

export function summariesBadge(state: IndexArtifactState | undefined): string {
  switch (state) {
    case IndexArtifactState.Current:
      return 'bg-success';
    case IndexArtifactState.Outdated:
      return 'bg-warning text-dark';
    default:
      return 'bg-secondary';
  }
}

/** A recompile is offered where summaries exist or are due, and a model can compile them. */
export function canRecompile(document: IndexedDocumentStatusDto, chatModelAvailable: boolean): boolean {
  return chatModelAvailable && (document.summariesState ?? IndexArtifactState.None) !== IndexArtifactState.None;
}

export function runStatusBadge(status: IndexRunStatus | undefined): string {
  switch (status) {
    case IndexRunStatus.Succeeded:
      return 'bg-success';
    case IndexRunStatus.Failed:
      return 'bg-danger';
    case IndexRunStatus.Running:
      return 'bg-info';
    default:
      return 'bg-secondary';
  }
}

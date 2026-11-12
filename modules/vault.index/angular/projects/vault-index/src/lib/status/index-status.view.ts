import { IndexedDocumentStatus } from '../proxy/indexing/indexed-document-status.enum';
import { IndexRunStatus } from '../proxy/indexing/index-run-status.enum';
import type { IndexStatusSummaryDto } from '../proxy/status/models';

/** A warning the summary raises, as a localization key and its arguments. */
export interface SummaryWarning {
  key: string;
  args: string[];
}

/**
 * What an operator must act on, from the summary alone: no model (nothing is indexed), a model that is not the one
 * the index was built with (search is refused), no keyword index (search runs on vectors only), and more chunks
 * than exact vector search was sized for.
 */
export function summaryWarnings(summary: IndexStatusSummaryDto): SummaryWarning[] {
  const warnings: SummaryWarning[] = [];
  if (!summary.configuredModelId) {
    warnings.push({ key: 'VaultIndex::IndexStatus:NoModelConfigured', args: [] });
  } else if (summary.servingModelId && summary.servingModelId !== summary.configuredModelId) {
    warnings.push({ key: 'VaultIndex::IndexStatus:ModelMismatch', args: [] });
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

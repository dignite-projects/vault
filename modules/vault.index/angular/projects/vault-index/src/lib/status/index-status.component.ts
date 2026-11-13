import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { filter, switchMap } from 'rxjs';
import { IndexStatusService } from '../proxy/http-api/status/index-status.service';
import { IndexedDocumentStatus, indexedDocumentStatusOptions } from '../proxy/indexing/indexed-document-status.enum';
import type { IndexRunDto, IndexStatusSummaryDto, IndexedDocumentStatusDto } from '../proxy/status/models';
import { INDEX_PERMISSIONS } from '../tokens';
import { IndexArtifactState } from '../proxy/indexing/index-artifact-state.enum';
import {
  buildingPercent,
  canRecompile,
  documentStatusBadge,
  runStatusBadge,
  summariesBadge,
  summaryWarnings,
} from './index-status.view';

const PAGE_SIZE = 20;

/**
 * The index status page: what is in the index and why something is not, where each document's summaries stand, and a
 * rebuild or a recompile for one document. The server limits the document list to the caller's read scope and decides
 * every permission; the page only hides the buttons from a caller without it.
 */
@Component({
  selector: 'lib-index-status',
  templateUrl: './index-status.component.html',
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IndexStatusComponent implements OnInit {
  private readonly service = inject(IndexStatusService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);

  readonly canRebuild = inject(PermissionService).getGrantedPolicy(INDEX_PERMISSIONS.Status.Rebuild);
  readonly statusOptions = indexedDocumentStatusOptions;
  readonly documentStatusBadge = documentStatusBadge;
  readonly runStatusBadge = runStatusBadge;
  readonly summariesBadge = summariesBadge;
  readonly noSummaries = IndexArtifactState.None;

  summary = signal<IndexStatusSummaryDto | null>(null);
  documents = signal<IndexedDocumentStatusDto[]>([]);
  totalCount = signal(0);
  skip = signal(0);
  loading = signal(true);
  statusFilter = signal<IndexedDocumentStatus | null>(null);
  titleFilter = signal('');
  expandedId = signal<string | null>(null);
  runs = signal<IndexRunDto[]>([]);
  runsLoading = signal(false);

  readonly warnings = computed(() => {
    const summary = this.summary();
    return summary ? summaryWarnings(summary) : [];
  });
  readonly chatModelAvailable = computed(() => this.summary()?.chatModelAvailable ?? false);
  readonly buildingPercent = computed(() => {
    const summary = this.summary();
    return summary ? buildingPercent(summary) : 0;
  });
  readonly hasPrevious = computed(() => this.skip() > 0);
  readonly hasNext = computed(() => this.skip() + PAGE_SIZE < this.totalCount());

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.service
      .getSummary()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(summary => this.summary.set(summary));
    this.loadDocuments();
  }

  applyFilter(): void {
    this.skip.set(0);
    this.loadDocuments();
  }

  page(direction: 1 | -1): void {
    this.skip.update(skip => Math.max(0, skip + direction * PAGE_SIZE));
    this.loadDocuments();
  }

  toggleRuns(document: IndexedDocumentStatusDto): void {
    if (this.expandedId() === document.documentId) {
      this.expandedId.set(null);
      return;
    }

    this.expandedId.set(document.documentId ?? null);
    this.runs.set([]);
    this.runsLoading.set(true);
    this.service
      .getRuns(document.documentId!)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.runs.set(result.items ?? []);
          this.runsLoading.set(false);
        },
        error: () => this.runsLoading.set(false),
      });
  }

  rebuild(document: IndexedDocumentStatusDto): void {
    this.confirmation
      .warn('VaultIndex::IndexStatus:RebuildConfirm', 'VaultIndex::IndexStatus:Rebuild')
      .pipe(
        filter(status => status === Confirmation.Status.confirm),
        switchMap(() => this.service.rebuild(document.documentId!)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.toaster.success('VaultIndex::IndexStatus:RebuildQueued');
        this.refresh();
      });
  }

  canRecompile(document: IndexedDocumentStatusDto): boolean {
    return this.canRebuild && canRecompile(document, this.chatModelAvailable());
  }

  recompile(document: IndexedDocumentStatusDto): void {
    this.confirmation
      .warn('VaultIndex::IndexStatus:RecompileConfirm', 'VaultIndex::IndexStatus:Recompile')
      .pipe(
        filter(status => status === Confirmation.Status.confirm),
        switchMap(() => this.service.recompile(document.documentId!)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.toaster.success('VaultIndex::IndexStatus:RecompileQueued');
        this.refresh();
      });
  }

  private loadDocuments(): void {
    this.loading.set(true);
    this.service
      .getDocumentList({
        status: this.statusFilter(),
        filter: this.titleFilter().trim() || null,
        skipCount: this.skip(),
        maxResultCount: PAGE_SIZE,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.documents.set(result.items ?? []);
          this.totalCount.set(result.totalCount ?? 0);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
  }
}

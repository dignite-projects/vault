import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DecimalPipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { LocalizationPipe, PermissionService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { IndexChunkService } from '../proxy/http-api/chunks/index-chunk.service';
import type { IndexChunkDto, IndexChunkListDto } from '../proxy/chunks/models';
import { INDEX_PERMISSIONS } from '../tokens';
import { documentStatusBadge } from '../status/index-status.view';

const PAGE_SIZE = 50;

/**
 * The chunk view: what one document was cut into, in order, with the chunks an operator disabled. The text is not
 * editable here; a wrong text is corrected in Extract. The server limits the view to the caller's read scope and
 * decides every permission; the page only hides the disable button from a caller without it.
 */
@Component({
  selector: 'lib-index-chunks',
  templateUrl: './index-chunks.component.html',
  imports: [DecimalPipe, RouterLink, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IndexChunksComponent implements OnInit {
  private readonly service = inject(IndexChunkService);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly documentId = inject(ActivatedRoute).snapshot.paramMap.get('documentId')!;

  readonly canManage = inject(PermissionService).getGrantedPolicy(INDEX_PERMISSIONS.Status.ManageChunks);
  readonly documentStatusBadge = documentStatusBadge;

  list = signal<IndexChunkListDto | null>(null);
  skip = signal(0);
  loading = signal(true);
  busyId = signal<string | null>(null);
  expanded = signal<ReadonlySet<string>>(new Set());

  readonly hasPrevious = computed(() => this.skip() > 0);
  readonly hasNext = computed(() => this.skip() + PAGE_SIZE < (this.list()?.totalCount ?? 0));

  ngOnInit(): void {
    this.load();
  }

  page(direction: 1 | -1): void {
    this.skip.update(skip => Math.max(0, skip + direction * PAGE_SIZE));
    this.load();
  }

  toggleText(chunk: IndexChunkDto): void {
    this.expanded.update(set => {
      const next = new Set(set);
      if (!next.delete(chunk.id!)) {
        next.add(chunk.id!);
      }
      return next;
    });
  }

  setDisabled(chunk: IndexChunkDto, disabled: boolean): void {
    this.busyId.set(chunk.id ?? null);
    const call = disabled ? this.service.disable(chunk.id!) : this.service.enable(chunk.id!);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busyId.set(null);
        this.toaster.success(disabled ? 'VaultIndex::Chunks:Disabled' : 'VaultIndex::Chunks:Enabled');
        this.load();
      },
      error: () => this.busyId.set(null),
    });
  }

  private load(): void {
    this.loading.set(true);
    this.service
      .getList({ documentId: this.documentId, skipCount: this.skip(), maxResultCount: PAGE_SIZE })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => {
          this.list.set(list);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
  }
}

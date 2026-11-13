import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LocalizationPipe } from '@abp/ng.core';
import { Confirmation, ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { debounceTime, filter, of, switchMap } from 'rxjs';
import { IndexProfileService } from '../proxy/http-api/profiles/index-profile.service';
import { ChunkingStrategy, chunkingStrategyOptions } from '../proxy/indexing/chunking-strategy.enum';
import type { IndexProfileDto, IndexRebuildEstimateDto } from '../proxy/profiles/models';
import {
  type ProfileForm,
  MAX_CHUNK_TOKENS,
  MIN_CHUNK_TOKENS,
  enrichmentCallText,
  enrichmentKeys,
  estimateText,
  formError,
  formFor,
  resetInput,
  toInput,
} from './index-profiles.view';

/**
 * The index profile page: how each document type is chunked, with the rebuild a change would cause shown before it
 * is saved. Saving or resetting only queues work; the affected documents are re-chunked and re-embedded in the
 * background at low priority. The route requires the profiles permission; the server checks it on every call.
 */
@Component({
  selector: 'lib-index-profiles',
  templateUrl: './index-profiles.component.html',
  imports: [DecimalPipe, FormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IndexProfilesComponent implements OnInit {
  private readonly service = inject(IndexProfileService);
  private readonly confirmation = inject(ConfirmationService);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);

  readonly strategyOptions = chunkingStrategyOptions;
  readonly minTokens = MIN_CHUNK_TOKENS;
  readonly maxTokens = MAX_CHUNK_TOKENS;
  readonly enrichmentKeys = enrichmentKeys;

  rows = signal<IndexProfileDto[]>([]);
  enrichmentAvailable = signal(false);
  loading = signal(true);
  editing = signal<IndexProfileDto | null>(null);
  form = signal<ProfileForm | null>(null);
  estimate = signal<IndexRebuildEstimateDto | null>(null);
  estimating = signal(false);
  saving = signal(false);

  readonly layerDefault = computed(() => this.rows().find(r => !r.documentTypeId) ?? null);
  readonly error = computed(() => {
    const form = this.form();
    return form ? formError(form) : null;
  });
  readonly estimateMessage = computed(() => {
    const estimate = this.estimate();
    return estimate ? estimateText(estimate) : null;
  });
  readonly enrichmentMessage = computed(() => {
    const estimate = this.estimate();
    return estimate ? enrichmentCallText(estimate) : null;
  });

  private backdropPressed = false;

  constructor() {
    // The estimate follows the form, a moment after the operator stops typing.
    toObservable(this.form)
      .pipe(
        debounceTime(300),
        switchMap(form => {
          if (!form || formError(form)) {
            this.estimating.set(false);
            return of(null);
          }

          this.estimating.set(true);
          return this.service.getRebuildEstimate(toInput(form));
        }),
        takeUntilDestroyed(),
      )
      .subscribe({
        next: estimate => {
          this.estimate.set(estimate);
          this.estimating.set(false);
        },
        error: () => this.estimating.set(false),
      });
  }

  ngOnInit(): void {
    this.load();
  }

  strategyKey(strategy: ChunkingStrategy | undefined): string {
    return 'VaultIndex::Enum:ChunkingStrategy.' + (strategy ?? ChunkingStrategy.Heading);
  }

  edit(row: IndexProfileDto): void {
    this.estimate.set(null);
    this.editing.set(row);
    this.form.set(formFor(row));
  }

  update<K extends keyof ProfileForm>(key: K, value: ProfileForm[K]): void {
    this.form.update(form => (form ? { ...form, [key]: value } : form));
  }

  close(): void {
    this.editing.set(null);
    this.form.set(null);
    this.estimate.set(null);
  }

  backdropDown(event: MouseEvent): void {
    this.backdropPressed = event.target === event.currentTarget;
  }

  backdropClick(event: MouseEvent): void {
    // Only a press and release both on the backdrop closes: dragging a selection out of an input must not.
    if (this.backdropPressed && event.target === event.currentTarget) {
      this.close();
    }

    this.backdropPressed = false;
  }

  save(): void {
    const form = this.form();
    if (!form || this.error() || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.service
      .set(toInput(form))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.toaster.success('VaultIndex::Profiles:Saved');
          this.close();
          this.load();
        },
        error: () => this.saving.set(false),
      });
  }

  reset(row: IndexProfileDto): void {
    const layerDefault = this.layerDefault();
    if (!row.documentTypeId || !layerDefault) {
      return;
    }

    this.service
      .getRebuildEstimate(resetInput(row, layerDefault))
      .pipe(
        switchMap(estimate => {
          const message = estimateText(estimate);
          return this.confirmation.warn(message.key, 'VaultIndex::Profiles:ResetConfirm', { messageLocalizationParams: message.args });
        }),
        filter(status => status === Confirmation.Status.confirm),
        switchMap(() => this.service.reset(row.documentTypeId!)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.toaster.success('VaultIndex::Profiles:Saved');
        this.load();
      });
  }

  private load(): void {
    this.loading.set(true);
    this.service
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.rows.set(result.items ?? []);
          this.enrichmentAvailable.set(result.enrichmentAvailable ?? false);
          this.loading.set(false);
        },
        error: () => this.loading.set(false),
      });
  }
}

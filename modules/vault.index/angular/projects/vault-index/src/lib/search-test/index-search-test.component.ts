import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { LocalizationPipe } from '@abp/ng.core';
import { IndexSearchTestService } from '../proxy/http-api/search/index-search-test.service';
import type { IndexSearchTestResultDto } from '../proxy/search/models';
import {
  type CandidateOrder,
  type FieldFilterRow,
  type SearchTestForm,
  buildTestInput,
  emptyFieldFilter,
  invalidSettings,
  orderCandidates,
} from './index-search-test.view';

/**
 * The retrieval test: run a query as search runs it, with other ranking settings if wanted, and see every candidate
 * with its rank in each leg, after fusion and after the rerank. Nothing is saved; the settings apply to one run.
 */
@Component({
  selector: 'lib-index-search-test',
  templateUrl: './index-search-test.component.html',
  imports: [DecimalPipe, FormsModule, RouterLink, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IndexSearchTestComponent {
  private readonly service = inject(IndexSearchTestService);
  private readonly destroyRef = inject(DestroyRef);

  readonly orders: CandidateOrder[] = ['final', 'fused', 'keyword', 'vector'];

  form = signal<SearchTestForm>({
    query: '',
    documentTypeCode: '',
    fieldFilters: [],
    keywordWeight: '',
    vectorWeight: '',
    candidatesPerLeg: '',
    rerank: true,
    rerankCandidateCount: '',
  });
  result = signal<IndexSearchTestResultDto | null>(null);
  running = signal(false);
  order = signal<CandidateOrder>('final');

  readonly invalid = computed(() => invalidSettings(this.form()));
  readonly canRun = computed(() => this.form().query.trim() !== '' && this.invalid().length === 0 && !this.running());
  readonly candidates = computed(() => orderCandidates(this.result()?.candidates ?? [], this.order()));

  update<K extends keyof SearchTestForm>(key: K, value: SearchTestForm[K]): void {
    this.form.update(form => ({ ...form, [key]: value }));
  }

  addFilter(): void {
    this.form.update(form => ({ ...form, fieldFilters: [...form.fieldFilters, emptyFieldFilter()] }));
  }

  removeFilter(index: number): void {
    this.form.update(form => ({ ...form, fieldFilters: form.fieldFilters.filter((_, i) => i !== index) }));
  }

  updateFilter(index: number, key: keyof FieldFilterRow, value: string): void {
    this.form.update(form => ({
      ...form,
      fieldFilters: form.fieldFilters.map((row, i) => (i === index ? { ...row, [key]: value } : row)),
    }));
  }

  run(): void {
    if (!this.canRun()) {
      return;
    }

    this.running.set(true);
    this.service
      .run(buildTestInput(this.form()))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.result.set(result);
          this.running.set(false);
        },
        error: () => this.running.set(false),
      });
  }
}

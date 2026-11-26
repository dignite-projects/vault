import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { CommonModule } from '@angular/common';
import { LocalizationPipe } from '@abp/ng.core';
import {
  DocumentDto,
  DocumentService,
  DocumentTextOrigin,
  FieldDefinitionDto,
  FieldDefinitionService,
} from '@dignite/ng.vault-extract';
import { Subscription } from 'rxjs';
import { DocumentFileBlobService } from '../../shared/document-file-blob.service';
import { isImageContentType, isPdfContentType } from '../../shared/content-type';
import { DocumentTypesStore } from '../../shared/document-types.store';
import {
  DocumentLayoutViewerComponent,
  LayoutViewerField,
} from '../document-layout-viewer/document-layout-viewer.component';
import { PageRegion, readRegionsParam } from '../document-layout-viewer/layout-boxes';

/** A field value as one line of text beside the layout; a table or another structured value shows its label only. */
function displayValue(value: unknown): string {
  if (value == null || typeof value === 'object' && !Array.isArray(value)) {
    return '';
  }

  if (Array.isArray(value)) {
    return value.every(item => item == null || typeof item !== 'object') ? value.join(', ') : '';
  }

  return String(value);
}

// File preview page (route documents/:id/file). A link can name regions to outline on a PDF's pages (`?regions=`, see
// readRegionsParam) or a page to open at (`?page=`); the page then starts on the layout view. Replaces the old detail-page openFile() blob new-tab
// shortcut with a readable /vault-extract/documents/{id}/file URL that includes the document ID. The file content is
// still fetched through DocumentService.getBlob with a Bearer token and embedded as a blob, so the token
// never enters the URL. Blob lifecycle is centralized in DocumentFileBlobService (#277).
@Component({
  selector: 'lib-document-file-preview',
  templateUrl: './document-file-preview.component.html',
  imports: [CommonModule, LocalizationPipe, DocumentLayoutViewerComponent],
  providers: [DocumentFileBlobService],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentFilePreviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly documentService = inject(DocumentService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly fileBlob = inject(DocumentFileBlobService);
  private readonly documentTypes = inject(DocumentTypesStore);
  private readonly fieldDefinitionService = inject(FieldDefinitionService);
  private readonly fieldDefinitions = signal<FieldDefinitionDto[]>([]);
  private fieldDefinitionsRequest: Subscription | null = null;

  document = signal<DocumentDto | null>(null);
  isLoading = signal(true);
  /** A PDF shows its parse's layout drawn over its pages instead of the browser's own viewer. */
  showLayout = signal(false);
  /** Regions the link asked to outline, and the page it asked to open at. */
  readonly highlight = signal<PageRegion[]>([]);
  readonly startPage = signal<number | null>(null);
  hasError = signal(false);

  protected documentId!: string;

  readonly fileName = computed(
    () => this.document()?.fileOrigin?.originalFileName || this.document()?.title || '',
  );
  readonly contentType = computed(() => this.document()?.fileOrigin?.contentType ?? '');
  readonly isImage = computed(() => isImageContentType(this.contentType()));
  readonly isPdf = computed(() => isPdfContentType(this.contentType()));
  // No FileOrigin: a text submitted without its original, or a sub-document seeded from its slice (a sub-document cut by
  // page has a file of its own). The template shows a neutral notice then, which for a submitted text says why.
  readonly hasSourceFile = computed(() => !!this.document()?.fileOrigin);
  /** A document submitted as text without its original file: it never had one, which the notice says. */
  readonly isSubmittedText = computed(() => this.document()?.textOrigin === DocumentTextOrigin.Submitted);

  /**
   * The extracted fields the layout lists, labelled and ordered as the detail page shows them. Until the type's field
   * definitions are in (or if they cannot be read), every value is listed under its field name.
   */
  readonly layoutFields = computed<LayoutViewerField[]>(() => {
    const values = this.document()?.extractedFields;
    if (!values) {
      return [];
    }

    const definitions = new Map(this.fieldDefinitions().map(definition => [definition.name ?? '', definition]));
    return Object.keys(values)
      .filter(name => definitions.size === 0 || definitions.has(name))
      .sort((a, b) =>
        (definitions.get(a)?.displayOrder ?? 0) - (definitions.get(b)?.displayOrder ?? 0) || a.localeCompare(b))
      .map(name => ({ name, label: definitions.get(name)?.displayName || name, value: displayValue(values[name]) }));
  });

  constructor() {
    // The field definitions of the document's type: once the document and the visible types are both in.
    effect(() => {
      const code = this.document()?.documentTypeCode;
      const typeId = code ? this.documentTypes.value().find(type => type.typeCode === code)?.id : undefined;
      untracked(() => this.loadFieldDefinitions(typeId));
    });
  }

  ngOnInit(): void {
    this.documentId = this.route.snapshot.paramMap.get('id')!;
    const query = this.route.snapshot.queryParamMap;
    const page = Number(query.get('page'));
    this.highlight.set(readRegionsParam(query.get('regions')));
    this.startPage.set(Number.isInteger(page) && page > 0 ? page : null);
    if (this.highlight().length > 0 || this.startPage()) {
      this.showLayout.set(true);
    }
    this.load();
  }

  private load(): void {
    this.isLoading.set(true);
    this.hasError.set(false);
    // Fetch metadata first to get contentType and filename for rendering decisions, then let the service
    // fetch the blob body.
    this.documentService
      .get(this.documentId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: doc => {
          this.document.set(doc);
          this.isLoading.set(false);
          // No file of its own (a submitted text, a sub-document seeded from its slice): nothing to fetch.
          if (doc.fileOrigin) {
            this.fileBlob.ensureLoaded(this.documentId);
          }
        },
        error: () => {
          this.isLoading.set(false);
          this.hasError.set(true);
        },
      });
  }

  private loadFieldDefinitions(documentTypeId: string | undefined): void {
    this.fieldDefinitionsRequest?.unsubscribe();
    this.fieldDefinitionsRequest = null;
    if (!documentTypeId) {
      this.fieldDefinitions.set([]);
      return;
    }

    this.fieldDefinitionsRequest = this.fieldDefinitionService
      .getList({ documentTypeId })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: definitions => this.fieldDefinitions.set(definitions),
        error: () => this.fieldDefinitions.set([]),
      });
  }

  back(): void {
    this.router.navigate(['/vault-extract/documents', this.documentId]);
  }
}

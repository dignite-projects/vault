import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { LocalizationPipe } from '@abp/ng.core';
import { DocumentService } from '@dignite/ng.vault-extract';
import { HttpErrorResponse } from '@angular/common/http';
import type { PDFDocumentLoadingTask, PDFDocumentProxy } from 'pdfjs-dist';
import { LayoutBox, LayoutField, readLayout } from './layout-boxes';

/** A field listed beside the pages: its name in the layout, and the label and value the page shows for it. */
export interface LayoutViewerField {
  name: string;
  label: string;
  value: string;
}

/** A rendered page: its size on screen and the boxes drawn over it, already in screen pixels. */
interface ViewerPage {
  number: number;
  width: number;
  height: number;
  boxes: ViewerBox[];
  /** The regions of every field on this page; a box's `text` is its field's name. */
  fieldBoxes: ViewerBox[];
}

interface ViewerBox extends LayoutBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

type ViewerState = 'loading' | 'ready' | 'noLayout' | 'error';

/** Widest a page is drawn, in CSS pixels. */
const MAX_PAGE_WIDTH = 960;

/**
 * Loads pdf.js on first use, with its worker run on the main thread: importing the worker module registers it
 * (`globalThis.pdfjsWorker`), which pdf.js takes instead of a worker script, so the host needs no asset for it.
 */
async function loadPdfJs() {
  const [pdfjs] = await Promise.all([
    import('pdfjs-dist'),
    // @ts-expect-error -- pdf.js ships no typings for its worker module; it is imported for its side effect.
    import('pdfjs-dist/build/pdf.worker.min.mjs'),
  ]);
  return pdfjs;
}

/**
 * Shows a PDF document's pages with the regions its parse read drawn over them (GET .../documents/{id}/layout): every
 * paragraph, heading, list item, table and table cell, and image, coloured by kind. Hovering a region shows its text;
 * clicking it keeps it selected. A page read by OCR carries no regions and is shown plain.
 *
 * Beside the pages it lists the extracted fields: pointing at one outlines where its value was found (the layout's
 * `fields`), clicking keeps it outlined and scrolls to it. A field without a known place is listed and says so.
 */
@Component({
  selector: 'lib-document-layout-viewer',
  templateUrl: './document-layout-viewer.component.html',
  styleUrl: './document-layout-viewer.component.scss',
  imports: [LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DocumentLayoutViewerComponent {
  readonly documentId = input.required<string>();
  /** The PDF itself (a blob: URL the preview page already holds). */
  readonly fileUrl = input.required<string>();
  /** The fields to list, in display order. Without them, the layout's own field names are listed. */
  readonly fields = input<LayoutViewerField[]>([]);

  private readonly documentService = inject(DocumentService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly canvases = viewChildren<ElementRef<HTMLCanvasElement>>('pageCanvas');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly state = signal<ViewerState>('loading');
  readonly current = signal(true);
  readonly pages = signal<ViewerPage[]>([]);
  readonly selected = signal<ViewerBox | null>(null);
  readonly hovered = signal<ViewerBox | null>(null);
  readonly shown = computed(() => this.hovered() ?? this.selected());

  private readonly layoutFields = signal<LayoutField[]>([]);
  readonly selectedField = signal<string | null>(null);
  readonly hoveredField = signal<string | null>(null);
  readonly activeField = computed(() => this.hoveredField() ?? this.selectedField());
  readonly fieldRows = computed(() => {
    const located = new Set(this.layoutFields().filter(field => field.boxes.length > 0).map(field => field.name));
    const given = this.fields();
    const rows = given.length > 0 ? given : this.layoutFields().map(field => ({ name: field.name, label: field.name, value: '' }));
    return rows.map(row => ({ ...row, located: located.has(row.name) }));
  });

  private loading: PDFDocumentLoadingTask | null = null;
  private pdf: PDFDocumentProxy | null = null;
  private scales = new Map<number, number>();

  constructor() {
    effect(() => {
      const id = this.documentId();
      const url = this.fileUrl();
      this.load(id, url);
    });

    // The canvases exist once the pages render; draw into them then.
    effect(() => {
      const canvases = this.canvases();
      if (canvases.length > 0) {
        void this.paint(canvases);
      }
    });

    this.destroyRef.onDestroy(() => void this.loading?.destroy());
  }

  select(box: ViewerBox): void {
    this.selected.set(this.selected() === box ? null : box);
  }

  selectField(name: string): void {
    const selecting = this.selectedField() !== name;
    this.selectedField.set(selecting ? name : null);
    if (selecting) {
      // The outline renders on the next change detection; bring its first region into view then.
      setTimeout(() => this.host.nativeElement.querySelector('.layout-field')?.scrollIntoView({ block: 'center', behavior: 'smooth' }));
    }
  }

  private load(id: string, url: string): void {
    this.state.set('loading');
    this.documentService
      // A document without a layout answers with an error the page explains itself: no global error dialog.
      .getLayout(id, { skipHandleError: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: async blob => {
          try {
            await this.render(readLayout(JSON.parse(await blob.text())), url);
          } catch {
            this.state.set('error');
          }
        },
        error: (error: HttpErrorResponse) => {
          // Extract:DocumentNoLayout: a sub-document, or a document parsed before layouts were kept.
          this.state.set(error.error?.error?.code === 'Extract:DocumentNoLayout' ? 'noLayout' : 'error');
        },
      });
  }

  private async render(layout: ReturnType<typeof readLayout>, url: string): Promise<void> {
    const pdfjs = await loadPdfJs();
    const data = await (await fetch(url)).arrayBuffer();
    this.loading = pdfjs.getDocument({ data });
    this.pdf = await this.loading.promise;

    const fieldBoxes = layout.fields.flatMap(field => field.boxes);
    const pages: ViewerPage[] = [];
    for (let number = 1; number <= this.pdf.numPages; number++) {
      const page = await this.pdf.getPage(number);
      const natural = page.getViewport({ scale: 1 });
      const scale = Math.min(1.5, MAX_PAGE_WIDTH / natural.width);
      const viewport = page.getViewport({ scale });
      this.scales.set(number, scale);
      const onScreen = (boxes: LayoutBox[]): ViewerBox[] =>
        boxes
          .filter(box => box.pageNumber === number)
          .map(box => {
            const [x1, y1] = viewport.convertToViewportPoint(box.left, box.bottom);
            const [x2, y2] = viewport.convertToViewportPoint(box.right, box.top);
            return { ...box, x: Math.min(x1, x2), y: Math.min(y1, y2), w: Math.abs(x2 - x1), h: Math.abs(y2 - y1) };
          });
      pages.push({ number, width: viewport.width, height: viewport.height, boxes: onScreen(layout.boxes), fieldBoxes: onScreen(fieldBoxes) });
    }

    this.layoutFields.set(layout.fields);
    this.current.set(layout.current);
    this.pages.set(pages);
    this.state.set('ready');
  }

  /** Draws each page into its canvas, sharp on high-density screens. */
  private async paint(canvases: readonly ElementRef<HTMLCanvasElement>[]): Promise<void> {
    if (!this.pdf) {
      return;
    }

    const ratio = window.devicePixelRatio || 1;
    for (const ref of canvases) {
      const canvas = ref.nativeElement;
      const number = Number(canvas.dataset['page']);
      const page = await this.pdf.getPage(number);
      const viewport = page.getViewport({ scale: (this.scales.get(number) ?? 1) * ratio });
      canvas.width = viewport.width;
      canvas.height = viewport.height;
      await page.render({ canvas, viewport }).promise;
    }
  }
}

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
import { LayoutBox, readLayout } from './layout-boxes';

/** A rendered page: its size on screen and the boxes drawn over it, already in screen pixels. */
interface ViewerPage {
  number: number;
  width: number;
  height: number;
  boxes: ViewerBox[];
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

  private readonly documentService = inject(DocumentService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly canvases = viewChildren<ElementRef<HTMLCanvasElement>>('pageCanvas');

  readonly state = signal<ViewerState>('loading');
  readonly current = signal(true);
  readonly pages = signal<ViewerPage[]>([]);
  readonly selected = signal<ViewerBox | null>(null);
  readonly hovered = signal<ViewerBox | null>(null);
  readonly shown = computed(() => this.hovered() ?? this.selected());

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

    const pages: ViewerPage[] = [];
    for (let number = 1; number <= this.pdf.numPages; number++) {
      const page = await this.pdf.getPage(number);
      const natural = page.getViewport({ scale: 1 });
      const scale = Math.min(1.5, MAX_PAGE_WIDTH / natural.width);
      const viewport = page.getViewport({ scale });
      this.scales.set(number, scale);
      pages.push({
        number,
        width: viewport.width,
        height: viewport.height,
        boxes: layout.boxes
          .filter(box => box.pageNumber === number)
          .map(box => {
            const [x1, y1] = viewport.convertToViewportPoint(box.left, box.bottom);
            const [x2, y2] = viewport.convertToViewportPoint(box.right, box.top);
            return { ...box, x: Math.min(x1, x2), y: Math.min(y1, y2), w: Math.abs(x2 - x1), h: Math.abs(y2 - y1) };
          }),
      });
    }

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

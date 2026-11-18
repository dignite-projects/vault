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
import type { PDFDocumentLoadingTask, PDFDocumentProxy, RenderTask } from 'pdfjs-dist';
import type { Subscription } from 'rxjs';
import { LayoutBox, LayoutField, layoutErrorCode, readLayout } from './layout-boxes';
import { PageBuffer, samePages } from './page-buffer';

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
  private readonly pageFrames = viewChildren<ElementRef<HTMLElement>>('pageFrame');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly state = signal<ViewerState>('loading');
  readonly current = signal(true);
  readonly pages = signal<ViewerPage[]>([]);
  /**
   * The pages drawn, with their canvas and regions: the ones in view and the most recently seen (see PageBuffer). The
   * others are empty frames of their size, so a long document neither holds a canvas per page nor its regions.
   */
  readonly drawn = signal<ReadonlySet<number>>(new Set());
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
  /** Which load is current: a newer one (another document) or the component's end makes an older load stop. */
  private generation = 0;
  private request: Subscription | null = null;
  /** The drawing in progress on each canvas; a canvas that leaves the buffer has its drawing cancelled. */
  private renders = new Map<HTMLCanvasElement, RenderTask>();
  private painted = new WeakSet<HTMLCanvasElement>();
  private buffer = new PageBuffer();
  private observer: IntersectionObserver | null = null;
  private scrollTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      const id = this.documentId();
      const url = this.fileUrl();
      this.load(id, url);
    });

    // A canvas exists while its page is drawn; draw into a new one, and stop drawing into one that went.
    effect(() => {
      const canvases = this.canvases().map(ref => ref.nativeElement);
      for (const [canvas, task] of this.renders) {
        if (!canvases.includes(canvas)) {
          task.cancel();
          this.renders.delete(canvas);
        }
      }

      for (const canvas of canvases) {
        if (!this.painted.has(canvas)) {
          this.painted.add(canvas);
          void this.paint(canvas);
        }
      }
    });

    // Watch which page frames are near the view; the buffer decides which pages are drawn.
    effect(() => {
      const frames = this.pageFrames().map(ref => ref.nativeElement);
      this.observer?.disconnect();
      this.observer = null;
      if (frames.length === 0) {
        return;
      }

      if (typeof IntersectionObserver === 'undefined') {
        this.drawn.set(new Set(this.buffer.update(frames.slice(0, 1).map(frame => ({ page: Number(frame.dataset['page']), inView: true })))));
        return;
      }

      // Half a view ahead on each side, so a page is drawn as it scrolls in rather than after.
      this.observer = new IntersectionObserver(entries => {
        const kept = this.buffer.update(entries.map(entry => ({
          page: Number((entry.target as HTMLElement).dataset['page']),
          inView: entry.isIntersecting,
        })));
        if (!samePages(kept, this.drawn())) {
          this.drawn.set(kept);
        }
      }, { rootMargin: '50% 0px' });
      frames.forEach(frame => this.observer!.observe(frame));
    });

    this.destroyRef.onDestroy(() => {
      this.release();
      if (this.scrollTimer !== null) {
        clearTimeout(this.scrollTimer);
      }
    });
  }

  select(box: ViewerBox): void {
    this.selected.set(this.selected() === box ? null : box);
  }

  selectField(name: string): void {
    const selecting = this.selectedField() !== name;
    this.selectedField.set(selecting ? name : null);
    if (selecting) {
      // The outline renders on the next change detection; bring its first region into view then.
      if (this.scrollTimer !== null) {
        clearTimeout(this.scrollTimer);
      }
      this.scrollTimer = setTimeout(() => {
        this.scrollTimer = null;
        this.host.nativeElement.querySelector('.layout-field')?.scrollIntoView({ block: 'center', behavior: 'smooth' });
      });
    }
  }

  private load(id: string, url: string): void {
    this.release();
    const generation = this.generation;
    this.state.set('loading');
    this.pages.set([]);
    this.layoutFields.set([]);
    this.selected.set(null);
    this.hovered.set(null);
    this.selectedField.set(null);
    this.hoveredField.set(null);

    this.request = this.documentService
      // A document without a layout answers with an error the page explains itself: no global error dialog.
      .getLayout(id, { skipHandleError: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: async blob => {
          try {
            await this.render(readLayout(JSON.parse(await blob.text())), url, generation);
          } catch {
            if (generation === this.generation) {
              this.state.set('error');
            }
          }
        },
        error: async (error: HttpErrorResponse) => {
          // Extract:DocumentNoLayout: a document parsed before layouts were kept, or a sub-document taken from a slice.
          const code = await layoutErrorCode(error.error);
          if (generation === this.generation) {
            this.state.set(code === 'Extract:DocumentNoLayout' ? 'noLayout' : 'error');
          }
        },
      });
  }

  /** Ends the current load: its request, its drawing and its PDF; whatever it still awaits then stops. */
  private release(): void {
    this.generation++;
    this.request?.unsubscribe();
    this.request = null;
    this.observer?.disconnect();
    this.observer = null;
    this.renders.forEach(task => task.cancel());
    this.renders.clear();
    this.buffer = new PageBuffer();
    this.drawn.set(new Set());
    void this.loading?.destroy();
    this.loading = null;
    this.pdf = null;
  }

  private async render(layout: ReturnType<typeof readLayout>, url: string, generation: number): Promise<void> {
    const pdfjs = await loadPdfJs();
    const data = await (await fetch(url)).arrayBuffer();
    if (generation !== this.generation) {
      return;
    }

    const loading = pdfjs.getDocument({ data });
    this.loading = loading;
    const pdf = await loading.promise;
    if (generation !== this.generation) {
      return;
    }

    this.pdf = pdf;
    const fieldBoxes = ([] as LayoutBox[]).concat(...layout.fields.map(field => field.boxes));
    const pages: ViewerPage[] = [];
    for (let number = 1; number <= pdf.numPages; number++) {
      const page = await pdf.getPage(number);
      if (generation !== this.generation) {
        return;
      }

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

  /**
   * Draws a page into its canvas, sharp on high-density screens. A newer load, or the page leaving the buffer (its
   * canvas goes, and its drawing is cancelled), stops it.
   */
  private async paint(canvas: HTMLCanvasElement): Promise<void> {
    const pdf = this.pdf;
    if (!pdf) {
      return;
    }

    const generation = this.generation;
    const number = Number(canvas.dataset['page']);
    try {
      const page = await pdf.getPage(number);
      if (generation !== this.generation || this.pdf !== pdf || !canvas.isConnected) {
        return;
      }

      const viewport = page.getViewport({ scale: (this.scales.get(number) ?? 1) * (window.devicePixelRatio || 1) });
      canvas.width = viewport.width;
      canvas.height = viewport.height;
      const task = page.render({ canvas, viewport });
      this.renders.set(canvas, task);
      await task.promise;
    } catch {
      // Cancelled by a newer load or by the page leaving the buffer, or the document was destroyed under it.
    } finally {
      this.renders.delete(canvas);
    }
  }
}

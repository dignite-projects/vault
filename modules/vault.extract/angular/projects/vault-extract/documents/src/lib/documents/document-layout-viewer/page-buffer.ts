// Which pages of the layout viewer keep their drawing. A drawn page is a canvas of its full size at the screen's pixel
// density, about 20 MB for an A4 page on a high-density screen, so a document of a few hundred pages cannot keep every
// page drawn. pdf.js's own viewer keeps the pages in view and the most recently seen up to a fixed count
// (PDFPageViewBuffer, DEFAULT_CACHE_SIZE 10; mozilla/pdf.js, Apache-2.0); this does the same.

/** How many pages stay drawn besides the ones in view, as pdf.js's viewer keeps. */
export const DRAWN_PAGE_BUFFER = 10;

/** A page that came into view or left it. */
export interface PageVisibility {
  page: number;
  inView: boolean;
}

export class PageBuffer {
  private readonly inView = new Set<number>();
  /** The pages seen, the most recent last. */
  private recent: number[] = [];

  constructor(private readonly size = DRAWN_PAGE_BUFFER) {}

  /** Records pages entering or leaving the view, and returns the pages to keep drawn. */
  update(changes: readonly PageVisibility[]): ReadonlySet<number> {
    for (const change of changes) {
      if (change.inView) {
        this.inView.add(change.page);
        this.recent = [...this.recent.filter(page => page !== change.page), change.page];
      } else {
        this.inView.delete(change.page);
      }
    }

    const kept = new Set([...this.inView, ...this.recent.slice(-this.size)]);
    this.recent = this.recent.filter(page => kept.has(page));
    return kept;
  }
}

/** Whether two sets of pages hold the same pages. */
export function samePages(a: ReadonlySet<number>, b: ReadonlySet<number>): boolean {
  return a.size === b.size && [...a].every(page => b.has(page));
}

// Reads the layout JSON GET /api/vault-extract/documents/{id}/layout returns into the boxes the viewer draws: every
// element (and table cell) region on a page, in the page's own frame. See docs/en/egress/rest-api.md.

/** What a box outlines: a block of text by its role, a table or one of its cells, or an image. */
export type LayoutBoxKind =
  | 'paragraph'
  | 'heading'
  | 'listItem'
  | 'code'
  | 'quote'
  | 'markdown'
  | 'pageHeader'
  | 'pageFooter'
  | 'table'
  | 'cell'
  | 'image'
  | 'field';

/** A region of a page: its corners in PDF points from the bottom-left corner of the page as it is shown. */
export interface LayoutBox {
  pageNumber: number;
  kind: LayoutBoxKind;
  /** The element's text (inline Markdown), or an image's OCR text; empty for a table. */
  text: string;
  left: number;
  bottom: number;
  right: number;
  top: number;
}

export interface LayoutPage {
  number: number;
  width?: number;
  height?: number;
  /** Whether the page's regions are in PDF points from the bottom-left corner, the frame the viewer draws. */
  drawable: boolean;
}

/**
 * A region handed to the viewer from outside the layout, such as a Vault Index chunk's: a box whose origin is the page's
 * top-left corner, each value a fraction of the page's width (`left`, `width`) or height (`top`, `height`).
 */
export interface PageRegion {
  page: number;
  left: number;
  top: number;
  width: number;
  height: number;
}

/** The most regions a link outlines; more are ignored. */
export const MAX_LINKED_REGIONS = 64;

/** A rectangle on a rendered page, in its pixels from the top-left corner. */
export interface ScreenRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Where a layout box lands on a page rendered at `rendered` size. The PDF engine (PdfPig) reads a page as it is shown:
 * its points start at the bottom-left corner of the visible page, after the page's CropBox and its /Rotate, and the
 * page's width and height are the visible page's. So the box scales onto the rendered page as it is; pdf.js's viewport
 * transform would crop and rotate it a second time. `page` is the layout's size of the page, or else pdf.js's size of
 * the displayed page at scale 1, which is the same frame.
 */
export function boxOnScreen(
  box: Pick<LayoutBox, 'left' | 'bottom' | 'right' | 'top'>,
  page: { width: number; height: number },
  rendered: { width: number; height: number },
): ScreenRect {
  const sx = rendered.width / page.width;
  const sy = rendered.height / page.height;
  return {
    x: box.left * sx,
    y: (page.height - box.top) * sy,
    w: (box.right - box.left) * sx,
    h: (box.top - box.bottom) * sy,
  };
}

/** Where a region in fractions of the page (a Vault Index chunk's) lands on a page rendered at `rendered` size. */
export function regionOnScreen(region: PageRegion, rendered: { width: number; height: number }): ScreenRect {
  return {
    x: region.left * rendered.width,
    y: region.top * rendered.height,
    w: region.width * rendered.width,
    h: region.height * rendered.height,
  };
}

/**
 * Reads the `regions` query parameter of the file preview: boxes `page,left,top,width,height` joined by `;`, such as
 * `2,0.118,0.214,0.764,0.061`. A malformed box, or one off the page, is skipped.
 */
export function readRegionsParam(value: string | null | undefined): PageRegion[] {
  const regions: PageRegion[] = [];
  for (const part of (value ?? '').split(';')) {
    const numbers = part.split(',').map(Number);
    if (numbers.length !== 5 || numbers.some(n => !Number.isFinite(n))) {
      continue;
    }

    const [page, left, top, width, height] = numbers;
    if (!Number.isInteger(page) || page < 1 || left < 0 || top < 0 || width <= 0 || height <= 0
      || left + width > 1.0001 || top + height > 1.0001) {
      continue;
    }

    regions.push({ page, left, top, width, height });
    if (regions.length === MAX_LINKED_REGIONS) {
      break;
    }
  }

  return regions;
}

/** Where a field's value was found: the regions of the text extraction quoted it from (the layout's `fields`). */
export interface LayoutField {
  name: string;
  boxes: LayoutBox[];
}

export interface Layout {
  /** Whether the document's Markdown is still the one this layout rendered (false after an operator's correction). */
  current: boolean;
  pages: LayoutPage[];
  boxes: LayoutBox[];
  /** Empty while the layout is not current: the field ranges then belong to text the layout did not render. */
  fields: LayoutField[];
}

interface JsonSource {
  page?: string;
  polygon?: number[][] | null;
}

interface JsonElement {
  type?: string;
  role?: string | null;
  text?: string | null;
  elements?: JsonElement[];
  cells?: { content?: JsonElement | null; sources?: JsonSource[] }[];
  sources?: JsonSource[];
}

interface JsonPage {
  id: string;
  number?: number | null;
  width?: number | null;
  height?: number | null;
  unit?: string | null;
  origin?: string | null;
}

const ROLES: readonly LayoutBoxKind[] = ['heading', 'listItem', 'code', 'quote', 'markdown', 'pageHeader', 'pageFooter'];

/**
 * The boxes of a layout. Only regions in PDF points from the bottom-left corner — what the PDF engine writes — are
 * drawn; a page read by OCR or an image carries its page only and yields no box.
 */
export function readLayout(json: unknown): Layout {
  const root = (json ?? {}) as {
    current?: boolean;
    fields?: { name?: string; sources?: JsonSource[] }[];
    document?: { pages?: JsonPage[]; sections?: JsonElement[] };
  };
  const jsonPages = root.document?.pages ?? [];
  const drawable = new Map<string, number>();
  const pages: LayoutPage[] = [];
  for (const page of jsonPages) {
    if (page.number == null) {
      continue;
    }

    const inPoints = page.unit === 'Point' && page.origin === 'BottomLeft';
    pages.push({ number: page.number, width: page.width ?? undefined, height: page.height ?? undefined, drawable: inPoints });
    if (inPoints) {
      drawable.set(page.id, page.number);
    }
  }

  const toBoxes = (kind: LayoutBoxKind, text: string, sources: JsonSource[] | undefined): LayoutBox[] => {
    const result: LayoutBox[] = [];
    for (const source of sources ?? []) {
      const pageNumber = source.page != null ? drawable.get(source.page) : undefined;
      const polygon = source.polygon;
      if (pageNumber == null || !polygon || polygon.length === 0) {
        continue;
      }

      const xs = polygon.map(point => point[0]);
      const ys = polygon.map(point => point[1]);
      result.push({
        pageNumber,
        kind,
        text,
        left: Math.min(...xs),
        right: Math.max(...xs),
        bottom: Math.min(...ys),
        top: Math.max(...ys),
      });
    }

    return result;
  };

  const boxes: LayoutBox[] = [];
  const add = (kind: LayoutBoxKind, text: string, sources: JsonSource[] | undefined) => boxes.push(...toBoxes(kind, text, sources));

  const walk = (element: JsonElement) => {
    switch (element.type) {
      case 'section':
        element.elements?.forEach(walk);
        break;
      case 'text': {
        const role = element.role as LayoutBoxKind | undefined;
        add(role && ROLES.includes(role) ? role : 'paragraph', element.text ?? '', element.sources);
        break;
      }
      case 'image':
        add('image', element.text ?? '', element.sources);
        break;
      case 'table':
        add('table', '', element.sources);
        for (const cell of element.cells ?? []) {
          add('cell', cell.content?.text ?? '', cell.sources);
        }

        break;
    }
  };

  root.document?.sections?.forEach(walk);
  const fields = (root.fields ?? [])
    .filter(field => !!field.name)
    .map(field => ({ name: field.name!, boxes: toBoxes('field', field.name!, field.sources) }));
  return { current: root.current !== false, pages, boxes, fields };
}

/**
 * The ABP error code of a failed layout request. The layout is requested as a blob, so the error body arrives as a Blob
 * too, not parsed JSON: it is read as text first. Undefined when the body carries no code.
 */
export async function layoutErrorCode(body: unknown): Promise<string | undefined> {
  let text: string;
  if (body instanceof Blob) {
    text = await body.text();
  } else if (typeof body === 'string') {
    text = body;
  } else {
    return (body as { error?: { code?: string } } | null)?.error?.code ?? undefined;
  }

  try {
    return (JSON.parse(text) as { error?: { code?: string } })?.error?.code ?? undefined;
  } catch {
    return undefined;
  }
}

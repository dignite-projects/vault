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

/** A region of a page: its corners in PDF points from the page's bottom-left corner. */
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

    pages.push({ number: page.number, width: page.width ?? undefined, height: page.height ?? undefined });
    if (page.unit === 'Point' && page.origin === 'BottomLeft') {
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

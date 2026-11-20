import { ChunkContent } from '../proxy/indexing/chunk-content.enum';

// Where a chunk came from, as the chunk view and the retrieval test show it: what it holds besides text, and a link to
// Vault Extract's file preview that outlines it on its pages (docs/en/egress/index-search.md#regions).

/** The part of a chunk, hit or candidate DTO this reads. */
export interface ChunkSource {
  firstPage?: number | null;
  regions?: { page?: number; left?: number; top?: number; width?: number; height?: number }[] | null;
  /** Flags: a table, a figure, or both. */
  content?: ChunkContent | null;
}

/** The route of a document's file preview in Vault Extract's SPA. */
export function sourceLink(documentId: string | null | undefined): string[] {
  return ['/vault-extract/documents', documentId ?? '', 'file'];
}

/**
 * The preview's query: the regions to outline (`regions=page,left,top,width,height;…`, see the preview's
 * readRegionsParam), or else the first page to open at; `null` when the chunk has no known place.
 */
export function sourceQuery(source: ChunkSource): Record<string, string> | null {
  const regions = (source.regions ?? [])
    .filter(r => r.page != null && r.left != null && r.top != null && r.width != null && r.height != null)
    .map(r => [r.page, r.left, r.top, r.width, r.height].join(','));
  if (regions.length > 0) {
    return { regions: regions.join(';') };
  }

  return source.firstPage ? { page: String(source.firstPage) } : null;
}

/** The localization keys of what a chunk holds besides text. */
export function contentKeys(content: ChunkContent | null | undefined): string[] {
  const keys: string[] = [];
  if ((content ?? 0) & ChunkContent.Table) {
    keys.push('VaultIndex::Chunks:Table');
  }
  if ((content ?? 0) & ChunkContent.Figure) {
    keys.push('VaultIndex::Chunks:Figure');
  }
  return keys;
}

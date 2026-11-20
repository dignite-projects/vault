import { ChunkContent } from '../proxy/indexing/chunk-content.enum';
import { contentKeys, sourceLink, sourceQuery } from './chunk-source';

describe('chunk source', () => {
  it('links to the regions of a chunk, or else its first page', () => {
    expect(sourceQuery({
      firstPage: 2,
      regions: [
        { page: 2, left: 0.118, top: 0.214, width: 0.764, height: 0.061 },
        { page: 3, left: 0, top: 0, width: 1, height: 0.5 },
      ],
    })).toEqual({ regions: '2,0.118,0.214,0.764,0.061;3,0,0,1,0.5' });
    expect(sourceQuery({ firstPage: 4, regions: null })).toEqual({ page: '4' });
    expect(sourceQuery({ firstPage: null, regions: [] })).toBeNull();
    expect(sourceLink('d1')).toEqual(['/vault-extract/documents', 'd1', 'file']);
  });

  it('names a table and a figure', () => {
    expect(contentKeys(ChunkContent.Text)).toEqual([]);
    expect(contentKeys(null)).toEqual([]);
    expect(contentKeys(ChunkContent.Table | ChunkContent.Figure)).toEqual(['VaultIndex::Chunks:Table', 'VaultIndex::Chunks:Figure']);
  });
});

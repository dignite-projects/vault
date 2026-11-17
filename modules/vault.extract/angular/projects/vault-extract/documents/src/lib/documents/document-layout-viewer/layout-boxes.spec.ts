import { describe, expect, it } from 'vitest';

import { readLayout } from './layout-boxes';

const square = (left: number, bottom: number, right: number, top: number) => [
  [left, top],
  [right, top],
  [right, bottom],
  [left, bottom],
];

describe('readLayout', () => {
  it('reads every element and table cell region of a PDF page', () => {
    const layout = readLayout({
      current: true,
      document: {
        version: 1,
        pages: [{ id: 'page-1', number: 1, width: 595, height: 842, unit: 'Point', origin: 'BottomLeft' }],
        sections: [
          {
            type: 'section',
            sources: [{ page: 'page-1' }],
            elements: [
              { type: 'text', role: 'heading', text: 'Title', sources: [{ page: 'page-1', polygon: square(50, 780, 300, 800) }] },
              { type: 'text', text: 'Body', sources: [{ page: 'page-1', polygon: square(50, 700, 545, 760) }] },
              {
                type: 'table',
                sources: [{ page: 'page-1', polygon: square(50, 500, 545, 680) }],
                cells: [{ content: { type: 'text', text: 'Item' }, sources: [{ page: 'page-1', polygon: square(50, 660, 300, 680) }] }],
              },
              { type: 'image', text: 'OCR', sources: [{ page: 'page-1', polygon: square(60, 100, 200, 300) }] },
            ],
          },
        ],
      },
    });

    expect(layout.current).toBe(true);
    expect(layout.pages).toEqual([{ number: 1, width: 595, height: 842 }]);
    expect(layout.boxes.map(box => [box.kind, box.text])).toEqual([
      ['heading', 'Title'],
      ['paragraph', 'Body'],
      ['table', ''],
      ['cell', 'Item'],
      ['image', 'OCR'],
    ]);
    expect(layout.boxes[0]).toMatchObject({ pageNumber: 1, left: 50, right: 300, bottom: 780, top: 800 });
  });

  it('draws nothing for a page read by OCR, which carries no regions', () => {
    const layout = readLayout({
      current: false,
      document: {
        pages: [{ id: 'page-2', number: 2 }],
        sections: [{ type: 'section', elements: [{ type: 'text', text: 'scanned', sources: [{ page: 'page-2' }] }] }],
      },
    });

    expect(layout.current).toBe(false);
    expect(layout.pages).toEqual([{ number: 2 }]);
    expect(layout.boxes).toEqual([]);
  });

  it('ignores regions in a frame it cannot place on a PDF page', () => {
    const layout = readLayout({
      document: {
        pages: [{ id: 'slide-1', number: 1, unit: 'Point', origin: 'TopLeft' }],
        sections: [{ type: 'section', elements: [{ type: 'text', text: 'x', sources: [{ page: 'slide-1', polygon: square(0, 0, 10, 10) }] }] }],
      },
    });

    expect(layout.boxes).toEqual([]);
  });
});

import { describe, expect, it } from 'vitest';

import {
  MAX_LINKED_REGIONS,
  boxOnScreen,
  layoutErrorCode,
  readLayout,
  readRegionsParam,
  regionOnScreen,
} from './layout-boxes';

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
    expect(layout.pages).toEqual([{ number: 1, width: 595, height: 842, drawable: true }]);
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
    expect(layout.pages).toEqual([{ number: 2, drawable: false }]);
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

  it("places each field's regions, and keeps a field whose regions cannot be drawn", () => {
    const layout = readLayout({
      current: true,
      fields: [
        { name: 'total_amount', sources: [{ page: 'page-1', polygon: square(50, 520, 120, 540) }, { page: 'page-1', polygon: square(300, 520, 545, 540) }] },
        { name: 'scanned_field', sources: [{ page: 'page-2' }] },
        { sources: [{ page: 'page-1', polygon: square(0, 0, 1, 1) }] },
      ],
      document: {
        pages: [
          { id: 'page-1', number: 1, unit: 'Point', origin: 'BottomLeft' },
          { id: 'page-2', number: 2 },
        ],
        sections: [],
      },
    });

    expect(layout.fields.map(field => [field.name, field.boxes.length])).toEqual([
      ['total_amount', 2],
      ['scanned_field', 0],
    ]);
    expect(layout.fields[0].boxes[1]).toMatchObject({ kind: 'field', pageNumber: 1, left: 300, right: 545 });
  });

  it('has no fields when the layout carries none', () => {
    expect(readLayout({ current: true, document: { pages: [], sections: [] } }).fields).toEqual([]);
  });
});

describe('layoutErrorCode', () => {
  it('reads the code from an error body that arrives as a blob', async () => {
    const body = new Blob([JSON.stringify({ error: { code: 'Extract:DocumentNoLayout', message: 'no layout' } })], { type: 'application/json' });
    expect(await layoutErrorCode(body)).toBe('Extract:DocumentNoLayout');
  });

  it('reads parsed and text bodies too, and has no code for anything else', async () => {
    expect(await layoutErrorCode({ error: { code: 'Extract:DocumentNoLayout' } })).toBe('Extract:DocumentNoLayout');
    expect(await layoutErrorCode('{"error":{"code":"X"}}')).toBe('X');
    expect(await layoutErrorCode(new Blob(['<html>Bad gateway</html>']))).toBeUndefined();
    expect(await layoutErrorCode(null)).toBeUndefined();
  });
});

describe('readRegionsParam', () => {
  it('reads boxes of page, left, top, width and height', () => {
    expect(readRegionsParam('2,0.118,0.214,0.764,0.061;3,0,0,1,0.5')).toEqual([
      { page: 2, left: 0.118, top: 0.214, width: 0.764, height: 0.061 },
      { page: 3, left: 0, top: 0, width: 1, height: 0.5 },
    ]);
  });

  it('skips a malformed box or one off the page', () => {
    expect(readRegionsParam('2,0.1,0.2;x,0,0,1,1;0,0,0,1,1;1.5,0,0,1,1;1,0.6,0,0.5,0.1;1,0,0,0,0.1;1,0.1,0.1,0.2,0.2')).toEqual([
      { page: 1, left: 0.1, top: 0.1, width: 0.2, height: 0.2 },
    ]);
    expect(readRegionsParam(null)).toEqual([]);
    expect(readRegionsParam('')).toEqual([]);
  });

  it('reads at most the cap', () => {
    const many = Array.from({ length: MAX_LINKED_REGIONS + 5 }, () => '1,0,0,0.1,0.1').join(';');
    expect(readRegionsParam(many).length).toBe(MAX_LINKED_REGIONS);
  });
});

describe('boxOnScreen', () => {
  // Measured on a PDF whose page is 600 x 800 points, cropped to [50 100 550 700] and turned by /Rotate 90: pdf.js shows
  // it 600 x 500, and PdfPig reads "ALPHA" at 60,420 - 125.6,434.4 on that page as shown. pdf.js draws the word's left
  // edge at 10% of the page width and its baseline 16% from the top. Through pdf.js's viewport transform, as the viewer
  // used to draw it, the same box landed at 53% across, turned on its side.
  const page = { width: 600, height: 500 };
  const alpha = { left: 60, bottom: 420, right: 125.6, top: 434.4 };

  it('scales a box onto a rotated and cropped page as the page is shown', () => {
    const box = boxOnScreen(alpha, page, { width: 900, height: 750 });

    expect(box.x / 900).toBeCloseTo(0.1, 4);
    expect((box.y + box.h) / 750).toBeCloseTo(1 - 420 / 500, 4);
    expect(box.w).toBeCloseTo(98.4, 4);
    expect(box.h).toBeCloseTo(21.6, 4);
  });

  it('measures from the top of the page, the origin of the screen', () => {
    expect(boxOnScreen({ left: 0, bottom: 490, right: 10, top: 500 }, page, page)).toEqual({ x: 0, y: 0, w: 10, h: 10 });
    expect(boxOnScreen({ left: 590, bottom: 0, right: 600, top: 10 }, page, page)).toEqual({ x: 590, y: 490, w: 10, h: 10 });
  });
});

describe('regionOnScreen', () => {
  it('takes the fractions of the page as it is rendered', () => {
    expect(regionOnScreen({ page: 1, left: 0.1, top: 0.1309, width: 0.2186, height: 0.0291 }, { width: 900, height: 750 })).toEqual({
      x: 90,
      y: 0.1309 * 750,
      w: 0.2186 * 900,
      h: 0.0291 * 750,
    });
  });
});

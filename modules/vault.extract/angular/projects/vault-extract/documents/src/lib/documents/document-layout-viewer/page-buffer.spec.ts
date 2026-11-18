import { describe, expect, it } from 'vitest';

import { PageBuffer, samePages } from './page-buffer';

const pages = (set: ReadonlySet<number>) => [...set].sort((a, b) => a - b);

describe('PageBuffer', () => {
  it('keeps the pages in view drawn', () => {
    const buffer = new PageBuffer(3);

    expect(pages(buffer.update([{ page: 1, inView: true }, { page: 2, inView: true }]))).toEqual([1, 2]);
  });

  it('keeps pages that left the view up to its size, the most recently seen first', () => {
    const buffer = new PageBuffer(3);
    for (let page = 1; page <= 6; page++) {
      buffer.update([{ page, inView: true }]);
      buffer.update([{ page, inView: false }]);
    }

    expect(pages(buffer.update([]))).toEqual([4, 5, 6]);
  });

  it('never lets a page in view go, however many there are', () => {
    const buffer = new PageBuffer(2);

    const kept = buffer.update([1, 2, 3, 4].map(page => ({ page, inView: true })));

    expect(pages(kept)).toEqual([1, 2, 3, 4]);
  });

  it('counts a page seen again as the most recent', () => {
    const buffer = new PageBuffer(2);
    for (const page of [1, 2, 3]) {
      buffer.update([{ page, inView: true }, { page, inView: false }]);
    }
    buffer.update([{ page: 2, inView: true }, { page: 2, inView: false }]);
    buffer.update([{ page: 4, inView: true }, { page: 4, inView: false }]);

    expect(pages(buffer.update([]))).toEqual([2, 4]);
  });

  it('bounds what a long document keeps drawn', () => {
    const buffer = new PageBuffer();
    let kept: ReadonlySet<number> = new Set();
    for (let page = 1; page <= 300; page++) {
      kept = buffer.update([{ page, inView: true }, { page: page - 1, inView: false }]);
    }

    expect(kept.size).toBe(10);
    expect(kept.has(300)).toBe(true);
  });
});

describe('samePages', () => {
  it('compares the pages, not the order', () => {
    expect(samePages(new Set([1, 2]), new Set([2, 1]))).toBe(true);
    expect(samePages(new Set([1, 2]), new Set([1, 3]))).toBe(false);
    expect(samePages(new Set([1]), new Set([1, 2]))).toBe(false);
  });
});

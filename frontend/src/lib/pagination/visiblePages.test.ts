import { describe, expect, it } from 'vitest';
import { countOrderListPages, listVisiblePages } from './visiblePages';

describe('listVisiblePages', () => {
  it.each([
    [1, 1, [1]],
    [3, 1, [1, 2, 3]],
    [3, 2, [1, 2, 3]],
    [3, 3, [1, 2, 3]],
    [7, 1, [1, 2, 3, 4, 5, 6, 7]],
    [7, 4, [1, 2, 3, 4, 5, 6, 7]],
    [7, 7, [1, 2, 3, 4, 5, 6, 7]],
    [8, 1, [1, 2, '…', 8]],
    [8, 2, [1, 2, 3, '…', 8]],
    [8, 3, [1, 2, 3, 4, '…', 8]],
    [8, 4, [1, '…', 3, 4, 5, '…', 8]],
    [8, 7, [1, '…', 6, 7, 8]],
    [8, 8, [1, '…', 7, 8]],
    [40, 1, [1, 2, '…', 40]],
    [40, 5, [1, '…', 4, 5, 6, '…', 40]],
    [40, 20, [1, '…', 19, 20, 21, '…', 40]],
    [40, 39, [1, '…', 38, 39, 40]],
    [40, 40, [1, '…', 39, 40]],
  ])('with %i pages and the current one at %i shows %j', (totalPages, currentPage, expectedItems) => {
    expect(listVisiblePages(currentPage, totalPages)).toEqual(expectedItems);
  });

  it('shows nothing without pages', () => {
    expect(listVisiblePages(1, 0)).toEqual([]);
  });

  it('never goes past 7 items, so the row fits the card', () => {
    const longestRowIn1000Pages = Math.max(
      ...Array.from({ length: 1000 }, (_, pageIndex) => listVisiblePages(pageIndex + 1, 1000).length),
    );
    expect(longestRowIn1000Pages).toBe(7);
  });

  it('brings the current page into range when it is past the last one', () => {
    expect(listVisiblePages(50, 40)).toEqual([1, '…', 39, 40]);
  });

  it('with 1,000 pages on the last one shows 1000 as the end', () => {
    expect(listVisiblePages(1000, 1000)).toEqual([1, '…', 999, 1000]);
  });
});

describe('countOrderListPages', () => {
  it.each([
    [0, 0],
    [1, 1],
    [10, 1],
    [11, 2],
    [23, 3],
    [9_999, 1000],
    [10_000, 1000],
    [10_001, 1000],
    [25_000, 1000],
  ])('with %i orders gives %i pages', (totalOrders, expectedPages) => {
    expect(countOrderListPages(totalOrders)).toBe(expectedPages);
  });
});

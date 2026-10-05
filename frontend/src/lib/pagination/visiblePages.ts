export const ORDERS_PER_PAGE = 10;
// The server refuses pages above 1,000; the screen never offers beyond it.
export const LAST_ALLOWED_PAGE = 1000;
const PAGES_SHOWN_WITHOUT_SHORTENING = 7;

export const PAGINATION_ELLIPSIS = '…';
export type PaginationItem = number | typeof PAGINATION_ELLIPSIS;

export function countOrderListPages(totalOrders: number): number {
  return Math.min(Math.ceil(Math.max(totalOrders, 0) / ORDERS_PER_PAGE), LAST_ALLOWED_PAGE);
}

// Always the first, the last, the current one and its neighbours; each missing stretch becomes an "…".
export function listVisiblePages(currentPage: number, totalPages: number): PaginationItem[] {
  if (totalPages <= 0) return [];
  if (totalPages <= PAGES_SHOWN_WITHOUT_SHORTENING) {
    return Array.from({ length: totalPages }, (_, pageIndex) => pageIndex + 1);
  }

  const currentPageInRange = Math.min(Math.max(currentPage, 1), totalPages);
  const alwaysShownPages = new Set([1, currentPageInRange - 1, currentPageInRange, currentPageInRange + 1, totalPages]);
  const shownPagesInOrder = [...alwaysShownPages]
    .filter((shownPage) => shownPage >= 1 && shownPage <= totalPages)
    .sort((smallerPage, largerPage) => smallerPage - largerPage);

  const paginationItems: PaginationItem[] = [];
  shownPagesInOrder.forEach((shownPage, pageIndex) => {
    const previousShownPage = shownPagesInOrder[pageIndex - 1];
    if (previousShownPage !== undefined && shownPage - previousShownPage > 1) paginationItems.push(PAGINATION_ELLIPSIS);
    paginationItems.push(shownPage);
  });
  return paginationItems;
}

import '../styles/pagination.css';
import { countOrderListPages, listVisiblePages, ORDERS_PER_PAGE, PAGINATION_ELLIPSIS } from '../lib/pagination/visiblePages';
import { formatBrazilianWholeNumber } from '../lib/number-format/brazilianNumberFormat';

type OrderListPaginationProps = {
  currentPage: number;
  totalOrders: number;
  ordersOnPage: number;
  pageBeingLoaded?: number;
  onChoosePage: (chosenPage: number) => void;
};

export function OrderListPagination({ currentPage, totalOrders, ordersOnPage, pageBeingLoaded, onChoosePage }: OrderListPaginationProps) {
  const totalPages = countOrderListPages(totalOrders);
  if (totalOrders <= ORDERS_PER_PAGE) return null;

  const firstOrderOnPage = (currentPage - 1) * ORDERS_PER_PAGE + 1;
  const lastOrderOnPage = firstOrderOnPage + ordersOnPage - 1;
  const paginationItems = listVisiblePages(currentPage, totalPages);
  const isLoadingRequestedPage = pageBeingLoaded !== undefined;

  return (
    <div className="pagination" role="group" aria-label="Páginas da lista de ordens" aria-busy={isLoadingRequestedPage}>
      <p className="pagination-summary numeric" data-testid="resumo-da-paginacao">
        {isLoadingRequestedPage ? (
          <>Carregando a página {formatBrazilianWholeNumber(pageBeingLoaded)}…</>
        ) : (
          <>
            Mostrando {formatBrazilianWholeNumber(firstOrderOnPage)}–{formatBrazilianWholeNumber(lastOrderOnPage)} de{' '}
            {formatBrazilianWholeNumber(totalOrders)} ordens
          </>
        )}
      </p>
      <ul className="pagination-buttons">
        <li>
          <button
            type="button"
            className="pagination-button"
            aria-label="Página anterior"
            disabled={currentPage <= 1}
            onClick={() => onChoosePage(currentPage - 1)}
          >
            ‹
          </button>
        </li>
        {paginationItems.map((paginationItem, positionInRow) =>
          paginationItem === PAGINATION_ELLIPSIS ? (
            <li key={`ellipsis-${positionInRow}`} className="pagination-ellipsis" data-testid="reticencias-da-paginacao">
              {PAGINATION_ELLIPSIS}
            </li>
          ) : (
            <li key={paginationItem}>
              <button
                type="button"
                className="pagination-button numeric"
                aria-label={`Página ${paginationItem}`}
                aria-current={paginationItem === currentPage ? 'page' : undefined}
                data-loading={paginationItem === pageBeingLoaded && paginationItem !== currentPage ? 'true' : undefined}
                onClick={() => onChoosePage(paginationItem)}
              >
                {paginationItem}
              </button>
            </li>
          ),
        )}
        <li>
          <button
            type="button"
            className="pagination-button"
            aria-label="Próxima página"
            disabled={currentPage >= totalPages}
            onClick={() => onChoosePage(currentPage + 1)}
          >
            ›
          </button>
        </li>
      </ul>
    </div>
  );
}

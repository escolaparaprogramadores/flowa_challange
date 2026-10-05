import { useCallback, useEffect, useRef, useState } from 'react';
import { countOrderListPages } from '../lib/pagination/visiblePages';
import {
  deleteAllOrders,
  fetchExposures,
  fetchOrderListPage,
  sendOrder,
  type OrderListPage,
  type OrderSendResult,
  type OrderToSend,
  type SymbolExposure,
} from '../services/ordersService';

export type ExposuresState =
  | { status: 'loading' }
  | { status: 'error'; errorMessage: string }
  | { status: 'ready'; symbolExposures: SymbolExposure[] };

export type OrderListState =
  | { status: 'loading' }
  | { status: 'error'; errorMessage: string }
  | { status: 'ready'; orderListPage: OrderListPage };

export type OrderSendFailure = Extract<OrderSendResult, { outcome: 'invalid' | 'communication-failure' }>;

// Another tab may have deleted orders: a page beyond the last one comes back empty with the real total,
// and then the screen asks for the last page that still exists instead of showing the list as empty.
async function readOrderListPage(requestedPage: number): Promise<OrderListState> {
  try {
    const orderListPage = await fetchOrderListPage(requestedPage);
    const lastPageWithOrders = countOrderListPages(orderListPage.totalOrders);
    if (orderListPage.orders.length === 0 && requestedPage > lastPageWithOrders && lastPageWithOrders >= 1) {
      return await readOrderListPage(lastPageWithOrders);
    }
    return { status: 'ready', orderListPage };
  } catch (readFailure) {
    return { status: 'error', errorMessage: (readFailure as Error).message };
  }
}

export function useOrdersAndExposures() {
  const [exposuresState, setExposuresState] = useState<ExposuresState>({ status: 'loading' });
  const [orderListState, setOrderListState] = useState<OrderListState>({ status: 'loading' });
  const [isSendingOrder, setIsSendingOrder] = useState(false);
  const [lastSendFailure, setLastSendFailure] = useState<OrderSendFailure>();
  const [orderListPageBeingLoaded, setOrderListPageBeingLoaded] = useState<number>();
  const latestExposuresReadNumber = useRef(0);
  const latestOrderListReadNumber = useRef(0);

  // Two reads may be open at the same time; only the latest requested one may change the panel,
  // otherwise an old and slow answer would erase the exposure already refreshed after a send.
  const refreshExposures = useCallback(async () => {
    const thisReadNumber = ++latestExposuresReadNumber.current;
    const readExposuresState: ExposuresState = await fetchExposures().then(
      (symbolExposures) => ({ status: 'ready', symbolExposures }),
      (readFailure: Error) => ({ status: 'error', errorMessage: readFailure.message }),
    );
    if (thisReadNumber === latestExposuresReadNumber.current) setExposuresState(readExposuresState);
  }, []);

  // Same guard as the exposure: the list read before the send cannot cover the one read after it.
  // It also holds between quick clicks on the pagination: only the last requested page shows up.
  const loadOrderListPage = useCallback(async (requestedPage: number) => {
    const thisReadNumber = ++latestOrderListReadNumber.current;
    setOrderListPageBeingLoaded(requestedPage);
    const readOrderListState = await readOrderListPage(requestedPage);
    if (thisReadNumber !== latestOrderListReadNumber.current) return;
    setOrderListState(readOrderListState);
    setOrderListPageBeingLoaded(undefined);
  }, []);

  useEffect(() => {
    void refreshExposures();
    void loadOrderListPage(1);
  }, [refreshExposures, loadOrderListPage]);

  async function sendOrderAndRefresh(orderToSend: OrderToSend) {
    setIsSendingOrder(true);
    setLastSendFailure(undefined);
    try {
      const orderSendResult = await sendOrder(orderToSend);
      if (orderSendResult.outcome === 'invalid' || orderSendResult.outcome === 'communication-failure') setLastSendFailure(orderSendResult);
    } finally {
      setIsSendingOrder(false);
    }
    // Exposure and list always come from the server: the screen never sums or builds a row on its own.
    // The new order is the most recent one, so the list goes back to page 1, where it shows at the top.
    await Promise.all([refreshExposures(), loadOrderListPage(1)]);
  }

  async function deleteAllOrdersAndRefresh() {
    const orderListPageOnScreen = orderListState.status === 'ready' ? orderListState.orderListPage.page : 1;
    try {
      await deleteAllOrders();
    } catch (deleteFailure) {
      // The OrderGenerator answers 503 after 5 s, but the OrderAccumulator finishes deleting anyway:
      // the screen reads the server again before showing the error, so it never shows orders that are already gone.
      await Promise.all([refreshExposures(), loadOrderListPage(orderListPageOnScreen)]);
      throw deleteFailure;
    }
    await Promise.all([refreshExposures(), loadOrderListPage(1)]);
  }

  return {
    exposuresState,
    orderListState,
    isSendingOrder,
    lastSendFailure,
    orderListPageBeingLoaded,
    loadOrderListPage,
    sendOrderAndRefresh,
    deleteAllOrdersAndRefresh,
  };
}

import type { AttemptedOrder, OrderListPage } from './ordersService';

// Ceiling of the reads after an unconfirmed send (CA-27 and the architecture review): 3 reads, one every 2 s, then nothing.
// The first one is the read every send already makes; the cycle adds the other two.
export const RECHECK_INTERVAL_IN_MS = 2_000;
export const MAX_READS_AFTER_UNCONFIRMED_SEND = 3;

// The order "appeared" when the server counts more orders than before the send and the newest one, at the top of
// page 1, has the same asset and side. Without the count from before the send (list loading or failed), nothing
// proves it appeared, so the cycle keeps its reads up to the ceiling.
export function isSentOrderOnFirstPage(
  firstOrderListPage: OrderListPage | undefined,
  attemptedOrder: AttemptedOrder,
  totalOrdersBeforeSend: number | undefined,
) {
  if (!firstOrderListPage || totalOrdersBeforeSend === undefined || firstOrderListPage.totalOrders <= totalOrdersBeforeSend) return false;
  const newestOrder = firstOrderListPage.orders[0];
  return (
    newestOrder !== undefined &&
    newestOrder.side === attemptedOrder.side &&
    newestOrder.symbol?.trim().toUpperCase() === attemptedOrder.symbol.trim().toUpperCase()
  );
}

// One timer at a time, chained after each read: a failed read counts as a round, and after the last one no timer is left.
export function startSentOrderRecheckCycle(recheckWhetherSentOrderAppeared: () => Promise<boolean>) {
  let isCycleCancelled = false;
  let nextRecheckTimer: ReturnType<typeof setTimeout> | undefined;

  function scheduleRecheck(readNumber: number) {
    nextRecheckTimer = setTimeout(async () => {
      const hasSentOrderAppeared = await recheckWhetherSentOrderAppeared();
      if (!isCycleCancelled && !hasSentOrderAppeared && readNumber < MAX_READS_AFTER_UNCONFIRMED_SEND) {
        scheduleRecheck(readNumber + 1);
      }
    }, RECHECK_INTERVAL_IN_MS);
  }

  scheduleRecheck(2);
  return function cancelSentOrderRecheckCycle() {
    isCycleCancelled = true;
    clearTimeout(nextRecheckTimer);
  };
}

import type { AttemptedOrder, OrderListPage } from './ordersService';

// Ceiling of the reads after an unconfirmed send (CA-27 and the architecture review): 3 reads, one every 2 s, then nothing.
// The first one is the read every send already makes; the cycle adds the other two.
export const RECHECK_INTERVAL_IN_MS = 2_000;
export const MAX_READS_AFTER_UNCONFIRMED_SEND = 3;

// The order "appeared" when page 1 has an order the screen had not seen before the send, with the same asset and side.
export function isSentOrderOnListPage(
  orderListPage: OrderListPage | undefined,
  attemptedOrder: AttemptedOrder,
  clOrdIdsBeforeSend: ReadonlySet<string>,
) {
  return (orderListPage?.orders ?? []).some(
    (listedOrder) =>
      !clOrdIdsBeforeSend.has(listedOrder.clOrdId) && listedOrder.symbol === attemptedOrder.symbol && listedOrder.side === attemptedOrder.side,
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

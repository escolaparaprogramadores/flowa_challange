import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AttemptedOrder, ListedOrder, OrderListPage } from './ordersService';
import {
  MAX_READS_AFTER_UNCONFIRMED_SEND,
  RECHECK_INTERVAL_IN_MS,
  isSentOrderOnFirstPage,
  readTrustedTotalOrdersBeforeSend,
  startSentOrderRecheckCycle,
} from './sentOrderRecheckCycle';

const sentPetr4Buy: AttemptedOrder = { symbol: 'PETR4', side: 'buy', quantity: 100, priceInReais: 10 };

function buildListedOrder(clOrdId: string, symbol: string, side: 'buy' | 'sell'): ListedOrder {
  return { receivedAt: '2026-10-06T12:00:00Z', outcome: 'accepted', symbol, side, quantity: 100, priceInReais: 10, orderId: `order-${clOrdId}`, clOrdId };
}


afterEach(() => {
  vi.useRealTimers();
});

describe('isSentOrderOnFirstPage', () => {
  const ordersOnServerBeforeSend = 25;

  function buildFirstPageWithTotal(totalOrders: number, listedOrders: ListedOrder[]): OrderListPage {
    return { page: 1, totalOrders, orders: listedOrders };
  }

  it('RF-11: one more order on the server, with the same asset and side at the top of page 1, is the sent order', () => {
    const firstPage = buildFirstPageWithTotal(26, [buildListedOrder('new-1', 'PETR4', 'buy'), buildListedOrder('old-1', 'PETR4', 'buy')]);
    expect(isSentOrderOnFirstPage(firstPage, sentPetr4Buy, ordersOnServerBeforeSend)).toBe(true);
  });

  it('RF-11: the same total as before the send means the order has not entered yet, even with a PETR4 buy at the top', () => {
    const firstPage = buildFirstPageWithTotal(25, [buildListedOrder('old-1', 'PETR4', 'buy')]);
    expect(isSentOrderOnFirstPage(firstPage, sentPetr4Buy, ordersOnServerBeforeSend)).toBe(false);
  });

  it('RF-11: an old PETR4 buy below a newer order of another asset or side does not count', () => {
    const otherAssetOnTop = buildFirstPageWithTotal(26, [buildListedOrder('new-1', 'VALE3', 'buy'), buildListedOrder('old-1', 'PETR4', 'buy')]);
    const otherSideOnTop = buildFirstPageWithTotal(26, [buildListedOrder('new-2', 'PETR4', 'sell'), buildListedOrder('old-1', 'PETR4', 'buy')]);
    expect(isSentOrderOnFirstPage(otherAssetOnTop, sentPetr4Buy, ordersOnServerBeforeSend)).toBe(false);
    expect(isSentOrderOnFirstPage(otherSideOnTop, sentPetr4Buy, ordersOnServerBeforeSend)).toBe(false);
  });

  it('RF-11: without a trusted count from before the send (list loading, failed or still being read), nothing counts as found', () => {
    const firstPage = buildFirstPageWithTotal(26, [buildListedOrder('new-1', 'PETR4', 'buy')]);
    expect(isSentOrderOnFirstPage(firstPage, sentPetr4Buy, undefined)).toBe(false);
  });

  it('RF-11: in the test mode the typed asset matches the stored one without case or spaces', () => {
    const firstPage = buildFirstPageWithTotal(26, [buildListedOrder('new-1', 'ITUB4', 'buy')]);
    expect(isSentOrderOnFirstPage(firstPage, { ...sentPetr4Buy, symbol: ' itub4 ' }, ordersOnServerBeforeSend)).toBe(true);
  });

  it('RF-12: a failed read (no page) does not count as found', () => {
    expect(isSentOrderOnFirstPage(undefined, sentPetr4Buy, ordersOnServerBeforeSend)).toBe(false);
  });
});

describe('readTrustedTotalOrdersBeforeSend', () => {
  it('RF-11: a settled list on screen gives its count, on any page', () => {
    expect(readTrustedTotalOrdersBeforeSend(25, false, false)).toBe(25);
  });

  it('F-01 regression: a list read still running (the read of the previous send) makes the count untrusted', () => {
    expect(readTrustedTotalOrdersBeforeSend(25, true, false)).toBeUndefined();
  });

  it('F-01 regression: an earlier unconfirmed order that may still enter makes the count untrusted', () => {
    expect(readTrustedTotalOrdersBeforeSend(25, false, true)).toBeUndefined();
  });

  it('RF-11: no list on screen gives no count', () => {
    expect(readTrustedTotalOrdersBeforeSend(undefined, false, false)).toBeUndefined();
  });
});

describe('startSentOrderRecheckCycle', () => {
  it('RNF-01: the ceiling is 3 reads after the warning, one every 2 s', () => {
    expect(MAX_READS_AFTER_UNCONFIRMED_SEND).toBe(3);
    expect(RECHECK_INTERVAL_IN_MS).toBe(2_000);
  });

  it('CA-27: after the read of the send itself, reads once every 2 s, 2 more times (3 in all), and leaves no timer behind', async () => {
    vi.useFakeTimers();
    const recheckWhetherSentOrderAppeared = vi.fn(async () => false);
    startSentOrderRecheckCycle(recheckWhetherSentOrderAppeared);
    await vi.advanceTimersByTimeAsync(1_999);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(0);
    await vi.advanceTimersByTimeAsync(1);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1_999);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(2);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(MAX_READS_AFTER_UNCONFIRMED_SEND - 1);
  });

  it('CA-27: stops as soon as the order appears', async () => {
    vi.useFakeTimers();
    const recheckWhetherSentOrderAppeared = vi.fn(async () => true);
    startSentOrderRecheckCycle(recheckWhetherSentOrderAppeared);
    await vi.advanceTimersByTimeAsync(2_000);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
  });

  it('RF-12: cancelling (new send or leaving the screen) stops the cycle before the next read', async () => {
    vi.useFakeTimers();
    const recheckWhetherSentOrderAppeared = vi.fn(async () => false);
    const cancelSentOrderRecheckCycle = startSentOrderRecheckCycle(recheckWhetherSentOrderAppeared);
    await vi.advanceTimersByTimeAsync(2_000);
    cancelSentOrderRecheckCycle();
    expect(vi.getTimerCount()).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
  });

  it('RF-12: a cycle cancelled while a read is running does not schedule another one', async () => {
    vi.useFakeTimers();
    let finishRunningRead: (hasSentOrderAppeared: boolean) => void = () => {};
    const recheckWhetherSentOrderAppeared = vi.fn(() => new Promise<boolean>((resolveRead) => { finishRunningRead = resolveRead; }));
    const cancelSentOrderRecheckCycle = startSentOrderRecheckCycle(recheckWhetherSentOrderAppeared);
    await vi.advanceTimersByTimeAsync(2_000);
    cancelSentOrderRecheckCycle();
    finishRunningRead(false);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(recheckWhetherSentOrderAppeared).toHaveBeenCalledTimes(1);
    expect(vi.getTimerCount()).toBe(0);
  });
});

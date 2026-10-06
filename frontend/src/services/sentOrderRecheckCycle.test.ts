import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AttemptedOrder, ListedOrder, OrderListPage } from './ordersService';
import {
  MAX_READS_AFTER_UNCONFIRMED_SEND,
  RECHECK_INTERVAL_IN_MS,
  isSentOrderOnListPage,
  startSentOrderRecheckCycle,
} from './sentOrderRecheckCycle';

const sentPetr4Buy: AttemptedOrder = { symbol: 'PETR4', side: 'buy', quantity: 100, priceInReais: 10 };

function buildListedOrder(clOrdId: string, symbol: string, side: 'buy' | 'sell'): ListedOrder {
  return { receivedAt: '2026-10-06T12:00:00Z', outcome: 'accepted', symbol, side, quantity: 100, priceInReais: 10, orderId: `order-${clOrdId}`, clOrdId };
}

function buildFirstPage(listedOrders: ListedOrder[]): OrderListPage {
  return { page: 1, totalOrders: listedOrders.length, orders: listedOrders };
}

afterEach(() => {
  vi.useRealTimers();
});

describe('isSentOrderOnListPage', () => {
  const clOrdIdsBeforeSend = new Set(['old-1']);

  it('RF-11: a new order with the same asset and side on page 1 is the sent order', () => {
    const firstPage = buildFirstPage([buildListedOrder('new-1', 'PETR4', 'buy'), buildListedOrder('old-1', 'PETR4', 'buy')]);
    expect(isSentOrderOnListPage(firstPage, sentPetr4Buy, clOrdIdsBeforeSend)).toBe(true);
  });

  it('RF-11: an order the screen already showed before the send does not count', () => {
    expect(isSentOrderOnListPage(buildFirstPage([buildListedOrder('old-1', 'PETR4', 'buy')]), sentPetr4Buy, clOrdIdsBeforeSend)).toBe(false);
  });

  it('RF-11: a new order of another asset or another side does not count', () => {
    const firstPage = buildFirstPage([buildListedOrder('new-1', 'VALE3', 'buy'), buildListedOrder('new-2', 'PETR4', 'sell')]);
    expect(isSentOrderOnListPage(firstPage, sentPetr4Buy, clOrdIdsBeforeSend)).toBe(false);
  });

  it('RF-12: a failed read (no page) does not count as found', () => {
    expect(isSentOrderOnListPage(undefined, sentPetr4Buy, clOrdIdsBeforeSend)).toBe(false);
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

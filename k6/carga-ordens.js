// Flowa load test with k6 OSS: k6 run -e FLOWA_URL=https://<api> k6/carga-ordens.js
// It sends accepted orders, orders rejected for an invalid field and orders rejected for crossing the
// exposure limit on every symbol, and leaves each symbol's exposure where it was before the run.
import http from 'k6/http';
import execution from 'k6/execution';
import { check } from 'k6';
import { Counter, Gauge, Trend } from 'k6/metrics';

const API_URL = (__ENV.FLOWA_URL || '').replace(/\/+$/, '');
if (!/^https?:\/\/[^\s/]+$/.test(API_URL)) {
  throw new Error('Set FLOWA_URL to the API origin, for example -e FLOWA_URL=https://example.com');
}

// Fixed ceilings. FLOWA_DURATION_SECONDS can only shorten the run, never past 5 minutes.
// About 15 orders per second in total: 7 pairs (14 orders) plus 1 invalid order per second.
const BALANCED_PAIRS_PER_SECOND = 7;
const MAX_DURATION_SECONDS = 300;
const testDurationSeconds = Math.min(
  MAX_DURATION_SECONDS,
  Math.max(1, parseInt(__ENV.FLOWA_DURATION_SECONDS || `${MAX_DURATION_SECONDS}`, 10) || MAX_DURATION_SECONDS),
);

const SUMMARY_DIR = (__ENV.FLOWA_SUMMARY_DIR || '.').replace(/[\\/]+$/, '');
const FLOWA_SYMBOLS = ['PETR4', 'VALE3', 'VIIA4'];
const PAIR_ORDER_QUANTITY = 1;
const PAIR_ORDER_PRICE = 1.0;

// Quantity 100000 fits in a FIX message, so the API forwards it and the OrderAccumulator rejects it.
const INVALID_ORDER_QUANTITY = 100000;
// Largest valid order: 99999 x 999.99 = 99,998,000.01. From zero exposure one of these is accepted and
// the next one on the same side crosses the 100,000,000.00 limit.
const LIMIT_FILL_ORDER_QUANTITY = 99999;
const LIMIT_FILL_ORDER_PRICE = 999.99;
const MAX_LIMIT_FILL_ATTEMPTS = 3;
const LIMIT_OVERFLOW_ROUNDS = 2;

// pt-BR reasons the API returns in the ExecutionReport text (tag 58); compared as data.
const INVALID_QUANTITY_REJECTION_REASON = 'A quantidade deve ser menor que 100.000.';
const limitRejectionReasonFor = (symbol) => `Ordem rejeitada: a exposição de ${symbol} passaria do limite de 100.000.000,00.`;

const ORDER_OUTCOME_KINDS = ['accepted', 'rejected_invalid_field', 'rejected_limit'];
const metricNameFor = (prefix, symbol) => `${prefix}_${symbol.toLowerCase()}`;

const orderCountersBySymbolAndKind = Object.fromEntries(FLOWA_SYMBOLS.map((symbol) => [
  symbol,
  Object.fromEntries(ORDER_OUTCOME_KINDS.map((outcomeKind) => [outcomeKind, new Counter(metricNameFor(`orders_${outcomeKind}`, symbol))])),
]));
const exposureAfterBySymbol = Object.fromEntries(FLOWA_SYMBOLS.map((symbol) => [symbol, new Gauge(metricNameFor('exposure_after', symbol))]));
const exposureDeltaCentsBySymbol = Object.fromEntries(FLOWA_SYMBOLS.map((symbol) => [symbol, new Gauge(metricNameFor('exposure_delta_cents', symbol))]));
const orderLatency = new Trend('order_latency', true);
const ordersSent = new Counter('orders_sent');
const unexpectedOrderOutcomes = new Counter('orders_unexpected_outcome');
const unbalancedOrders = new Counter('orders_left_unbalanced');
const restoredExposureSymbols = new Counter('exposure_restored_symbols');

const orderCountThresholds = Object.fromEntries(FLOWA_SYMBOLS.flatMap((symbol) =>
  ORDER_OUTCOME_KINDS.map((outcomeKind) => [metricNameFor(`orders_${outcomeKind}`, symbol), ['count>0']])));

export const options = {
  scenarios: {
    balanced_pairs: {
      executor: 'constant-arrival-rate',
      exec: 'sendBalancedPair',
      rate: BALANCED_PAIRS_PER_SECOND,
      timeUnit: '1s',
      duration: `${testDurationSeconds}s`,
      preAllocatedVUs: 10,
      maxVUs: 30,
      // Two sequential orders of up to 15 s each: the closing leg must not be cut off.
      gracefulStop: '35s',
    },
    invalid_field_orders: {
      executor: 'constant-arrival-rate',
      exec: 'sendInvalidFieldOrders',
      rate: 1,
      timeUnit: '3s',
      duration: `${testDurationSeconds}s`,
      preAllocatedVUs: 2,
      maxVUs: 5,
      gracefulStop: '50s',
    },
    limit_overflow: {
      executor: 'per-vu-iterations',
      exec: 'sendLimitOverflowOrders',
      vus: 1,
      iterations: LIMIT_OVERFLOW_ROUNDS,
      maxDuration: '3m',
      gracefulStop: '2m',
    },
  },
  // The run fails on HTTP errors (400, the API's 429, 5xx), on a missing outcome kind for any symbol
  // and on any symbol whose exposure did not return to its value before the run.
  thresholds: {
    http_req_failed: ['rate<0.01'],
    orders_unexpected_outcome: ['count==0'],
    exposure_restored_symbols: [`count==${FLOWA_SYMBOLS.length}`],
    ...orderCountThresholds,
  },
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(80)', 'p(90)', 'p(95)', 'p(99)'],
};

function readExposureBySymbol(requestName) {
  const exposuresResponse = http.get(`${API_URL}/api/exposures`, { tags: { name: requestName }, timeout: '15s' });
  if (exposuresResponse.status !== 200) {
    throw new Error(`GET /api/exposures answered ${exposuresResponse.status}`);
  }
  return Object.fromEntries(exposuresResponse.json('data.exposures')
    .map((symbolExposure) => [symbolExposure.symbol, symbolExposure.exposure]));
}

function buildPairOrder(symbol, side) {
  return { symbol, side, quantity: PAIR_ORDER_QUANTITY, price: PAIR_ORDER_PRICE };
}

function buildInvalidQuantityOrder(symbol) {
  return { symbol, side: 'buy', quantity: INVALID_ORDER_QUANTITY, price: PAIR_ORDER_PRICE };
}

function buildLimitFillOrder(symbol, side) {
  return { symbol, side, quantity: LIMIT_FILL_ORDER_QUANTITY, price: LIMIT_FILL_ORDER_PRICE };
}

function oppositeSideOf(side) {
  return side === 'buy' ? 'sell' : 'buy';
}

// The order status comes in data; the rejection reason (tag 58) is the envelope message.
function postOrder(order) {
  const orderResponse = http.post(
    `${API_URL}/api/orders`,
    JSON.stringify(order),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'order' }, timeout: '15s' },
  );
  ordersSent.add(1);
  orderLatency.add(orderResponse.timings.duration);
  check(orderResponse, { 'order answered with 200': (answeredOrder) => answeredOrder.status === 200 });
  return orderResponse.status === 200
    ? { status: orderResponse.json('data.status'), message: orderResponse.json('message') }
    : { status: null, message: null };
}

function classifyOrderOutcome(order, orderOutcome) {
  if (orderOutcome.status === 'accepted') return 'accepted';
  if (orderOutcome.status === 'rejected' && orderOutcome.message === INVALID_QUANTITY_REJECTION_REASON) return 'rejected_invalid_field';
  if (orderOutcome.status === 'rejected' && orderOutcome.message === limitRejectionReasonFor(order.symbol)) return 'rejected_limit';
  return 'unclassified';
}

function countOrderOutcome(symbol, outcomeKind) {
  if (ORDER_OUTCOME_KINDS.includes(outcomeKind)) orderCountersBySymbolAndKind[symbol][outcomeKind].add(1);
}

function sendOrderAndCountOutcome(order) {
  const outcomeKind = classifyOrderOutcome(order, postOrder(order));
  countOrderOutcome(order.symbol, outcomeKind);
  return outcomeKind;
}

export function setup() {
  const versionResponse = http.get(`${API_URL}/version`, { tags: { name: 'version' } });
  if (versionResponse.status !== 200) {
    throw new Error(`API is down: /version answered ${versionResponse.status}`);
  }
  const exposureBeforeBySymbol = readExposureBySymbol('exposure_before');

  // Every threshold metric gets a zero sample, so a kind that never happens fails instead of being skipped.
  for (const symbol of FLOWA_SYMBOLS) {
    for (const outcomeKind of ORDER_OUTCOME_KINDS) orderCountersBySymbolAndKind[symbol][outcomeKind].add(0);
  }
  unexpectedOrderOutcomes.add(0);
  restoredExposureSymbols.add(0);

  // The first leg of a pair goes to the side that moves the exposure toward zero, so a symbol that is
  // already close to the limit is not pushed against it.
  const pairFirstSideBySymbol = Object.fromEntries(FLOWA_SYMBOLS.map((symbol) =>
    [symbol, exposureBeforeBySymbol[symbol] > 0 ? 'sell' : 'buy']));
  return { runningCommit: versionResponse.json('commit'), exposureBeforeBySymbol, pairFirstSideBySymbol };
}

// Both legs in the same iteration: the closing leg only goes out if the opening one was accepted.
export function sendBalancedPair(loadContext) {
  const pairSymbol = FLOWA_SYMBOLS[execution.scenario.iterationInTest % FLOWA_SYMBOLS.length];
  const openingSide = loadContext.pairFirstSideBySymbol[pairSymbol];

  if (sendOrderAndCountOutcome(buildPairOrder(pairSymbol, openingSide)) !== 'accepted') {
    unexpectedOrderOutcomes.add(1);
    return;
  }
  if (sendOrderAndCountOutcome(buildPairOrder(pairSymbol, oppositeSideOf(openingSide))) !== 'accepted') {
    unexpectedOrderOutcomes.add(1);
    unbalancedOrders.add(1);
  }
}

export function sendInvalidFieldOrders() {
  for (const symbol of FLOWA_SYMBOLS) {
    if (sendOrderAndCountOutcome(buildInvalidQuantityOrder(symbol)) !== 'rejected_invalid_field') unexpectedOrderOutcomes.add(1);
  }
}

// Per symbol: fill on the side that moves the exposure away from zero until one order crosses the
// limit, then send one opposite order for every accepted fill order.
export function sendLimitOverflowOrders() {
  const currentExposureBySymbol = readExposureBySymbol('exposure_before_limit_round');
  for (const symbol of FLOWA_SYMBOLS) {
    const fillSide = currentExposureBySymbol[symbol] >= 0 ? 'buy' : 'sell';
    let acceptedFillOrders = 0;
    let hasCrossedLimit = false;

    for (let fillAttempt = 0; fillAttempt < MAX_LIMIT_FILL_ATTEMPTS && !hasCrossedLimit; fillAttempt += 1) {
      const fillOutcomeKind = sendOrderAndCountOutcome(buildLimitFillOrder(symbol, fillSide));
      if (fillOutcomeKind === 'accepted') {
        acceptedFillOrders += 1;
      } else if (fillOutcomeKind === 'rejected_limit') {
        hasCrossedLimit = true;
      } else {
        break;
      }
    }
    if (!hasCrossedLimit) unexpectedOrderOutcomes.add(1);

    for (let unwindIndex = 0; unwindIndex < acceptedFillOrders; unwindIndex += 1) {
      if (sendOrderAndCountOutcome(buildLimitFillOrder(symbol, oppositeSideOf(fillSide))) !== 'accepted') {
        unexpectedOrderOutcomes.add(1);
        unbalancedOrders.add(1);
      }
    }
  }
}

export function teardown(loadContext) {
  const exposureAfterRun = readExposureBySymbol('exposure_after');
  for (const symbol of FLOWA_SYMBOLS) {
    // Exposures are money with two decimals; compare in cents to avoid floating point noise.
    const exposureDeltaCents = Math.round(exposureAfterRun[symbol] * 100) - Math.round(loadContext.exposureBeforeBySymbol[symbol] * 100);
    exposureAfterBySymbol[symbol].add(exposureAfterRun[symbol]);
    exposureDeltaCentsBySymbol[symbol].add(exposureDeltaCents);
    if (exposureDeltaCents === 0) restoredExposureSymbols.add(1);
  }
}

function readMetricStatistic(testResult, metricName, statisticName) {
  const testMetric = testResult.metrics[metricName];
  const metricStatistic = testMetric && testMetric.values ? testMetric.values[statisticName] : undefined;
  return typeof metricStatistic === 'number' ? metricStatistic : 0;
}

function formatMilliseconds(milliseconds) {
  return `${milliseconds.toFixed(0)} ms`;
}

export function handleSummary(testResult) {
  const testMinutes = testResult.state.testRunDurationMs / 60000;
  // Requests per minute count only orders; the exposure and version reads are left out.
  const totalOrdersSent = readMetricStatistic(testResult, 'orders_sent', 'count');
  // Same metric the error threshold judges.
  const errorRate = readMetricStatistic(testResult, 'http_req_failed', 'rate');
  const exposureBeforeBySymbol = testResult.setup_data ? testResult.setup_data.exposureBeforeBySymbol : {};

  const loadSummary = {
    date: new Date().toISOString(),
    url: API_URL,
    commit: testResult.setup_data ? testResult.setup_data.runningCommit : null,
    durationSeconds: Math.round(testResult.state.testRunDurationMs / 1000),
    orderLatencyMs: {
      p80: readMetricStatistic(testResult, 'order_latency', 'p(80)'),
      p90: readMetricStatistic(testResult, 'order_latency', 'p(90)'),
      p95: readMetricStatistic(testResult, 'order_latency', 'p(95)'),
      p99: readMetricStatistic(testResult, 'order_latency', 'p(99)'),
    },
    orderRequests: totalOrdersSent,
    orderRequestsPerMinute: testMinutes > 0 ? totalOrdersSent / testMinutes : 0,
    errorRate,
    errorRateBelowLimit: errorRate < 0.01,
    ordersBySymbol: Object.fromEntries(FLOWA_SYMBOLS.map((symbol) => [symbol, {
      accepted: readMetricStatistic(testResult, metricNameFor('orders_accepted', symbol), 'count'),
      rejectedInvalidField: readMetricStatistic(testResult, metricNameFor('orders_rejected_invalid_field', symbol), 'count'),
      rejectedLimit: readMetricStatistic(testResult, metricNameFor('orders_rejected_limit', symbol), 'count'),
    }])),
    unexpectedOutcomes: readMetricStatistic(testResult, 'orders_unexpected_outcome', 'count'),
    ordersLeftUnbalanced: readMetricStatistic(testResult, 'orders_left_unbalanced', 'count'),
    exposureBySymbol: Object.fromEntries(FLOWA_SYMBOLS.map((symbol) => [symbol, {
      before: exposureBeforeBySymbol[symbol],
      after: readMetricStatistic(testResult, metricNameFor('exposure_after', symbol), 'value'),
      deltaCents: readMetricStatistic(testResult, metricNameFor('exposure_delta_cents', symbol), 'value'),
    }])),
    exposureRestoredSymbols: readMetricStatistic(testResult, 'exposure_restored_symbols', 'count'),
  };

  const latencyPercentiles = loadSummary.orderLatencyMs;
  const latencyTable = [
    '| Date (UTC) | Running commit | P80 | P90 | P95 | P99 | Requests per minute | Error rate |',
    '|---|---|---|---|---|---|---|---|',
    `| ${loadSummary.date.slice(0, 16).replace('T', ' ')} | ${(loadSummary.commit || '?').slice(0, 7)} `
      + `| ${formatMilliseconds(latencyPercentiles.p80)} | ${formatMilliseconds(latencyPercentiles.p90)} `
      + `| ${formatMilliseconds(latencyPercentiles.p95)} | ${formatMilliseconds(latencyPercentiles.p99)} `
      + `| ${loadSummary.orderRequestsPerMinute.toFixed(0)} | ${(loadSummary.errorRate * 100).toFixed(2)}% |`,
  ].join('\n');

  const ordersBySymbolTable = [
    '| Symbol | Accepted | Rejected: invalid field | Rejected: limit |',
    '|---|---|---|---|',
    ...FLOWA_SYMBOLS.map((symbol) => {
      const symbolOrders = loadSummary.ordersBySymbol[symbol];
      return `| ${symbol} | ${symbolOrders.accepted} | ${symbolOrders.rejectedInvalidField} | ${symbolOrders.rejectedLimit} |`;
    }),
  ].join('\n');

  const exposureTable = [
    '| Symbol | Before | After | Difference |',
    '|---|---|---|---|',
    ...FLOWA_SYMBOLS.map((symbol) => {
      const symbolExposure = loadSummary.exposureBySymbol[symbol];
      return `| ${symbol} | ${symbolExposure.before} | ${symbolExposure.after} | ${(symbolExposure.deltaCents / 100).toFixed(2)} |`;
    }),
  ].join('\n');

  const markdownSummary = [
    '# Flowa load test (k6)',
    '',
    `Against ${loadSummary.url}, ${loadSummary.durationSeconds} s at about 15 orders per second.`,
    `Error rate below 1%: ${loadSummary.errorRateBelowLimit ? 'yes' : 'no'}.`,
    '',
    latencyTable,
    '',
    '## Orders by symbol',
    '',
    ordersBySymbolTable,
    '',
    `Unexpected outcomes: ${loadSummary.unexpectedOutcomes}. Orders left without their opposite order: ${loadSummary.ordersLeftUnbalanced}.`,
    '',
    '## Exposure before and after',
    '',
    exposureTable,
    '',
    `Symbols back to the exposure before the run: ${loadSummary.exposureRestoredSymbols} of ${FLOWA_SYMBOLS.length}.`,
    '',
  ].join('\n');

  return {
    stdout: `${markdownSummary}\n`,
    [`${SUMMARY_DIR}/load-summary.md`]: markdownSummary,
    [`${SUMMARY_DIR}/load-summary.json`]: JSON.stringify(loadSummary, null, 2),
  };
}

import { describe, expect, it } from 'vitest';
import { calculateLimitUsage, calculateRemainingToLimitBySide, findExposureSide } from './limitUsage';

describe('calculateLimitUsage', () => {
  it.each([
    [0, '0%', 0],
    [78_991, '0,07%', 0.07],
    [30_000, '0,03%', 0.03],
    [10_000, '0,01%', 0.01],
    [12_500_000, '12,5%', 12.5],
    [50_000_000, '50%', 50],
    [89_999_999.99, '89,99%', 89.99],
    [90_000_000, '90%', 90],
    [95_000_000, '95%', 95],
    [99_997_997.01, '99,99%', 99.99],
    [99_999_999.99, '99,99%', 99.99],
    [100_000_000, '100%', 100],
  ])('exposure %d shows %s, truncating without rounding up', (exposureInReais, expectedPercentage, expectedWidth) => {
    const limitUsage = calculateLimitUsage(exposureInReais);
    expect(limitUsage.shownPercentage).toBe(expectedPercentage);
    expect(limitUsage.barWidthInPercent).toBe(expectedWidth);
  });

  it.each([
    [0.01, '< 0,01%'],
    [0.5, '< 0,01%'],
    [9_999.99, '< 0,01%'],
    [-0.5, '< 0,01%'],
    [-9_999.99, '< 0,01%'],
  ])('exposure %d, above zero and below 0.01%%, shows "< 0,01%%"', (exposureInReais, expectedPercentage) => {
    const limitUsage = calculateLimitUsage(exposureInReais);
    expect(limitUsage.shownPercentage).toBe(expectedPercentage);
    expect(limitUsage.barWidthInPercent).toBe(0);
    expect(limitUsage.isNearLimit).toBe(false);
  });

  it.each([
    [-95_000_000, '95%', 95, true],
    [-89_999_999.99, '89,99%', 89.99, false],
    [-90_000_000, '90%', 90, true],
    [-78_991, '0,07%', 0.07, false],
  ])('negative exposure %d uses the unsigned value: %s', (exposureInReais, expectedPercentage, expectedWidth, expectedIsNearLimit) => {
    expect(calculateLimitUsage(exposureInReais)).toEqual({
      shownPercentage: expectedPercentage,
      barWidthInPercent: expectedWidth,
      isNearLimit: expectedIsNearLimit,
    });
  });

  it.each([
    [0, false],
    [89_990_000, false],
    [89_999_999.99, false],
    [90_000_000, true],
    [99_997_997.01, true],
  ])('is exposure %d near the limit (amber bar)? %s', (exposureInReais, expectedIsNearLimit) => {
    expect(calculateLimitUsage(exposureInReais).isNearLimit).toBe(expectedIsNearLimit);
  });

  it('above the limit shows the real number and the bar is full and amber', () => {
    expect(calculateLimitUsage(100_500_000)).toEqual({
      shownPercentage: '100,5%',
      barWidthInPercent: 100,
      isNearLimit: true,
    });
  });
});

describe('findExposureSide', () => {
  it.each([
    [99_999_000.01, 'long'],
    [0.01, 'long'],
    [0, 'flat'],
    [-0, 'flat'],
    [0.004, 'flat'],
    [-0.004, 'flat'],
    [-0.01, 'short'],
    [-39_999_535.62, 'short'],
  ])('exposure %d is %s', (exposureInReais, expectedExposureSide) => {
    expect(findExposureSide(exposureInReais)).toBe(expectedExposureSide);
  });
});

describe('calculateRemainingToLimitBySide', () => {
  it.each([
    // Example checked in CA-5: VALE3 sold.
    [-39_999_535.62, 139_999_535.62, 60_000_464.38],
    [99_999_000.01, 999.99, 199_999_000.01],
    [0, 100_000_000, 100_000_000],
    [-99_998_000.01, 199_998_000.01, 1_999.99],
    [100_000_000, 0, 200_000_000],
    [-100_000_000, 200_000_000, 0],
  ])('exposure %d leaves %d to buy and %d to sell', (exposureInReais, expectedRemainingToBuy, expectedRemainingToSell) => {
    expect(calculateRemainingToLimitBySide(exposureInReais)).toEqual({
      remainingToBuyInReais: expectedRemainingToBuy,
      remainingToSellInReais: expectedRemainingToSell,
    });
  });
});

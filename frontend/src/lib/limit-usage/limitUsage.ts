export const EXPOSURE_LIMIT_PER_ASSET_IN_REAIS = 100_000_000;
const EXPOSURE_LIMIT_PER_ASSET_IN_CENTS = EXPOSURE_LIMIT_PER_ASSET_IN_REAIS * 100;

// 1 hundredth of a percentage point of the R$ 100.000.000,00 limit is R$ 10.000,00 = 1.000.000 cents.
const CENTS_PER_HUNDREDTH_OF_PERCENT = 1_000_000;
const WARNING_THRESHOLD_IN_HUNDREDTHS_OF_PERCENT = 9_000;

const percentageFormatter = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 });

export type LimitUsage = {
  shownPercentage: string;
  barWidthInPercent: number;
  isNearLimit: boolean;
};

export type ExposureSide = 'long' | 'short' | 'flat';

export type RemainingToLimitBySide = {
  remainingToBuyInReais: number;
  remainingToSellInReais: number;
};

// The side is judged in whole cents, so -0 and floating point dust read as flat.
export function findExposureSide(exposureInReais: number): ExposureSide {
  const exposureInCents = Math.round(exposureInReais * 100);
  if (exposureInCents > 0) return 'long';
  if (exposureInCents < 0) return 'short';
  return 'flat';
}

// A buy raises the exposure toward +limit and a sell lowers it toward −limit, so each side has its own room.
export function calculateRemainingToLimitBySide(exposureInReais: number): RemainingToLimitBySide {
  const exposureInCents = Math.round(exposureInReais * 100);
  return {
    remainingToBuyInReais: (EXPOSURE_LIMIT_PER_ASSET_IN_CENTS - exposureInCents) / 100,
    remainingToSellInReais: (EXPOSURE_LIMIT_PER_ASSET_IN_CENTS + exposureInCents) / 100,
  };
}

// A sell that makes the exposure negative also consumes the limit: the math uses the unsigned value.
// The percentage is truncated to 2 decimals, never rounded up, and the math runs in whole
// cents so floating point does not turn 89.999% into 90%.
export function calculateLimitUsage(exposureInReais: number): LimitUsage {
  const unsignedExposureInCents = Math.round(Math.abs(exposureInReais) * 100);
  const hundredthsOfPercent =
    (unsignedExposureInCents - (unsignedExposureInCents % CENTS_PER_HUNDREDTH_OF_PERCENT)) / CENTS_PER_HUNDREDTH_OF_PERCENT;
  const truncatedPercentage = hundredthsOfPercent / 100;
  const isBelowSmallestShownValue = unsignedExposureInCents > 0 && hundredthsOfPercent === 0;

  return {
    shownPercentage: isBelowSmallestShownValue ? '< 0,01%' : `${percentageFormatter.format(truncatedPercentage)}%`,
    barWidthInPercent: Math.min(truncatedPercentage, 100),
    isNearLimit: hundredthsOfPercent >= WARNING_THRESHOLD_IN_HUNDREDTHS_OF_PERCENT,
  };
}

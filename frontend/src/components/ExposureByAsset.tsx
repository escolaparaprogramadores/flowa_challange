import { RisingChartIcon } from './Icons';
import type { ExposuresState } from '../hooks/useOrdersAndExposures';
import {
  calculateLimitUsage,
  calculateRemainingToLimitBySide,
  EXPOSURE_LIMIT_PER_ASSET_IN_REAIS,
  findExposureSide,
  type ExposureSide,
} from '../lib/limit-usage/limitUsage';
import { formatBrazilianReais } from '../lib/number-format/brazilianNumberFormat';
import type { SymbolExposure } from '../services/ordersService';

const EXPOSURE_SIDE_LABELS: Record<ExposureSide, string> = { long: 'Comprado', short: 'Vendido', flat: 'Zerado' };

// The section keeps the "panel" class because the grid tests check the cards through it; the panel
// look is removed in CSS, since each asset is now its own card.
export function ExposureByAsset({ exposuresState }: { exposuresState: ExposuresState }) {
  return (
    <section className="panel exposure" aria-labelledby="exposure-heading" aria-busy={exposuresState.status === 'loading'}>
      <div className="exposure-header">
        <h2 className="exposure-title" id="exposure-heading">Exposição por ativo</h2>
        <p className="exposure-limit">Limite por ativo · {formatBrazilianReais(EXPOSURE_LIMIT_PER_ASSET_IN_REAIS)}</p>
      </div>
      {exposuresState.status === 'loading' && <p className="exposure-notice">Carregando a exposição…</p>}
      {exposuresState.status === 'error' && (
        <p className="exposure-notice error" role="alert">{exposuresState.errorMessage}</p>
      )}
      {exposuresState.status === 'ready' && (
        <ul className="exposure-list">
          {exposuresState.symbolExposures.map((symbolExposure) => (
            <AssetCard key={symbolExposure.symbol} symbolExposure={symbolExposure} />
          ))}
        </ul>
      )}
    </section>
  );
}

function AssetCard({ symbolExposure }: { symbolExposure: SymbolExposure }) {
  const limitUsage = calculateLimitUsage(symbolExposure.exposure);
  const exposureSide = findExposureSide(symbolExposure.exposure);
  const remainingToLimitBySide = calculateRemainingToLimitBySide(symbolExposure.exposure);
  const limitUsageFillClasses = [
    'limit-usage-fill',
    symbolExposure.exposure !== 0 && 'has-exposure',
    limitUsage.isNearLimit && 'near-limit',
  ]
    .filter(Boolean)
    .join(' ');
  return (
    <li className="exposure-item" data-testid={`exposicao-${symbolExposure.symbol}`}>
      <div className="exposure-asset">
        <span className={`exposure-icon exposure-icon-${exposureSide}`}>
          <RisingChartIcon />
        </span>
        <h3 className="exposure-symbol">{symbolExposure.symbol}</h3>
        <span className={`exposure-side exposure-side-${exposureSide}`} data-testid="exposicao-lado">
          {EXPOSURE_SIDE_LABELS[exposureSide]}
        </span>
      </div>
      <dl className="exposure-figures">
        <div className="exposure-current-figure">
          <dt>Exposição atual</dt>
          <dd className="numeric exposure-current" data-testid="exposicao-atual">{formatBrazilianReais(symbolExposure.exposure)}</dd>
        </div>
        <RemainingToLimitFigure
          figureLabel="Falta para comprar"
          figureTestId="falta-para-comprar"
          remainingInReais={remainingToLimitBySide.remainingToBuyInReais}
          isOnPositionSide={exposureSide !== 'short'}
        />
        <RemainingToLimitFigure
          figureLabel="Falta para vender"
          figureTestId="falta-para-vender"
          remainingInReais={remainingToLimitBySide.remainingToSellInReais}
          isOnPositionSide={exposureSide === 'short'}
        />
      </dl>
      <div className="limit-usage">
        <div
          className="limit-usage-track"
          role="meter"
          aria-label={`Uso do limite de ${symbolExposure.symbol}`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={limitUsage.barWidthInPercent}
          aria-valuetext={limitUsage.shownPercentage}
        >
          <span
            className={limitUsageFillClasses}
            data-testid="uso-do-limite-preenchimento"
            style={{ width: `${limitUsage.barWidthInPercent}%` }}
          />
        </div>
        <p className="limit-usage-caption" aria-hidden="true">
          <span>Uso do limite</span>
          <span className={limitUsage.isNearLimit ? 'numeric near-limit' : 'numeric'} data-testid="uso-do-limite-porcentagem">
            {limitUsage.shownPercentage}
          </span>
        </p>
      </div>
    </li>
  );
}

// "exposicao-restante" marks the figure on the side the position already is: it equals 100.000.000 − |exposure|,
// the "remaining" the API sends, and the older E2E files read the card through it.
function RemainingToLimitFigure({
  figureLabel,
  figureTestId,
  remainingInReais,
  isOnPositionSide,
}: {
  figureLabel: string;
  figureTestId: string;
  remainingInReais: number;
  isOnPositionSide: boolean;
}) {
  const shownRemaining = formatBrazilianReais(remainingInReais);
  return (
    <div>
      <dt>{figureLabel}</dt>
      <dd className="numeric exposure-remaining" data-testid={figureTestId}>
        {isOnPositionSide ? <span data-testid="exposicao-restante">{shownRemaining}</span> : shownRemaining}
      </dd>
    </div>
  );
}

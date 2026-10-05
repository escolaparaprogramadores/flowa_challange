import { DatadogLogo, ExternalArrowIcon, ShieldIcon } from './Icons';
import '../styles/top-bar.css';

const DATADOG_DASHBOARDS = [
  {
    dashboardName: 'Four Golden Signals',
    dashboardUrl:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-a9e17306767d8f22537a7acb53350942?refresh_mode=sliding&tpl_var_ecs_service%5B0%5D=%2A&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=%2A&tpl_var_version%5B0%5D=%2A&from_ts=1791086628736&to_ts=1791101028736&live=true',
  },
  {
    dashboardName: 'Jornada da ordem',
    dashboardUrl:
      'https://app.datadoghq.com/dashboard/mvw-rz6-i7h?fromUser=false&graphType=flamegraph&refresh_mode=sliding&shouldShowLegend=true&traceQuery=&from_ts=1791086615296&to_ts=1791101015296&live=true',
  },
  {
    dashboardName: 'Ordens e exposição',
    dashboardUrl:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-cb393cd3b3760676912c981fa71f3372?refresh_mode=sliding&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=order-accumulator&tpl_var_side%5B0%5D=%2A&tpl_var_symbol%5B0%5D=%2A&from_ts=1791086654893&to_ts=1791101054893&live=true',
  },
] as const;

export function TopBar() {
  return (
    <header className="top-bar">
      <BaseLogo />
      <div className="top-bar-actions">
        <ul className="top-bar-dashboards" aria-label="Painéis do Datadog">
          {DATADOG_DASHBOARDS.map((datadogDashboard) => (
            <li key={datadogDashboard.dashboardName}>
              <DatadogDashboardLink dashboardName={datadogDashboard.dashboardName} dashboardUrl={datadogDashboard.dashboardUrl} />
            </li>
          ))}
        </ul>
        <EnvironmentBadge />
      </div>
    </header>
  );
}

function BaseLogo() {
  return (
    <span className="logo" role="img" aria-label="Base investimentos">
      <span className="logo-brand" aria-hidden="true">base</span>
      <span className="logo-suffix" aria-hidden="true">investimentos</span>
    </span>
  );
}

function DatadogDashboardLink({ dashboardName, dashboardUrl }: { dashboardName: string; dashboardUrl: string }) {
  return (
    <a className="datadog-dashboard-link" href={dashboardUrl} target="_blank" rel="noopener noreferrer">
      <span className="datadog-dashboard-logo">
        <DatadogLogo />
      </span>
      <span className="datadog-dashboard-texts">
        <span className="datadog-dashboard-brand">Datadog</span>
        <span className="datadog-dashboard-name">{dashboardName}</span>
      </span>
      <ExternalArrowIcon className="datadog-dashboard-arrow" />
      <span className="screen-reader-only"> (abre em nova aba)</span>
    </a>
  );
}

function EnvironmentBadge() {
  return (
    <div className="environment-badge">
      <span className="environment-badge-icon">
        <ShieldIcon />
      </span>
      <span className="environment-badge-texts">
        <span className="environment-badge-label">Ambiente</span>
        <span className="environment-badge-name">Demonstração</span>
      </span>
    </div>
  );
}

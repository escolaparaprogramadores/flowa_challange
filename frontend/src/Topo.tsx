import { IconeEscudo, IconeSetaParaFora, LogoDatadog } from './Icones';
import './topo.css';

const PAINEIS_DO_DATADOG = [
  {
    nomeDoPainel: 'Four Golden Signals',
    enderecoDoPainel:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-a9e17306767d8f22537a7acb53350942?refresh_mode=sliding&tpl_var_ecs_service%5B0%5D=%2A&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=%2A&tpl_var_version%5B0%5D=%2A&from_ts=1791086628736&to_ts=1791101028736&live=true',
  },
  {
    nomeDoPainel: 'Jornada da ordem',
    enderecoDoPainel:
      'https://app.datadoghq.com/dashboard/mvw-rz6-i7h?fromUser=false&graphType=flamegraph&refresh_mode=sliding&shouldShowLegend=true&traceQuery=&from_ts=1791086615296&to_ts=1791101015296&live=true',
  },
  {
    nomeDoPainel: 'Ordens e exposição',
    enderecoDoPainel:
      'https://p.datadoghq.com/sb/63578a59-bd12-11f1-a546-261a98ac5284-cb393cd3b3760676912c981fa71f3372?refresh_mode=sliding&tpl_var_env%5B0%5D=dev&tpl_var_service%5B0%5D=order-accumulator&tpl_var_side%5B0%5D=%2A&tpl_var_symbol%5B0%5D=%2A&from_ts=1791086654893&to_ts=1791101054893&live=true',
  },
] as const;

export function Topo() {
  return (
    <header className="topo">
      <LogoBase />
      <div className="topo-acoes">
        <ul className="topo-paineis" aria-label="Painéis do Datadog">
          {PAINEIS_DO_DATADOG.map((painelDoDatadog) => (
            <li key={painelDoDatadog.nomeDoPainel}>
              <LinkDoPainelDoDatadog nomeDoPainel={painelDoDatadog.nomeDoPainel} enderecoDoPainel={painelDoDatadog.enderecoDoPainel} />
            </li>
          ))}
        </ul>
        <SeloDoAmbiente />
      </div>
    </header>
  );
}

function LogoBase() {
  return (
    <span className="logo" role="img" aria-label="Base investimentos">
      <span className="logo-base" aria-hidden="true">base</span>
      <span className="logo-complemento" aria-hidden="true">investimentos</span>
    </span>
  );
}

function LinkDoPainelDoDatadog({ nomeDoPainel, enderecoDoPainel }: { nomeDoPainel: string; enderecoDoPainel: string }) {
  return (
    <a className="painel-datadog" href={enderecoDoPainel} target="_blank" rel="noopener noreferrer">
      <span className="painel-datadog-logo">
        <LogoDatadog />
      </span>
      <span className="painel-datadog-textos">
        <span className="painel-datadog-marca">Datadog</span>
        <span className="painel-datadog-nome">{nomeDoPainel}</span>
      </span>
      <IconeSetaParaFora className="painel-datadog-seta" />
      <span className="sr"> (abre em nova aba)</span>
    </a>
  );
}

function SeloDoAmbiente() {
  return (
    <div className="selo-ambiente">
      <span className="selo-ambiente-icone">
        <IconeEscudo />
      </span>
      <span className="selo-ambiente-textos">
        <span className="selo-ambiente-rotulo">Ambiente</span>
        <span className="selo-ambiente-nome">Demonstração</span>
      </span>
    </div>
  );
}

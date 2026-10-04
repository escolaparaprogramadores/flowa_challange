import { formatarReais } from './lib/validacaoDaOrdem';
import type { ExposicaoDoSimbolo } from './ordensService';

export type EstadoDasExposicoes =
  | { situacao: 'carregando' }
  | { situacao: 'erro'; mensagemDeErro: string }
  | { situacao: 'pronto'; exposicoesPorSimbolo: ExposicaoDoSimbolo[] };

export function ExposicaoPorAtivo({ estadoDasExposicoes }: { estadoDasExposicoes: EstadoDasExposicoes }) {
  return (
    <section className="painel exposicao" aria-labelledby="titulo-exposicao" aria-busy={estadoDasExposicoes.situacao === 'carregando'}>
      <h2 className="painel-cabeca" id="titulo-exposicao">Exposição por ativo</h2>
      {estadoDasExposicoes.situacao === 'carregando' && <p className="exposicao-aviso">Carregando a exposição…</p>}
      {estadoDasExposicoes.situacao === 'erro' && (
        <p className="exposicao-aviso erro" role="alert">{estadoDasExposicoes.mensagemDeErro}</p>
      )}
      {estadoDasExposicoes.situacao === 'pronto' && (
        <ul className="exposicao-lista">
          {estadoDasExposicoes.exposicoesPorSimbolo.map((exposicaoDoSimbolo) => (
            <li className="exposicao-item" key={exposicaoDoSimbolo.simbolo} data-testid={`exposicao-${exposicaoDoSimbolo.simbolo}`}>
              <span className="exposicao-simbolo">{exposicaoDoSimbolo.simbolo}</span>
              <dl className="exposicao-dados">
                <dt>Exposição atual</dt>
                <dd className="num" data-testid="exposicao-atual">{formatarReais(exposicaoDoSimbolo.exposicao)}</dd>
                <dt>Falta até o limite</dt>
                <dd className="num" data-testid="exposicao-restante">{formatarReais(exposicaoDoSimbolo.restanteAteOLimite)}</dd>
              </dl>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

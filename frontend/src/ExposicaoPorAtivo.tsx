import { IconeGraficoSubindo } from './Icones';
import { calcularUsoDoLimite, LIMITE_DE_EXPOSICAO_POR_ATIVO_EM_REAIS } from './lib/usoDoLimite';
import { formatarReais } from './lib/validacaoDaOrdem';
import type { ExposicaoDoSimbolo } from './ordensService';

export type EstadoDasExposicoes =
  | { situacao: 'carregando' }
  | { situacao: 'erro'; mensagemDeErro: string }
  | { situacao: 'pronto'; exposicoesPorSimbolo: ExposicaoDoSimbolo[] };

// A seção mantém a classe "painel" porque os testes da grade conferem os cartões por ela; o visual de
// painel sai no CSS, já que cada ativo agora é um cartão próprio.
export function ExposicaoPorAtivo({ estadoDasExposicoes }: { estadoDasExposicoes: EstadoDasExposicoes }) {
  return (
    <section className="painel exposicao" aria-labelledby="titulo-exposicao" aria-busy={estadoDasExposicoes.situacao === 'carregando'}>
      <div className="exposicao-cabeca">
        <h2 className="exposicao-titulo" id="titulo-exposicao">Exposição por ativo</h2>
        <p className="exposicao-limite">Limite por ativo · {formatarReais(LIMITE_DE_EXPOSICAO_POR_ATIVO_EM_REAIS)}</p>
      </div>
      {estadoDasExposicoes.situacao === 'carregando' && <p className="exposicao-aviso">Carregando a exposição…</p>}
      {estadoDasExposicoes.situacao === 'erro' && (
        <p className="exposicao-aviso erro" role="alert">{estadoDasExposicoes.mensagemDeErro}</p>
      )}
      {estadoDasExposicoes.situacao === 'pronto' && (
        <ul className="exposicao-lista">
          {estadoDasExposicoes.exposicoesPorSimbolo.map((exposicaoDoSimbolo) => (
            <CartaoDoAtivo key={exposicaoDoSimbolo.simbolo} exposicaoDoSimbolo={exposicaoDoSimbolo} />
          ))}
        </ul>
      )}
    </section>
  );
}

function CartaoDoAtivo({ exposicaoDoSimbolo }: { exposicaoDoSimbolo: ExposicaoDoSimbolo }) {
  const usoDoLimite = calcularUsoDoLimite(exposicaoDoSimbolo.exposicao);
  const classesDoPreenchimento = [
    'uso-do-limite-preenchimento',
    exposicaoDoSimbolo.exposicao !== 0 && 'com-exposicao',
    usoDoLimite.estaPertoDoLimite && 'perto-do-limite',
  ]
    .filter(Boolean)
    .join(' ');
  return (
    <li className="exposicao-item" data-testid={`exposicao-${exposicaoDoSimbolo.simbolo}`}>
      <div className="exposicao-ativo">
        <span className="exposicao-icone">
          <IconeGraficoSubindo />
        </span>
        <h3 className="exposicao-simbolo">{exposicaoDoSimbolo.simbolo}</h3>
      </div>
      <dl className="exposicao-dados">
        <div>
          <dt>Exposição atual</dt>
          <dd className="num exposicao-atual" data-testid="exposicao-atual">{formatarReais(exposicaoDoSimbolo.exposicao)}</dd>
        </div>
        <div>
          <dt>Falta até o limite</dt>
          <dd className="num exposicao-restante" data-testid="exposicao-restante">{formatarReais(exposicaoDoSimbolo.restanteAteOLimite)}</dd>
        </div>
      </dl>
      <div className="uso-do-limite">
        <div
          className="uso-do-limite-trilho"
          role="meter"
          aria-label={`Uso do limite de ${exposicaoDoSimbolo.simbolo}`}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={usoDoLimite.larguraDaBarraEmPorcentagem}
          aria-valuetext={usoDoLimite.porcentagemMostrada}
        >
          <span
            className={classesDoPreenchimento}
            data-testid="uso-do-limite-preenchimento"
            style={{ width: `${usoDoLimite.larguraDaBarraEmPorcentagem}%` }}
          />
        </div>
        <p className="uso-do-limite-legenda" aria-hidden="true">
          <span>Uso do limite</span>
          <span className={usoDoLimite.estaPertoDoLimite ? 'num perto-do-limite' : 'num'} data-testid="uso-do-limite-porcentagem">
            {usoDoLimite.porcentagemMostrada}
          </span>
        </p>
      </div>
    </li>
  );
}

// Teste de carga do Flowa com o k6 OSS: k6 run -e FLOWA_URL=https://<api> k6/carga-ordens.js
// As ordens andam em pares de compra e venda do mesmo símbolo, quantidade e preço, para a
// exposição do banco público voltar perto de onde estava sem apagar nada.
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const URL_DA_API = (__ENV.FLOWA_URL || '').replace(/\/+$/, '');
if (!/^https?:\/\/[^\s/]+$/.test(URL_DA_API)) {
  throw new Error('Defina FLOWA_URL com a origem da API, por exemplo -e FLOWA_URL=https://exemplo.com');
}

// Tetos fixos no script. FLOWA_SEGUNDOS só encurta o teste, nunca passa dos 5 minutos.
const ORDENS_POR_SEGUNDO = 15;
const DURACAO_MAXIMA_EM_SEGUNDOS = 300;
const VUS_PRE_ALOCADOS = 10;
const VUS_NO_MAXIMO = 30;
const duracaoDoTesteEmSegundos = Math.min(
  DURACAO_MAXIMA_EM_SEGUNDOS,
  Math.max(1, parseInt(__ENV.FLOWA_SEGUNDOS || `${DURACAO_MAXIMA_EM_SEGUNDOS}`, 10) || DURACAO_MAXIMA_EM_SEGUNDOS),
);

const PASTA_DO_RESUMO = (__ENV.FLOWA_RESUMO_DIR || '.').replace(/[\\/]+$/, '');
const SIMBOLOS_DO_FLOWA = ['PETR4', 'VALE3', 'VIIA4'];
const QUANTIDADE_DA_ORDEM = 1;
const PRECO_DA_ORDEM = 1.0;

export const options = {
  scenarios: {
    ordens_em_pares: {
      executor: 'constant-arrival-rate',
      rate: ORDENS_POR_SEGUNDO,
      timeUnit: '1s',
      duration: `${duracaoDoTesteEmSegundos}s`,
      preAllocatedVUs: VUS_PRE_ALOCADOS,
      maxVUs: VUS_NO_MAXIMO,
      gracefulStop: '10s',
    },
  },
  // Só reprova por erro. A rejeição de negócio volta 200 (docs/contracts/contracts.md), então
  // aqui contam 400, o 429 do freio da API e os 5xx.
  thresholds: {
    http_req_failed: ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(80)', 'p(90)', 'p(95)', 'p(99)'],
};

const latenciaDaOrdem = new Trend('flowa_latencia_ordem', true);
const ordensAceitas = new Counter('flowa_ordens_aceitas');
const ordensRejeitadas = new Counter('flowa_ordens_rejeitadas');
const paresAbertos = new Counter('flowa_pares_abertos');
const paresFechados = new Counter('flowa_pares_fechados');
const fechamentosNaoAceitos = new Counter('flowa_fechamentos_nao_aceitos');
const ordensEnviadas = new Counter('flowa_ordens_enviadas');

// Cada VU guarda a segunda perna do par que abriu. O par fica aberto se o teste acaba antes dela
// ou se ela não é aceita: é o resíduo que aparece na exposição depois do run.
let segundaPernaPendente = null;
let paresIniciadosNesteVu = 0;

export function setup() {
  const respostaDaVersao = http.get(`${URL_DA_API}/version`, { tags: { name: 'version' } });
  const respostaDaExposicao = http.get(`${URL_DA_API}/api/exposures`, { tags: { name: 'exposicao' } });
  if (respostaDaVersao.status !== 200 || respostaDaExposicao.status !== 200) {
    throw new Error(`API fora do ar: /version ${respostaDaVersao.status}, /api/exposures ${respostaDaExposicao.status}`);
  }

  // A primeira perna vai para o lado que aproxima a exposição de zero, para não empurrar um
  // símbolo já quase cheio contra o limite.
  const primeiroLadoPorSimbolo = {};
  for (const exposicaoDoSimbolo of respostaDaExposicao.json('exposures')) {
    primeiroLadoPorSimbolo[exposicaoDoSimbolo.symbol] = exposicaoDoSimbolo.exposure > 0 ? 'sell' : 'buy';
  }
  return { commitNoAr: respostaDaVersao.json('commit'), primeiroLadoPorSimbolo };
}

export default function enviarOrdemDoPar(contextoDaCarga) {
  let ordemDaVez = segundaPernaPendente;
  segundaPernaPendente = null;
  if (ordemDaVez === null) {
    const simboloDoPar = SIMBOLOS_DO_FLOWA[(__VU + paresIniciadosNesteVu) % SIMBOLOS_DO_FLOWA.length];
    paresIniciadosNesteVu += 1;
    ordemDaVez = { symbol: simboloDoPar, side: contextoDaCarga.primeiroLadoPorSimbolo[simboloDoPar], abrePar: true };
  }

  const respostaDaOrdem = http.post(
    `${URL_DA_API}/api/orders`,
    JSON.stringify({ symbol: ordemDaVez.symbol, side: ordemDaVez.side, quantity: QUANTIDADE_DA_ORDEM, price: PRECO_DA_ORDEM }),
    { headers: { 'Content-Type': 'application/json' }, tags: { name: 'ordem' }, timeout: '15s' },
  );
  ordensEnviadas.add(1);
  latenciaDaOrdem.add(respostaDaOrdem.timings.duration);
  check(respostaDaOrdem, { 'ordem respondida com 200': (respostaRecebida) => respostaRecebida.status === 200 });

  const situacaoDaOrdem = respostaDaOrdem.status === 200 ? respostaDaOrdem.json('status') : null;
  if (situacaoDaOrdem === 'accepted') ordensAceitas.add(1);
  if (situacaoDaOrdem === 'rejected') ordensRejeitadas.add(1);
  if (situacaoDaOrdem !== 'accepted') {
    if (!ordemDaVez.abrePar) fechamentosNaoAceitos.add(1);
    return;
  }

  // A outra perna só sai se esta foi aceita; senão o par mexeria na exposição.
  if (ordemDaVez.abrePar) {
    paresAbertos.add(1);
    segundaPernaPendente = { symbol: ordemDaVez.symbol, side: ordemDaVez.side === 'buy' ? 'sell' : 'buy', abrePar: false };
  } else {
    paresFechados.add(1);
  }
}

function lerEstatisticaDaMetrica(resultadoDoTeste, nomeDaMetrica, nomeDaEstatistica) {
  const metricaDoTeste = resultadoDoTeste.metrics[nomeDaMetrica];
  const estatisticaDaMetrica = metricaDoTeste && metricaDoTeste.values ? metricaDoTeste.values[nomeDaEstatistica] : undefined;
  return typeof estatisticaDaMetrica === 'number' ? estatisticaDaMetrica : 0;
}

function formatarMilissegundos(milissegundos) {
  return `${milissegundos.toFixed(0)} ms`;
}

export function handleSummary(resultadoDoTeste) {
  const minutosDeTeste = resultadoDoTeste.state.testRunDurationMs / 60000;
  // Requisições por minuto contam só as ordens; os dois GETs do setup ficam de fora.
  const totalDeOrdensEnviadas = lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_ordens_enviadas', 'count');
  // A taxa de erro é a mesma métrica que o threshold julga.
  const taxaDeErro = lerEstatisticaDaMetrica(resultadoDoTeste, 'http_req_failed', 'rate');
  const resumoDaCarga = {
    data: new Date().toISOString(),
    url: URL_DA_API,
    commit: resultadoDoTeste.setup_data ? resultadoDoTeste.setup_data.commitNoAr : null,
    duracao_segundos: Math.round(resultadoDoTeste.state.testRunDurationMs / 1000),
    latencia_ordem_ms: {
      p80: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_latencia_ordem', 'p(80)'),
      p90: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_latencia_ordem', 'p(90)'),
      p95: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_latencia_ordem', 'p(95)'),
      p99: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_latencia_ordem', 'p(99)'),
    },
    requisicoes: totalDeOrdensEnviadas,
    requisicoes_por_minuto: minutosDeTeste > 0 ? totalDeOrdensEnviadas / minutosDeTeste : 0,
    taxa_de_erro: taxaDeErro,
    limite_de_erro_respeitado: taxaDeErro < 0.01,
    ordens_aceitas: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_ordens_aceitas', 'count'),
    ordens_rejeitadas: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_ordens_rejeitadas', 'count'),
    pares_abertos: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_pares_abertos', 'count'),
    pares_fechados: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_pares_fechados', 'count'),
    fechamentos_nao_aceitos: lerEstatisticaDaMetrica(resultadoDoTeste, 'flowa_fechamentos_nao_aceitos', 'count'),
  };

  const percentisDaLatencia = resumoDaCarga.latencia_ordem_ms;
  const tabelaDoReadme = [
    '| Data (UTC) | Commit no ar | P80 | P90 | P95 | P99 | Requisições por minuto | Taxa de erro |',
    '|---|---|---|---|---|---|---|---|',
    `| ${resumoDaCarga.data.slice(0, 16).replace('T', ' ')} | ${(resumoDaCarga.commit || '?').slice(0, 7)} `
      + `| ${formatarMilissegundos(percentisDaLatencia.p80)} | ${formatarMilissegundos(percentisDaLatencia.p90)} `
      + `| ${formatarMilissegundos(percentisDaLatencia.p95)} | ${formatarMilissegundos(percentisDaLatencia.p99)} `
      + `| ${resumoDaCarga.requisicoes_por_minuto.toFixed(0)} | ${(resumoDaCarga.taxa_de_erro * 100).toFixed(2)}% |`,
  ].join('\n');

  const resumoEmMarkdown = [
    '# Teste de carga do Flowa (k6)',
    '',
    `Contra ${resumoDaCarga.url}, ${resumoDaCarga.duracao_segundos} s a ${ORDENS_POR_SEGUNDO} ordens por segundo.`,
    `Taxa de erro abaixo de 1%: ${resumoDaCarga.limite_de_erro_respeitado ? 'sim' : 'não'}.`,
    '',
    tabelaDoReadme,
    '',
    `Ordens aceitas: ${resumoDaCarga.ordens_aceitas}. Rejeitadas pela regra ou pelo limite: ${resumoDaCarga.ordens_rejeitadas}.`,
    `Pares abertos: ${resumoDaCarga.pares_abertos}. Fechados: ${resumoDaCarga.pares_fechados}. A diferença é o resíduo na exposição.`,
    `Pares que ficaram abertos porque a perna de fechamento não foi aceita: ${resumoDaCarga.fechamentos_nao_aceitos}.`,
    '',
  ].join('\n');

  return {
    stdout: `${resumoEmMarkdown}\n`,
    [`${PASTA_DO_RESUMO}/resumo-carga.md`]: resumoEmMarkdown,
    [`${PASTA_DO_RESUMO}/resumo-carga.json`]: JSON.stringify(resumoDaCarga, null, 2),
  };
}

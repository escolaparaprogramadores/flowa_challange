import { expect, test, type Page } from '@playwright/test';
import { ROTA_DAS_EXPOSICOES, ROTA_DAS_ORDENS, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Estes cenários falam com o OrderGenerator e o OrderAccumulator de verdade.
// Os valores de exposição são lidos antes e depois de cada ordem, então o teste
// não depende do banco estar vazio.

const LIMITE_DE_EXPOSICAO_POR_SIMBOLO = 100_000_000;
const COR_DO_STATUS_ACEITO = 'rgb(111, 235, 192)';
const COR_DO_STATUS_REJEITADO = 'rgb(255, 164, 151)';

type OrdemDoTeste = { simbolo: string; lado: 'Compra' | 'Venda'; quantidade: string; preco: string };

type OrdemCriadaNoServidor = { status: string; message: string; clOrdId: string };

// The answer of POST /api/orders is a DataMessage: the order is its "data" and the text of the screen is its "message".
function readCreatedOrderFromDataMessage(orderDataMessage: { message: string; data: { status: string; clOrdId: string } }): OrdemCriadaNoServidor {
  return { ...orderDataMessage.data, message: orderDataMessage.message };
}

// Devolve o corpo do POST e só termina depois que a lista foi lida de novo, já com a ordem enviada.
async function enviarOrdemPelaBoleta(paginaDaBoleta: Page, ordemDoTeste: OrdemDoTeste): Promise<OrdemCriadaNoServidor> {
  await paginaDaBoleta.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: ordemDoTeste.simbolo, exact: true }).click();
  await paginaDaBoleta.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: ordemDoTeste.lado }).click();
  await paginaDaBoleta.getByLabel(/^Quantidade de/).fill(ordemDoTeste.quantidade);
  await paginaDaBoleta.getByLabel('Preço por ação (R$)').fill(ordemDoTeste.preco);
  const respostaDaCriacaoDaOrdem = paginaDaBoleta.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  const releituraDaLista = paginaDaBoleta.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'GET' && new URL(respostaHttp.url()).pathname === ROTA_DAS_ORDENS);
  await paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ }).click();
  const ordemCriada = readCreatedOrderFromDataMessage(await (await respostaDaCriacaoDaOrdem).json());
  await releituraDaLista;
  await expect(paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return ordemCriada;
}

type ExposicaoNoServidor = { symbol: string; exposure: number; remaining: number };

async function lerExposicaoNoServidor(paginaDaBoleta: Page, simboloDaExposicao: string): Promise<ExposicaoNoServidor> {
  const respostaDasExposicoes = await paginaDaBoleta.request.get(ROTA_DAS_EXPOSICOES);
  expect(respostaDasExposicoes.status()).toBe(200);
  const corpoDasExposicoes = ((await respostaDasExposicoes.json()) as { data: { exposures: ExposicaoNoServidor[] } }).data;
  const exposicaoDoSimbolo = corpoDasExposicoes.exposures.find((exposicaoDoSimbolo) => exposicaoDoSimbolo.symbol === simboloDaExposicao);
  if (!exposicaoDoSimbolo) throw new Error('Símbolo ' + simboloDaExposicao + ' ausente em /api/exposures');
  return exposicaoDoSimbolo;
}

const formatadorDeReais = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });

async function conferirPainelDeExposicao(paginaDaBoleta: Page, simboloDaExposicao: string, exposicaoAtual: number, restanteAteOLimite: number) {
  const linhaDoSimbolo = paginaDaBoleta.getByTestId('exposicao-' + simboloDaExposicao);
  await expect(linhaDoSimbolo.getByTestId('exposicao-atual')).toHaveText(formatadorDeReais.format(exposicaoAtual));
  await expect(linhaDoSimbolo.getByTestId('exposicao-restante')).toHaveText(formatadorDeReais.format(restanteAteOLimite));
}

// A resposta de cada envio agora é a linha nova no topo de "Compra/Venda" (CA-11, CA-12).
function celulaDaOrdemNoTopo(paginaDaBoleta: Page, colunaDaLista: string) {
  return paginaDaBoleta.getByTestId('linha-da-ordem').first().locator(`td[data-coluna="${colunaDaLista}"]`);
}

function seloDaOrdemNoTopo(paginaDaBoleta: Page) {
  return celulaDaOrdemNoTopo(paginaDaBoleta, 'status').locator('.selo-da-ordem');
}

async function conferirOrdemNoTopoDaLista(
  paginaDaBoleta: Page,
  ordemCriada: OrdemCriadaNoServidor,
  ordemEsperada: { selo: 'Aceita' | 'Rejeitada'; ativo: string; lado: string; quantidade: string; preco: string },
) {
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'identificador-do-envio')).toHaveText(ordemCriada.clOrdId);
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'identificador-do-envio')).toHaveText(/^[0-9a-f]{32}$/i);
  await expect(seloDaOrdemNoTopo(paginaDaBoleta)).toHaveText(ordemEsperada.selo);
  await expect(seloDaOrdemNoTopo(paginaDaBoleta)).toHaveCSS('color', ordemEsperada.selo === 'Aceita' ? COR_DO_STATUS_ACEITO : COR_DO_STATUS_REJEITADO);
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'ativo')).toHaveText(ordemEsperada.ativo);
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'lado')).toHaveText(ordemEsperada.lado);
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'quantidade')).toHaveText(ordemEsperada.quantidade);
  await expect(celulaDaOrdemNoTopo(paginaDaBoleta, 'preco')).toHaveText(ordemEsperada.preco);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-14 e CA-11: compra válida é enviada e entra aceita no topo de Compra/Venda', async ({ page }) => {
  const ordemCriada = await enviarOrdemPelaBoleta(page, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  expect(ordemCriada.status).toBe('accepted');
  expect(ordemCriada.message).toBe('Ordem aceita.');
  await conferirOrdemNoTopoDaLista(page, ordemCriada, { selo: 'Aceita', ativo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: formatadorDeReais.format(10) });
});

test('CA-14 e CA-11: venda válida é enviada e entra aceita no topo de Compra/Venda', async ({ page }) => {
  const ordemCriada = await enviarOrdemPelaBoleta(page, { simbolo: 'VALE3', lado: 'Venda', quantidade: '200', preco: '55,30' });
  expect(ordemCriada.status).toBe('accepted');
  await conferirOrdemNoTopoDaLista(page, ordemCriada, { selo: 'Aceita', ativo: 'VALE3', lado: 'Venda', quantidade: '200', preco: formatadorDeReais.format(55.3) });
});

test('CA-17: o painel mostra os três ativos e muda depois de uma ordem aceita', async ({ page }) => {
  for (const simboloDaOrdem of ['PETR4', 'VALE3', 'VIIA4']) {
    const exposicaoAtual = await lerExposicaoNoServidor(page, simboloDaOrdem);
    await conferirPainelDeExposicao(page, simboloDaOrdem, exposicaoAtual.exposure, exposicaoAtual.remaining);
  }
  const exposicaoAntes = await lerExposicaoNoServidor(page, 'PETR4');
  const ordemCriada = await enviarOrdemPelaBoleta(page, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  expect(ordemCriada.status).toBe('accepted');
  const exposicaoEsperada = exposicaoAntes.exposure + 10_000;
  await conferirPainelDeExposicao(page, 'PETR4', exposicaoEsperada, LIMITE_DE_EXPOSICAO_POR_SIMBOLO - Math.abs(exposicaoEsperada));
});

test('CA-16 e CA-17: ordem que estoura o limite volta rejeitada com o motivo, entra "Rejeitada" no topo da lista e não muda a exposição', async ({ page }) => {
  // Leva VIIA4 até perto do limite com ordens válidas e grandes; a primeira que
  // não couber mais é a rejeição que o cenário quer ver.
  const leiturasDaExposicao: string[] = [];
  page.on('request', (requisicaoDaPagina) => {
    if (new URL(requisicaoDaPagina.url()).pathname === ROTA_DAS_EXPOSICOES) leiturasDaExposicao.push(requisicaoDaPagina.url());
  });
  let houveRejeicao = false;
  for (let tentativaDeLeitura = 0; tentativaDeLeitura < 6 && !houveRejeicao; tentativaDeLeitura++) {
    const exposicaoAntes = await lerExposicaoNoServidor(page, 'VIIA4');
    const leiturasAntesDoEnvio = leiturasDaExposicao.length;
    const ordemCriada = await enviarOrdemPelaBoleta(page, { simbolo: 'VIIA4', lado: 'Compra', quantidade: '99.999', preco: '999,99' });
    if (ordemCriada.status === 'rejected') {
      houveRejeicao = true;
      expect(ordemCriada.message).toBe('Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.');
      // A rejeição também é gravada: entra no topo da lista com o selo vermelho (CA-11).
      await conferirOrdemNoTopoDaLista(page, ordemCriada, { selo: 'Rejeitada', ativo: 'VIIA4', lado: 'Compra', quantidade: '99.999', preco: formatadorDeReais.format(999.99) });
      // RF-31: depois da rejeição a tela relê a exposição (uma leitura nova) e o valor lido não mudou.
      await expect.poll(() => leiturasDaExposicao.length).toBe(leiturasAntesDoEnvio + 1);
      await conferirPainelDeExposicao(page, 'VIIA4', exposicaoAntes.exposure, exposicaoAntes.remaining);
      expect(await lerExposicaoNoServidor(page, 'VIIA4')).toEqual(exposicaoAntes);
    } else {
      expect(ordemCriada.status).toBe('accepted');
      await conferirOrdemNoTopoDaLista(page, ordemCriada, { selo: 'Aceita', ativo: 'VIIA4', lado: 'Compra', quantidade: '99.999', preco: formatadorDeReais.format(999.99) });
    }
  }
  expect(houveRejeicao).toBe(true);
});

test('RF-24: enquanto a ordem viaja, o envio fica desabilitado e mostra "Enviando…"', async ({ page }) => {
  // Segura a requisição real por um instante, sem trocar a resposta do servidor.
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, async (criacaoDaOrdemSegurada) => {
    await new Promise((liberarRequisicaoSegurada) => setTimeout(liberarRequisicaoSegurada, 800));
    await criacaoDaOrdemSegurada.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  const respostaDaCriacaoDaOrdem = page.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const botaoDuranteOEnvio = page.getByRole('button', { name: 'Enviando…' });
  await expect(botaoDuranteOEnvio).toBeDisabled();
  const ordemCriada = readCreatedOrderFromDataMessage(await (await respostaDaCriacaoDaOrdem).json());
  await expect(celulaDaOrdemNoTopo(page, 'identificador-do-envio')).toHaveText(ordemCriada.clOrdId);
  await expect(seloDaOrdemNoTopo(page)).toHaveText('Aceita');
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
});

test('RNF-08: a exposição é lida ao abrir e depois de cada envio, sem leitura contínua', async ({ context }) => {
  // Página nova, com o contador ligado antes do primeiro carregamento: a do beforeEach
  // pode ter uma leitura ainda em voo e contaria duas vezes.
  const paginaContada = await context.newPage();
  const leiturasDaExposicao: string[] = [];
  paginaContada.on('request', (requisicaoDaPagina) => {
    if (new URL(requisicaoDaPagina.url()).pathname === ROTA_DAS_EXPOSICOES) leiturasDaExposicao.push(requisicaoDaPagina.url());
  });
  await paginaContada.goto('/');
  await expect(paginaContada.getByTestId('exposicao-PETR4')).toBeVisible();
  await paginaContada.waitForTimeout(3_000);
  expect(leiturasDaExposicao).toHaveLength(1);
  const ordemCriada = await enviarOrdemPelaBoleta(paginaContada, { simbolo: 'VALE3', lado: 'Compra', quantidade: '10', preco: '10,00' });
  await expect(celulaDaOrdemNoTopo(paginaContada, 'identificador-do-envio')).toHaveText(ordemCriada.clOrdId);
  await expect(seloDaOrdemNoTopo(paginaContada)).toHaveText('Aceita');
  await paginaContada.waitForTimeout(3_000);
  expect(leiturasDaExposicao).toHaveLength(2);
});

test('regressão: uma leitura antiga e lenta da exposição não apaga a leitura feita depois do envio', async ({ context, page }) => {
  const exposicaoAntes = await lerExposicaoNoServidor(page, 'PETR4');
  const paginaComLeituraLenta = await context.newPage();
  let primeiraLeituraJaSegurada = false;
  // A primeira leitura busca o valor real na hora, mas só o entrega 3 s depois, já com a ordem aceita.
  await paginaComLeituraLenta.route('**' + ROTA_DAS_EXPOSICOES, async (leituraDaExposicao) => {
    if (primeiraLeituraJaSegurada) return leituraDaExposicao.continue();
    primeiraLeituraJaSegurada = true;
    const respostaDeAntesDaOrdem = await leituraDaExposicao.fetch();
    await new Promise((liberarLeituraSegurada) => setTimeout(liberarLeituraSegurada, 3_000));
    await leituraDaExposicao.fulfill({ response: respostaDeAntesDaOrdem });
  });
  await paginaComLeituraLenta.goto('/');
  const ordemCriada = await enviarOrdemPelaBoleta(paginaComLeituraLenta, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  expect(ordemCriada.status).toBe('accepted');
  await paginaComLeituraLenta.waitForTimeout(3_500);
  const exposicaoEsperada = exposicaoAntes.exposure + 10_000;
  await conferirPainelDeExposicao(paginaComLeituraLenta, 'PETR4', exposicaoEsperada, LIMITE_DE_EXPOSICAO_POR_SIMBOLO - Math.abs(exposicaoEsperada));
});

test('RNF-02: cada número da tela usa algarismos tabulares', async ({ page }) => {
  const ordemCriada = await enviarOrdemPelaBoleta(page, { simbolo: 'VALE3', lado: 'Compra', quantidade: '300', preco: '12,34' });
  await expect(celulaDaOrdemNoTopo(page, 'identificador-do-envio')).toHaveText(ordemCriada.clOrdId);
  const numerosDaTela = [
    page.getByLabel(/^Quantidade de/),
    page.getByLabel('Preço por ação (R$)'),
    page.locator('.resumo-linha').filter({ hasText: 'Preço por ação' }).locator('dd'),
    page.getByTestId('total-estimado'),
    celulaDaOrdemNoTopo(page, 'quantidade'),
    celulaDaOrdemNoTopo(page, 'preco'),
    celulaDaOrdemNoTopo(page, 'numero-da-ordem'),
    celulaDaOrdemNoTopo(page, 'identificador-do-envio'),
    ...['PETR4', 'VALE3', 'VIIA4'].flatMap((simboloDoPainel) => [
      page.getByTestId('exposicao-' + simboloDoPainel).getByTestId('exposicao-atual'),
      page.getByTestId('exposicao-' + simboloDoPainel).getByTestId('exposicao-restante'),
    ]),
  ];
  for (const numeroDaTela of numerosDaTela) {
    await expect(numeroDaTela).toHaveCount(1);
    await expect(numeroDaTela).toHaveCSS('font-variant-numeric', 'tabular-nums');
  }
});

test('RF-25 e CA-15: o 400 de validação do servidor aparece na faixa de Compra/Venda como "Não enviada", com a mensagem de cada campo', async ({ page }) => {
  // O 400 só nasce de uma ordem que a tela já recusaria; a resposta abaixo é o corpo do contrato §1, linha "Campo inválido".
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, (criacaoDaOrdem) =>
    criacaoDaOrdem.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'urn:base-investimentos:problem:invalid-order', title: 'Dados inválidos', status: 400, detail: 'A ordem tem campos inválidos.', success: false, statusResultado: 'InvalidInput', errors: ['O preço deve ser múltiplo de 0,01.'] }),
    }),
  );
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const faixaDaFalha = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('faixa-da-falha-no-envio');
  await expect(faixaDaFalha.getByTestId('status-da-ordem')).toHaveText('Não enviada');
  await expect(faixaDaFalha.getByTestId('status-da-ordem')).toHaveCSS('color', COR_DO_STATUS_REJEITADO);
  await expect(faixaDaFalha.getByTestId('mensagem-da-ordem')).toHaveText('A ordem tem campos inválidos.');
  await expect(faixaDaFalha.getByTestId('erros-de-campo-da-ordem').getByRole('listitem')).toHaveText(['O preço deve ser múltiplo de 0,01.']);
});

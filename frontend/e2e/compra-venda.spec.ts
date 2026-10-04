import { expect, test, type Locator, type Page } from '@playwright/test';
import path from 'node:path';
import { ROTA_DAS_EXPOSICOES, ROTA_DAS_ORDENS, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Fala com o OrderGenerator e o OrderAccumulator de verdade. Os cenários que precisam do banco
// vazio apagam tudo antes pela rota DELETE /api/orders do contrato.

const PASTA_DAS_PROVAS = process.env.F6_PASTA_DAS_PROVAS ?? path.resolve('test-results', 'provas-compra-venda');
const TEXTO_DA_LISTA_VAZIA = 'Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.';
const COLUNAS_DA_LISTA = ['Data', 'Status', 'Ativo', 'Lado', 'Quantidade', 'Preço', 'Número da ordem', 'Identificador do envio'];
const COR_DO_SELO_ACEITA = { fundo: 'rgba(79, 227, 176, 0.13)', texto: 'rgb(111, 235, 192)' };
const COR_DO_SELO_REJEITADA = { fundo: 'rgba(255, 138, 122, 0.12)', texto: 'rgb(255, 164, 151)' };
const COR_DO_SELO_ENVIANDO = { fundo: 'rgba(242, 184, 75, 0.14)', texto: 'rgb(244, 197, 106)' };
const formatadorDeReais = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });

type OrdemGravadaNoServidor = { receivedAt: string; status: string; symbol: string | null; side: string | null; quantity: number; price: number; orderId: string; clOrdId: string };
type PaginaDeOrdensNoServidor = { page: number; pageSize: number; total: number; orders: OrdemGravadaNoServidor[] };
type OrdemDaBoleta = { lado: 'Compra' | 'Venda'; quantidade: string; preco: string };

async function apagarTodasAsOrdensNoServidor(paginaDaBoleta: Page) {
  const respostaDoApagamento = await paginaDaBoleta.request.delete(ROTA_DAS_ORDENS);
  expect(respostaDoApagamento.status()).toBe(204);
}

async function lerPrimeiraPaginaNoServidor(paginaDaBoleta: Page): Promise<PaginaDeOrdensNoServidor> {
  const respostaDaLista = await paginaDaBoleta.request.get(ROTA_DAS_ORDENS + '?page=1');
  expect(respostaDaLista.status()).toBe(200);
  return (await respostaDaLista.json()) as PaginaDeOrdensNoServidor;
}

async function enviarOrdemPelaBoleta(paginaDaBoleta: Page, ordemDaBoleta: OrdemDaBoleta): Promise<{ clOrdId: string; status: string }> {
  await paginaDaBoleta.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: ordemDaBoleta.lado }).click();
  await paginaDaBoleta.getByLabel(/^Quantidade de/).fill(ordemDaBoleta.quantidade);
  await paginaDaBoleta.getByLabel('Preço por ação (R$)').fill(ordemDaBoleta.preco);
  const respostaDaCriacao = paginaDaBoleta.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  const releituraDaLista = paginaDaBoleta.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'GET' && new URL(respostaHttp.url()).pathname === ROTA_DAS_ORDENS);
  await paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ }).click();
  const corpoDaCriacao = (await (await respostaDaCriacao).json()) as { clOrdId: string; status: string };
  await releituraDaLista;
  await expect(paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
  return corpoDaCriacao;
}

function cartaoCompraVenda(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('region', { name: 'Compra/Venda' });
}

function linhasDaLista(paginaDaBoleta: Page) {
  return cartaoCompraVenda(paginaDaBoleta).getByTestId('linha-da-ordem');
}

function celulaDaLinha(linhaDaOrdem: Locator, colunaDaLinha: string) {
  return linhaDaOrdem.locator(`td[data-coluna="${colunaDaLinha}"]`);
}

function diaMesAnoDeBrasilia(instanteEmIsoUtc: string) {
  return new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(instanteEmIsoUtc));
}

function horaMinutoDeBrasilia(instanteEmIsoUtc: string) {
  return new Intl.DateTimeFormat('pt-BR', { timeZone: 'America/Sao_Paulo', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(new Date(instanteEmIsoUtc));
}

async function conferirLinhaContraOServidor(linhaDaOrdem: Locator, ordemNoServidor: OrdemGravadaNoServidor, ordemEsperada: { ativo: string; lado: string; quantidade: string; preco: string; selo: 'Aceita' | 'Rejeitada' }) {
  await expect(celulaDaLinha(linhaDaOrdem, 'data').locator('.linha-da-ordem-dia')).toHaveText(diaMesAnoDeBrasilia(ordemNoServidor.receivedAt));
  await expect(celulaDaLinha(linhaDaOrdem, 'data').locator('.linha-da-ordem-hora')).toHaveText(horaMinutoDeBrasilia(ordemNoServidor.receivedAt));
  await expect(celulaDaLinha(linhaDaOrdem, 'data').locator('.linha-da-ordem-hora')).toHaveText(/^\d{2}:\d{2}$/);
  await expect(celulaDaLinha(linhaDaOrdem, 'data')).not.toContainText('agora');
  const seloDaLinha = celulaDaLinha(linhaDaOrdem, 'status').locator('.selo-da-ordem');
  await expect(seloDaLinha).toHaveText(ordemEsperada.selo);
  const corEsperada = ordemEsperada.selo === 'Aceita' ? COR_DO_SELO_ACEITA : COR_DO_SELO_REJEITADA;
  await expect(seloDaLinha).toHaveCSS('background-color', corEsperada.fundo);
  await expect(seloDaLinha).toHaveCSS('color', corEsperada.texto);
  await expect(celulaDaLinha(linhaDaOrdem, 'ativo')).toHaveText(ordemEsperada.ativo);
  await expect(celulaDaLinha(linhaDaOrdem, 'lado')).toHaveText(ordemEsperada.lado);
  await expect(celulaDaLinha(linhaDaOrdem, 'quantidade')).toHaveText(ordemEsperada.quantidade);
  await expect(celulaDaLinha(linhaDaOrdem, 'preco')).toHaveText(ordemEsperada.preco);
  await expect(celulaDaLinha(linhaDaOrdem, 'numero-da-ordem')).toHaveText(ordemNoServidor.orderId);
  await expect(celulaDaLinha(linhaDaOrdem, 'identificador-do-envio')).toHaveText(ordemNoServidor.clOrdId);
  await expect(celulaDaLinha(linhaDaOrdem, 'identificador-do-envio')).toHaveText(/^[0-9a-f]{32}$/i);
}

test('CA-8: o cartão da esquerda se chama "Compra/Venda" e os textos antigos não aparecem', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 2, name: 'Compra/Venda' })).toBeVisible();
  await expect(page.getByText('Resposta da ordem')).toHaveCount(0);
  await expect(page.getByText('Minha Carteira')).toHaveCount(0);
});

test('CA-14: sem ordens no banco, o cartão mostra o vazio com ícone e texto, sem tabela', async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const listaVazia = cartaoCompraVenda(page).getByTestId('lista-de-ordens-vazia');
  await expect(listaVazia).toHaveText(TEXTO_DA_LISTA_VAZIA);
  await expect(listaVazia.locator('svg')).toHaveCount(1);
  await expect(listaVazia.locator('svg')).toBeVisible();
  await expect(cartaoCompraVenda(page).getByRole('table')).toHaveCount(0);
  await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '06-vazio-1440.png'), fullPage: true });
});

test('CA-11 e CA-36: duas ordens enviadas aparecem no topo, a mais nova em cima, com os 8 campos e os selos', async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
  try {
    await page.goto('/');
    await expect(cartaoCompraVenda(page).getByTestId('lista-de-ordens-vazia')).toBeVisible();
    // Com a exposição zerada, a primeira compra grande cabe no limite e a segunda não: aceita e depois rejeitada.
    const primeiroEnvio = await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '99.999', preco: '999,99' });
    const segundoEnvio = await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '99.999', preco: '999,99' });
    expect(primeiroEnvio.status).toBe('accepted');
    expect(segundoEnvio.status).toBe('rejected');

    const paginaNoServidor = await lerPrimeiraPaginaNoServidor(page);
    expect(paginaNoServidor.orders.map((ordemNoServidor) => ordemNoServidor.clOrdId)).toEqual([segundoEnvio.clOrdId, primeiroEnvio.clOrdId]);
    await expect(cartaoCompraVenda(page).locator('thead th')).toHaveText(COLUNAS_DA_LISTA);
    await expect(linhasDaLista(page)).toHaveCount(2);
    const precoEsperado = formatadorDeReais.format(999.99);
    await conferirLinhaContraOServidor(linhasDaLista(page).nth(0), paginaNoServidor.orders[0], { ativo: 'PETR4', lado: 'Compra', quantidade: '99.999', preco: precoEsperado, selo: 'Rejeitada' });
    await conferirLinhaContraOServidor(linhasDaLista(page).nth(1), paginaNoServidor.orders[1], { ativo: 'PETR4', lado: 'Compra', quantidade: '99.999', preco: precoEsperado, selo: 'Aceita' });
  } finally {
    // A compra grande deixa PETR4 quase no limite; zerar de novo impede que as specs seguintes vejam rejeição.
    await apagarTodasAsOrdensNoServidor(page);
  }
});

test('CA-12: a ordem nova entra no topo sem recarregar e continua lá depois de recarregar', async ({ page }) => {
  await page.goto('/');
  const envioDaVenda = await enviarOrdemPelaBoleta(page, { lado: 'Venda', quantidade: '7', preco: '12,34' });
  const linhaDoTopo = linhasDaLista(page).first();
  await expect(celulaDaLinha(linhaDoTopo, 'identificador-do-envio')).toHaveText(envioDaVenda.clOrdId);
  await expect(celulaDaLinha(linhaDoTopo, 'lado')).toHaveText('Venda');
  await expect(celulaDaLinha(linhaDoTopo, 'quantidade')).toHaveText('7');
  await expect(celulaDaLinha(linhaDoTopo, 'preco')).toHaveText(formatadorDeReais.format(12.34));
  await page.reload();
  await expect(celulaDaLinha(linhasDaLista(page).first(), 'identificador-do-envio')).toHaveText(envioDaVenda.clOrdId);
});

test('CA-15: erro de preenchimento (400) aparece na faixa vermelha com os erros por campo, sem linha nova, e some no envio seguinte', async ({ page }) => {
  await page.goto('/');
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '1', preco: '10,00' });
  const totalAntes = (await lerPrimeiraPaginaNoServidor(page)).total;
  const identificadorDoTopoAntes = await celulaDaLinha(linhasDaLista(page).first(), 'identificador-do-envio').textContent();
  // O 400 só nasce de uma ordem que a tela já recusaria; a resposta é o corpo do contrato §1, "Campo inválido".
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, (criacaoDaOrdem) =>
    criacaoDaOrdem.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({ status: 'validation_error', message: 'A ordem tem campos inválidos.', errors: [{ field: 'price', message: 'O preço deve ser múltiplo de 0,01.' }] }),
    }),
  );
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '10', preco: '10,00' });
  const faixaDaFalha = cartaoCompraVenda(page).getByTestId('faixa-da-falha-no-envio');
  await expect(faixaDaFalha).toBeVisible();
  await expect(faixaDaFalha.getByTestId('status-da-ordem')).toHaveText('Não enviada');
  await expect(faixaDaFalha.getByTestId('mensagem-da-ordem')).toHaveText('A ordem tem campos inválidos.');
  await expect(faixaDaFalha.getByTestId('erros-de-campo-da-ordem').getByRole('listitem')).toHaveText(['O preço deve ser múltiplo de 0,01.']);
  await expect(faixaDaFalha).toHaveCSS('background-color', COR_DO_SELO_REJEITADA.fundo);
  await expect(faixaDaFalha.getByTestId('status-da-ordem')).toHaveCSS('color', COR_DO_SELO_REJEITADA.texto);
  // A faixa fica no topo do cartão, logo abaixo do título e antes da tabela.
  const topoDaFaixa = (await faixaDaFalha.boundingBox())!.y;
  const topoDaTabela = (await cartaoCompraVenda(page).getByRole('table').boundingBox())!.y;
  expect(topoDaFaixa).toBeLessThan(topoDaTabela);
  expect((await lerPrimeiraPaginaNoServidor(page)).total).toBe(totalAntes);
  await expect(celulaDaLinha(linhasDaLista(page).first(), 'identificador-do-envio')).toHaveText(identificadorDoTopoAntes!);

  await page.unroute('**' + ROTA_DE_CRIACAO_DE_ORDEM);
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '1', preco: '10,00' });
  await expect(faixaDaFalha).toHaveCount(0);
});

test('CA-15: sem comunicação com o servidor (503) a faixa mostra a mensagem e a lista não ganha linha', async ({ page }) => {
  await page.goto('/');
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '1', preco: '10,00' });
  const totalAntes = (await lerPrimeiraPaginaNoServidor(page)).total;
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, (criacaoDaOrdem) =>
    criacaoDaOrdem.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 'communication_error', message: 'Não foi possível falar com o OrderAccumulator.' }) }),
  );
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '10', preco: '10,00' });
  const faixaDaFalha = cartaoCompraVenda(page).getByTestId('faixa-da-falha-no-envio');
  await expect(faixaDaFalha.getByTestId('status-da-ordem')).toHaveText('Erro de comunicação');
  await expect(faixaDaFalha.getByTestId('mensagem-da-ordem')).toHaveText('A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.');
  await expect(faixaDaFalha.getByTestId('erros-de-campo-da-ordem')).toHaveCount(0);
  expect((await lerPrimeiraPaginaNoServidor(page)).total).toBe(totalAntes);
});

test('CA-16: enquanto a ordem viaja, o cartão mostra o selo âmbar "Enviando…" e o botão fica desligado', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, async (criacaoDaOrdemSegurada) => {
    await new Promise((liberarRequisicaoSegurada) => setTimeout(liberarRequisicaoSegurada, 1_500));
    await criacaoDaOrdemSegurada.continue();
  });
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const seloEnviando = cartaoCompraVenda(page).getByTestId('selo-enviando');
  await expect(seloEnviando).toHaveText('Enviando…');
  await expect(seloEnviando).toHaveCSS('background-color', COR_DO_SELO_ENVIANDO.fundo);
  await expect(seloEnviando).toHaveCSS('color', COR_DO_SELO_ENVIANDO.texto);
  await expect(page.getByRole('button', { name: 'Enviando…' })).toBeDisabled();
  await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '06-enviando-1440.png') });
  await expect(seloEnviando).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Enviar ordem de compra' })).toBeEnabled();
});

test('CA-41 e CA-34: ao abrir, a lista é lida uma vez com page=1; parada 30 s, nenhuma leitura nova', async ({ context }) => {
  test.setTimeout(60_000);
  const paginaContada = await context.newPage();
  const leiturasDaLista: string[] = [];
  const leiturasDaExposicao: string[] = [];
  paginaContada.on('request', (requisicaoDaPagina) => {
    const caminhoDaRequisicao = new URL(requisicaoDaPagina.url()).pathname;
    if (caminhoDaRequisicao === ROTA_DAS_ORDENS && requisicaoDaPagina.method() === 'GET') leiturasDaLista.push(requisicaoDaPagina.url());
    if (caminhoDaRequisicao === ROTA_DAS_EXPOSICOES) leiturasDaExposicao.push(requisicaoDaPagina.url());
  });
  await paginaContada.goto('/');
  await expect(cartaoCompraVenda(paginaContada).getByRole('table').or(cartaoCompraVenda(paginaContada).getByTestId('lista-de-ordens-vazia'))).toBeVisible();
  expect(leiturasDaLista).toHaveLength(1);
  expect(new URL(leiturasDaLista[0]).search).toBe('?page=1');
  const leiturasDaListaAoAbrir = leiturasDaLista.length;
  const leiturasDaExposicaoAoAbrir = leiturasDaExposicao.length;
  await paginaContada.waitForTimeout(30_000);
  expect(leiturasDaLista).toHaveLength(leiturasDaListaAoAbrir);
  expect(leiturasDaExposicao).toHaveLength(leiturasDaExposicaoAoAbrir);
});

test('CA-11 e RNF-01: em 1440 px os códigos de 32 letras aparecem inteiros, cada ordem numa linha, nas cores da maquete', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await enviarOrdemPelaBoleta(page, { lado: 'Compra', quantidade: '1', preco: '10,00' });
  const linhaDoTopo = linhasDaLista(page).first();
  for (const colunaDeCodigo of ['numero-da-ordem', 'identificador-do-envio']) {
    const celulaDeCodigo = celulaDaLinha(linhaDoTopo, colunaDeCodigo);
    await expect(celulaDeCodigo).toHaveCSS('white-space', 'nowrap');
    const codigoCabeInteiro = await celulaDeCodigo.evaluate((celulaNaPagina) => celulaNaPagina.scrollWidth <= celulaNaPagina.clientWidth);
    expect(codigoCabeInteiro, colunaDeCodigo).toBe(true);
  }
  const molduraDaTabela = cartaoCompraVenda(page).locator('.tabela-de-ordens-moldura');
  const tabelaCabeSemRolar = await molduraDaTabela.evaluate((molduraNaPagina) => molduraNaPagina.scrollWidth <= molduraNaPagina.clientWidth);
  expect(tabelaCabeSemRolar).toBe(true);
  await expect(molduraDaTabela).toHaveCSS('background-color', 'rgb(11, 23, 25)');
  const cabecalhoDaData = cartaoCompraVenda(page).locator('thead th').first();
  // Em 1440 px vale a tabela da maquete: cabeçalho à vista e a ordem numa linha só, sem rótulo por campo.
  await expect(cabecalhoDaData).toBeVisible();
  await expect(linhaDoTopo).toHaveCSS('display', 'table-row');
  await expect(cabecalhoDaData).toHaveCSS('font-size', '11px');
  await expect(cabecalhoDaData).toHaveCSS('text-transform', 'uppercase');
  await expect(cabecalhoDaData).toHaveCSS('color', 'rgb(143, 163, 161)');
  const celulaDoAtivo = celulaDaLinha(linhaDoTopo, 'ativo');
  await expect(celulaDoAtivo).toHaveCSS('background-color', 'rgb(14, 27, 30)');
  await linhaDoTopo.hover();
  await expect(celulaDoAtivo).toHaveCSS('background-color', 'rgb(18, 36, 39)');
  const seloDoTopo = celulaDaLinha(linhaDoTopo, 'status').locator('.selo-da-ordem');
  await expect(seloDoTopo).toHaveCSS('font-size', '12px');
  await expect(seloDoTopo).toHaveCSS('font-weight', '800');
  await expect(seloDoTopo).toHaveCSS('padding', '6px 11px');
  await expect(seloDoTopo).toHaveCSS('border-radius', '999px');
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '06-lista-1440.png'), fullPage: true });
});

const ROTULOS_DOS_CAMPOS_DA_ORDEM = [
  ['data', 'Data'], ['status', 'Status'], ['ativo', 'Ativo'], ['lado', 'Lado'], ['quantidade', 'Quantidade'],
  ['preco', 'Preço'], ['numero-da-ordem', 'Número da ordem'], ['identificador-do-envio', 'Identificador do envio'],
] as const;
const QUANTIDADE_DE_ORDENS_DA_LISTA_CHEIA = 10;

// Página 1 cheia com os valores mais largos que a boleta aceita (99.999 a R$ 999,99). Compra e venda se
// alternam, então a exposição volta a zero e as specs seguintes não herdam ativo perto do limite.
async function gravarListaCheiaDeOrdensLargas(paginaDaBoleta: Page) {
  await apagarTodasAsOrdensNoServidor(paginaDaBoleta);
  for (let posicaoDaOrdem = 0; posicaoDaOrdem < QUANTIDADE_DE_ORDENS_DA_LISTA_CHEIA; posicaoDaOrdem++) {
    const ordemGravada = await paginaDaBoleta.request.post(ROTA_DE_CRIACAO_DE_ORDEM, {
      data: { symbol: 'PETR4', side: posicaoDaOrdem % 2 === 0 ? 'buy' : 'sell', quantity: 99_999, price: 999.99 },
    });
    expect(ordemGravada.status()).toBe(200);
  }
}

test.describe('lista cheia de ordens largas', () => {
  test.beforeEach(async ({ page }) => {
    await gravarListaCheiaDeOrdensLargas(page);
  });

  test.afterEach(async ({ page }) => {
    await apagarTodasAsOrdensNoServidor(page);
  });

  for (const larguraDaJanela of [375, 860, 1440, 1920]) {
    test(`RNF-02: em ${larguraDaJanela} px, com a página 1 cheia de valores largos, a página não rola para o lado`, async ({ page }) => {
      await page.setViewportSize({ width: larguraDaJanela, height: 900 });
      await page.goto('/');
      await expect(linhasDaLista(page)).toHaveCount(QUANTIDADE_DE_ORDENS_DA_LISTA_CHEIA);
      await expect(celulaDaLinha(linhasDaLista(page).first(), 'quantidade')).toHaveText('99.999');
      await expect(celulaDaLinha(linhasDaLista(page).first(), 'preco')).toHaveText(formatadorDeReais.format(999.99));
      const paginaCabeSemRolarParaOLado = await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);
      expect(paginaCabeSemRolarParaOLado).toBe(true);
      if (larguraDaJanela === 375) await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '06-lista-375.png'), fullPage: true });
    });
  }

  for (const larguraDaJanela of [375, 860, 1280]) {
    test(`CA-26 e ASSUMI-04: em ${larguraDaJanela} px cada ordem vira um bloco com os 8 campos rotulados, todos à vista dentro do cartão`, async ({ page }) => {
      await page.setViewportSize({ width: larguraDaJanela, height: 900 });
      await page.goto('/');
      await expect(linhasDaLista(page)).toHaveCount(QUANTIDADE_DE_ORDENS_DA_LISTA_CHEIA);
      // No modo em blocos o cabeçalho da tabela sai de cena: cada campo leva o próprio rótulo.
      await expect(cartaoCompraVenda(page).locator('thead')).toBeHidden();
      const caixaDoCartao = (await cartaoCompraVenda(page).boundingBox())!;
      for (let posicaoDaLinha = 0; posicaoDaLinha < QUANTIDADE_DE_ORDENS_DA_LISTA_CHEIA; posicaoDaLinha++) {
        const linhaDaOrdem = linhasDaLista(page).nth(posicaoDaLinha);
        await expect(linhaDaOrdem, `linha ${posicaoDaLinha + 1}`).toHaveCSS('display', 'block');
        for (const [colunaDoCampo, rotuloDoCampo] of ROTULOS_DOS_CAMPOS_DA_ORDEM) {
          const celulaDoCampo = celulaDaLinha(linhaDaOrdem, colunaDoCampo);
          const nomeDoCampo = `linha ${posicaoDaLinha + 1} / ${colunaDoCampo}`;
          await expect(celulaDoCampo, nomeDoCampo).toBeVisible();
          expect(await celulaDoCampo.evaluate((celulaNaPagina) => getComputedStyle(celulaNaPagina, '::before').content), nomeDoCampo).toBe(`"${rotuloDoCampo}"`);
          const caixaDoCampo = (await celulaDoCampo.boundingBox())!;
          expect(caixaDoCampo.x, `${nomeDoCampo}: começa dentro do cartão`).toBeGreaterThanOrEqual(caixaDoCartao.x);
          expect(caixaDoCampo.x + caixaDoCampo.width, `${nomeDoCampo}: termina dentro do cartão`).toBeLessThanOrEqual(caixaDoCartao.x + caixaDoCartao.width);
        }
      }
      const molduraCabeSemRolar = await cartaoCompraVenda(page).locator('.tabela-de-ordens-moldura').evaluate((molduraNaPagina) => molduraNaPagina.scrollWidth <= molduraNaPagina.clientWidth);
      expect(molduraCabeSemRolar).toBe(true);
    });
  }
});

test('ASSUMI-01: se a lista não puder ser lida, o cartão avisa o erro no lugar da tabela, sem o vazio', async ({ page }) => {
  // Só a leitura da lista falha; o corpo é o 503 do contrato. A boleta e a exposição seguem reais.
  await page.route('**' + ROTA_DAS_ORDENS + '?page=1', (leituraDaLista) =>
    leituraDaLista.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 'communication_error', message: 'Não foi possível falar com o OrderAccumulator.' }) }),
  );
  await page.goto('/');
  const avisoDeErro = cartaoCompraVenda(page).getByRole('alert');
  await expect(avisoDeErro).toHaveText('Não foi possível ler as ordens agora. Tente de novo em instantes.');
  await expect(avisoDeErro).toHaveCSS('color', COR_DO_SELO_REJEITADA.texto);
  await expect(cartaoCompraVenda(page).getByRole('table')).toHaveCount(0);
  await expect(cartaoCompraVenda(page).getByTestId('lista-de-ordens-vazia')).toHaveCount(0);
});

test('ASSUMI-07: enquanto a primeira leitura não volta, o cartão diz que está carregando as ordens', async ({ page }) => {
  let liberarLeituraDaLista: () => void = () => {};
  const leituraLiberada = new Promise<void>((resolverLeitura) => { liberarLeituraDaLista = resolverLeitura; });
  await page.route('**' + ROTA_DAS_ORDENS + '?page=1', async (leituraDaLista) => {
    await leituraLiberada;
    await leituraDaLista.continue();
  });
  await page.goto('/');
  await expect(cartaoCompraVenda(page).getByText('Carregando as ordens…', { exact: true })).toBeVisible();
  await expect(cartaoCompraVenda(page)).toHaveAttribute('aria-busy', 'true');
  liberarLeituraDaLista();
  await expect(cartaoCompraVenda(page).getByText('Carregando as ordens…', { exact: true })).toHaveCount(0);
  await expect(cartaoCompraVenda(page)).toHaveAttribute('aria-busy', 'false');
});

test('ASSUMI-03: ordem gravada sem símbolo e sem lado (rejeição vinda direto pelo FIX) mostra "—" nos dois campos', async ({ page }) => {
  // Pela tela e pela API não dá para gravar lado nulo; o caso só nasce no FIX, então a leitura devolve o
  // corpo do contrato com symbol e side nulos (formato conferido pela F1 na API candidata).
  await page.route('**' + ROTA_DAS_ORDENS + '?page=1', (leituraDaLista) =>
    leituraDaLista.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ page: 1, pageSize: 10, total: 1, orders: [{ receivedAt: '2026-10-04T23:30:00.123456Z', status: 'rejected', symbol: null, side: null, quantity: 5, price: 1.1, orderId: 'a'.repeat(32), clOrdId: 'b'.repeat(32) }] }),
    }),
  );
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const linhaSemSimboloNemLado = linhasDaLista(page).first();
  await expect(celulaDaLinha(linhaSemSimboloNemLado, 'ativo')).toHaveText('—');
  await expect(celulaDaLinha(linhaSemSimboloNemLado, 'lado')).toHaveText('—');
  await expect(celulaDaLinha(linhaSemSimboloNemLado, 'status').locator('.selo-da-ordem')).toHaveText('Rejeitada');
  await expect(celulaDaLinha(linhaSemSimboloNemLado, 'data').locator('.linha-da-ordem-dia')).toHaveText('04/10/2026');
  await expect(celulaDaLinha(linhaSemSimboloNemLado, 'data').locator('.linha-da-ordem-hora')).toHaveText('20:30');
});

import { expect, test, type Page } from '@playwright/test';
import { ROTA_DAS_EXPOSICOES, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Estes cenários falam com o OrderGenerator e o OrderAccumulator de verdade.
// Os valores de exposição são lidos antes e depois de cada ordem, então o teste
// não depende do banco estar vazio.

const LIMITE_DE_EXPOSICAO_POR_SIMBOLO = 100_000_000;
const COR_DO_STATUS_ACEITO = 'rgb(111, 235, 192)';
const COR_DO_STATUS_REJEITADO = 'rgb(255, 164, 151)';

type OrdemDoTeste = { simbolo: string; lado: 'Compra' | 'Venda'; quantidade: string; preco: string };

async function enviarOrdemPelaBoleta(paginaDaBoleta: Page, ordemDoTeste: OrdemDoTeste) {
  await paginaDaBoleta.getByLabel('Símbolo').selectOption(ordemDoTeste.simbolo);
  await paginaDaBoleta.getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: ordemDoTeste.lado }).click();
  await paginaDaBoleta.getByLabel(/^Quantidade de/).fill(ordemDoTeste.quantidade);
  await paginaDaBoleta.getByLabel('Preço por ação (R$)').fill(ordemDoTeste.preco);
  const respostaDaCriacaoDaOrdem = paginaDaBoleta.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  await paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ }).click();
  await respostaDaCriacaoDaOrdem;
  await expect(paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ })).toBeEnabled();
}

type ExposicaoNoServidor = { symbol: string; exposure: number; remaining: number };

async function lerExposicaoNoServidor(paginaDaBoleta: Page, simboloDaExposicao: string): Promise<ExposicaoNoServidor> {
  const respostaDasExposicoes = await paginaDaBoleta.request.get(ROTA_DAS_EXPOSICOES);
  expect(respostaDasExposicoes.status()).toBe(200);
  const corpoDasExposicoes = (await respostaDasExposicoes.json()) as { exposures: ExposicaoNoServidor[] };
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

function celulaDaResposta(paginaDaBoleta: Page, rotuloDaCelula: string) {
  return paginaDaBoleta
    .locator('.resposta-celula')
    .filter({ has: paginaDaBoleta.locator('dt', { hasText: new RegExp('^' + rotuloDaCelula + '$') }) })
    .locator('dd');
}

async function conferirDadosDaResposta(paginaDaBoleta: Page, respostaEsperada: { ativo: string; lado: string; quantidade: string; preco: string }) {
  await expect(celulaDaResposta(paginaDaBoleta, 'Ativo')).toHaveText(respostaEsperada.ativo);
  await expect(celulaDaResposta(paginaDaBoleta, 'Lado')).toHaveText(respostaEsperada.lado);
  await expect(celulaDaResposta(paginaDaBoleta, 'Quantidade')).toHaveText(respostaEsperada.quantidade);
  await expect(celulaDaResposta(paginaDaBoleta, 'Preço')).toHaveText(respostaEsperada.preco);
  await expect(celulaDaResposta(paginaDaBoleta, 'Identificador do envio')).toHaveText(/^[0-9a-f]{32}$/i);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-14: compra válida é enviada e a tela mostra a resposta aceita', async ({ page }) => {
  await enviarOrdemPelaBoleta(page, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Aceita');
  await expect(page.getByTestId('status-da-ordem')).toHaveCSS('color', COR_DO_STATUS_ACEITO);
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText('Ordem aceita.');
  await conferirDadosDaResposta(page, { ativo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: formatadorDeReais.format(10) });
});

test('CA-14: venda válida é enviada e a tela mostra a resposta aceita', async ({ page }) => {
  await enviarOrdemPelaBoleta(page, { simbolo: 'VALE3', lado: 'Venda', quantidade: '200', preco: '55,30' });
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Aceita');
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText('Ordem aceita.');
  await conferirDadosDaResposta(page, { ativo: 'VALE3', lado: 'Venda', quantidade: '200', preco: formatadorDeReais.format(55.3) });
});

test('CA-17: o painel mostra os três ativos e muda depois de uma ordem aceita', async ({ page }) => {
  for (const simboloDaOrdem of ['PETR4', 'VALE3', 'VIIA4']) {
    const exposicaoAtual = await lerExposicaoNoServidor(page, simboloDaOrdem);
    await conferirPainelDeExposicao(page, simboloDaOrdem, exposicaoAtual.exposure, exposicaoAtual.remaining);
  }
  const exposicaoAntes = await lerExposicaoNoServidor(page, 'PETR4');
  await enviarOrdemPelaBoleta(page, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Aceita');
  const exposicaoEsperada = exposicaoAntes.exposure + 10_000;
  await conferirPainelDeExposicao(page, 'PETR4', exposicaoEsperada, LIMITE_DE_EXPOSICAO_POR_SIMBOLO - Math.abs(exposicaoEsperada));
});

test('CA-16 e CA-17: ordem que estoura o limite aparece rejeitada com o motivo e não muda a exposição', async ({ page }) => {
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
    await enviarOrdemPelaBoleta(page, { simbolo: 'VIIA4', lado: 'Compra', quantidade: '99.999', preco: '999,99' });
    const situacaoNaTela = await page.getByTestId('status-da-ordem').textContent();
    if (situacaoNaTela === 'Rejeitada') {
      houveRejeicao = true;
      await expect(page.getByTestId('status-da-ordem')).toHaveCSS('color', COR_DO_STATUS_REJEITADO);
      await expect(page.getByTestId('mensagem-da-ordem')).toHaveText(
        'Ordem rejeitada: a exposição de VIIA4 passaria do limite de 100.000.000,00.',
      );
      // RF-31: depois da rejeição a tela relê a exposição (uma leitura nova) e o valor lido não mudou.
      await expect.poll(() => leiturasDaExposicao.length).toBe(leiturasAntesDoEnvio + 1);
      await conferirPainelDeExposicao(page, 'VIIA4', exposicaoAntes.exposure, exposicaoAntes.remaining);
      expect(await lerExposicaoNoServidor(page, 'VIIA4')).toEqual(exposicaoAntes);
    } else {
      expect(situacaoNaTela).toBe('Aceita');
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
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  const botaoDuranteOEnvio = page.getByRole('button', { name: 'Enviando…' });
  await expect(botaoDuranteOEnvio).toBeDisabled();
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Aceita');
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
  await enviarOrdemPelaBoleta(paginaContada, { simbolo: 'VALE3', lado: 'Compra', quantidade: '10', preco: '10,00' });
  await expect(paginaContada.getByTestId('status-da-ordem')).toHaveText('Aceita');
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
  await enviarOrdemPelaBoleta(paginaComLeituraLenta, { simbolo: 'PETR4', lado: 'Compra', quantidade: '1.000', preco: '10,00' });
  await expect(paginaComLeituraLenta.getByTestId('status-da-ordem')).toHaveText('Aceita');
  await paginaComLeituraLenta.waitForTimeout(3_500);
  const exposicaoEsperada = exposicaoAntes.exposure + 10_000;
  await conferirPainelDeExposicao(paginaComLeituraLenta, 'PETR4', exposicaoEsperada, LIMITE_DE_EXPOSICAO_POR_SIMBOLO - Math.abs(exposicaoEsperada));
});

test('RNF-02: cada número da tela usa algarismos tabulares', async ({ page }) => {
  await enviarOrdemPelaBoleta(page, { simbolo: 'VALE3', lado: 'Compra', quantidade: '300', preco: '12,34' });
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Aceita');
  const numerosDaTela = [
    page.getByLabel(/^Quantidade de/),
    page.getByLabel('Preço por ação (R$)'),
    page.locator('.resumo-linha').filter({ hasText: 'Preço por ação' }).locator('dd'),
    page.getByTestId('total-estimado'),
    celulaDaResposta(page, 'Quantidade'),
    celulaDaResposta(page, 'Preço'),
    celulaDaResposta(page, 'Número da ordem'),
    celulaDaResposta(page, 'Identificador do envio'),
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

test('RF-25: o 400 de validação do servidor aparece no painel como "Não enviada", com a mensagem de cada campo', async ({ page }) => {
  // O 400 só nasce de uma ordem que a tela já recusaria; a resposta abaixo é o corpo do contrato §1, linha "Campo inválido".
  await page.route('**' + ROTA_DE_CRIACAO_DE_ORDEM, (criacaoDaOrdem) =>
    criacaoDaOrdem.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({
        status: 'validation_error',
        message: 'A ordem tem campos inválidos.',
        errors: [{ field: 'price', message: 'O preço deve ser múltiplo de 0,01.' }],
      }),
    }),
  );
  await page.getByLabel(/^Quantidade de/).fill('10');
  await page.getByLabel('Preço por ação (R$)').fill('10,00');
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  await expect(page.getByTestId('status-da-ordem')).toHaveText('Não enviada');
  await expect(page.getByTestId('status-da-ordem')).toHaveCSS('color', COR_DO_STATUS_REJEITADO);
  await expect(page.getByTestId('mensagem-da-ordem')).toHaveText('A ordem tem campos inválidos.');
  await expect(page.getByTestId('erros-de-campo-da-ordem').getByRole('listitem')).toHaveText(['O preço deve ser múltiplo de 0,01.']);
});

import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import path from 'node:path';
import { MENSAGEM_DE_ORDENS_NAO_APAGADAS, ROTA_DAS_EXPOSICOES, ROTA_DAS_ORDENS, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Fala com o OrderGenerator e o OrderAccumulator de verdade. Cada teste começa e termina com o banco vazio,
// e só os cenários de falha trocam a resposta do DELETE na rede.

const PASTA_DAS_PROVAS = process.env.F8_PASTA_DAS_PROVAS ?? path.resolve('test-results', 'provas-deletar-tudo');
const TEXTO_DA_JANELA = 'Todas as ordens serão apagadas e a exposição de PETR4, VALE3 e VIIA4 volta para R$ 0,00. Isso não pode ser desfeito.';
const TEXTO_DA_LISTA_VAZIA = 'Nenhuma ordem enviada ainda. Preencha a boleta e envie para ver a resposta aqui.';
const COR_CORAL = 'rgb(255, 164, 151)';
const COR_DO_TEXTO_SOBRE_CORAL = 'rgb(42, 14, 10)';
const COR_DA_BORDA_CORAL_TRANSLUCIDA = 'rgba(255, 164, 151, 0.38)';
const COR_DO_FUNDO_DO_CANCELAR = 'rgb(11, 23, 25)';
const SIMBOLOS_DA_EXPOSICAO = ['PETR4', 'VALE3', 'VIIA4'];
const ORDENS_DE_COMPRA_DO_TESTE = [
  { symbol: 'PETR4', side: 'buy', quantity: 100, price: 10 },
  { symbol: 'VALE3', side: 'buy', quantity: 200, price: 5 },
  { symbol: 'VIIA4', side: 'buy', quantity: 300, price: 2 },
];

type ExposicaoNoServidor = { symbol: string; exposure: number; remaining: number };

async function apagarTodasAsOrdensNoServidor(paginaDaBoleta: Page) {
  const respostaDoApagamento = await paginaDaBoleta.request.delete(ROTA_DAS_ORDENS);
  expect(respostaDoApagamento.status()).toBe(204);
}

async function criarOrdensDeCompraPelaApi(paginaDaBoleta: Page) {
  for (const ordemDeCompra of ORDENS_DE_COMPRA_DO_TESTE) {
    const respostaDaCriacao = await paginaDaBoleta.request.post(ROTA_DE_CRIACAO_DE_ORDEM, { data: ordemDeCompra });
    expect(respostaDaCriacao.status()).toBe(200);
    expect(((await respostaDaCriacao.json()) as { status: string }).status).toBe('accepted');
  }
}

async function contarOrdensNoServidor(paginaDaBoleta: Page) {
  const respostaDaLista = await paginaDaBoleta.request.get(ROTA_DAS_ORDENS + '?page=1');
  expect(respostaDaLista.status()).toBe(200);
  return ((await respostaDaLista.json()) as { total: number }).total;
}

async function lerExposicoesNoServidor(paginaDaBoleta: Page) {
  const respostaDasExposicoes = await paginaDaBoleta.request.get(ROTA_DAS_EXPOSICOES);
  expect(respostaDasExposicoes.status()).toBe(200);
  return ((await respostaDasExposicoes.json()) as { exposures: ExposicaoNoServidor[] }).exposures;
}

// Com a página em 90%, o navegador arredonda a medida calculada (38px vira 37.9861px); a tolerância é de cinco centésimos de pixel.
async function conferirMedidaEmPxDeCss(elementoDaTela: Locator, propriedadeCss: string, medidaEsperadaEmPx: number) {
  await expect
    .poll(
      async () =>
        parseFloat(await elementoDaTela.evaluate((elementoNaPagina, nomeDaPropriedade) => getComputedStyle(elementoNaPagina).getPropertyValue(nomeDaPropriedade), propriedadeCss)),
      { message: propriedadeCss },
    )
    .toBeCloseTo(medidaEsperadaEmPx, 1);
}

// Traços de IconeLixeira e IconeAlerta em Icones.tsx: o ícone certo se prova pelo desenho, não pela contagem de svg.
const DESENHO_DO_ICONE_LIXEIRA = ['M4 7h16', 'M10 11v6M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'];
const DESENHO_DO_ICONE_ALERTA = ['M10.3 3.9L2.4 17.5a2 2 0 001.7 3h15.8a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', 'M12 9v4', 'M12 17h.01'];

async function conferirDesenhoDoIcone(elementoComIcone: Locator, tracosEsperados: string[]) {
  await expect(elementoComIcone.locator('svg')).toHaveCount(1);
  await expect(elementoComIcone.locator('svg')).toBeVisible();
  await expect(elementoComIcone.locator('svg')).toHaveAttribute('aria-hidden', 'true');
  const tracosDoIcone = await elementoComIcone.locator('svg path').evaluateAll((caminhosDoSvg) => caminhosDoSvg.map((caminhoDoSvg) => caminhoDoSvg.getAttribute('d')));
  expect(tracosDoIcone).toEqual(tracosEsperados);
}

function ehChamadaDaApi(requisicaoDaTela: Request, metodoHttp: string, rotaDaApi: string) {
  return requisicaoDaTela.method() === metodoHttp && new URL(requisicaoDaTela.url()).pathname === rotaDaApi;
}

function localizarCartaoCompraVenda(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('region', { name: 'Compra/Venda' });
}

function localizarBotaoDeletarTudoDoCabecalho(paginaDaBoleta: Page) {
  return localizarCartaoCompraVenda(paginaDaBoleta).getByRole('button', { name: 'Deletar tudo' });
}

function localizarJanelaDeConfirmacao(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('dialog', { name: 'Deletar todos os dados?' });
}

function localizarLinhasDaListaDeOrdens(paginaDaBoleta: Page) {
  return localizarCartaoCompraVenda(paginaDaBoleta).getByTestId('linha-da-ordem');
}

async function abrirTelaComAsOrdensDoTeste(paginaDaBoleta: Page) {
  await criarOrdensDeCompraPelaApi(paginaDaBoleta);
  await paginaDaBoleta.goto('/');
  await expect(localizarLinhasDaListaDeOrdens(paginaDaBoleta)).toHaveCount(ORDENS_DE_COMPRA_DO_TESTE.length);
  await expect(paginaDaBoleta.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).toHaveText('R$ 1.000,00');
}

async function lerExposicoesNaTela(paginaDaBoleta: Page) {
  const exposicoesNaTela: string[] = [];
  for (const simboloDaExposicao of SIMBOLOS_DA_EXPOSICAO) {
    const cartaoDoAtivo = paginaDaBoleta.getByTestId('exposicao-' + simboloDaExposicao);
    exposicoesNaTela.push(
      `${simboloDaExposicao} ${await cartaoDoAtivo.getByTestId('exposicao-atual').innerText()} ${await cartaoDoAtivo.getByTestId('exposicao-restante').innerText()}`,
    );
  }
  return exposicoesNaTela;
}

async function conferirTelaZerada(paginaDaBoleta: Page) {
  await expect(localizarCartaoCompraVenda(paginaDaBoleta).getByTestId('lista-de-ordens-vazia')).toHaveText(TEXTO_DA_LISTA_VAZIA);
  await expect(localizarLinhasDaListaDeOrdens(paginaDaBoleta)).toHaveCount(0);
  await expect(paginaDaBoleta.getByRole('group', { name: 'Páginas da lista de ordens' })).toHaveCount(0);
  for (const simboloDaExposicao of SIMBOLOS_DA_EXPOSICAO) {
    const cartaoDoAtivo = paginaDaBoleta.getByTestId('exposicao-' + simboloDaExposicao);
    await expect(cartaoDoAtivo.getByTestId('exposicao-atual'), simboloDaExposicao).toHaveText('R$ 0,00');
    await expect(cartaoDoAtivo.getByTestId('exposicao-restante'), simboloDaExposicao).toHaveText('R$ 100.000.000,00');
    await expect(cartaoDoAtivo.getByTestId('uso-do-limite-porcentagem'), simboloDaExposicao).toHaveText('0%');
  }
}

// Marca a página para provar que a tela não recarregou: um recarregamento apaga a marca.
async function marcarPaginaSemRecarregar(paginaDaBoleta: Page) {
  await paginaDaBoleta.evaluate(() => {
    (window as unknown as { marcaDaPaginaSemRecarregar: string }).marcaDaPaginaSemRecarregar = 'mesma-pagina';
  });
}

async function lerMarcaDaPagina(paginaDaBoleta: Page) {
  return paginaDaBoleta.evaluate(() => (window as unknown as { marcaDaPaginaSemRecarregar?: string }).marcaDaPaginaSemRecarregar);
}

test.beforeEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
});

test.afterEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
});

test('CA-17: o botão vermelho com lixeira fica à direita no cabeçalho de Compra/Venda, com 38 px e borda coral translúcida', async ({ page }) => {
  await page.goto('/');
  const botaoDoCabecalho = localizarBotaoDeletarTudoDoCabecalho(page);
  await expect(botaoDoCabecalho).toHaveCount(1);
  await expect(botaoDoCabecalho).toHaveText('Deletar tudo');
  await conferirDesenhoDoIcone(botaoDoCabecalho, DESENHO_DO_ICONE_LIXEIRA);
  await conferirMedidaEmPxDeCss(botaoDoCabecalho, 'height', 38);
  await expect(botaoDoCabecalho).toHaveCSS('color', COR_CORAL);
  await expect(botaoDoCabecalho).toHaveCSS('border-top-color', COR_DA_BORDA_CORAL_TRANSLUCIDA);
  await expect(botaoDoCabecalho).toHaveCSS('background-color', 'rgba(255, 138, 122, 0.12)');
  await botaoDoCabecalho.hover();
  await expect(botaoDoCabecalho).toHaveCSS('background-color', 'rgba(255, 138, 122, 0.2)');
  const caixaDoTitulo = (await localizarCartaoCompraVenda(page).getByRole('heading', { name: 'Compra/Venda' }).boundingBox())!;
  const caixaDoBotao = (await botaoDoCabecalho.boundingBox())!;
  const caixaDoCartao = (await localizarCartaoCompraVenda(page).boundingBox())!;
  expect(caixaDoBotao.x).toBeGreaterThan(caixaDoTitulo.x + caixaDoTitulo.width);
  // À direita: o botão termina a menos de 40 px de CSS da borda direita do cartão (a página está em 90%).
  expect((caixaDoCartao.x + caixaDoCartao.width - (caixaDoBotao.x + caixaDoBotao.width)) / 0.9).toBeLessThan(40);
});

test('CA-17: clicar abre a janela por cima, com fundo escurecido e borrado, ícone de alerta, título, texto e os dois botões da maquete', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await abrirTelaComAsOrdensDoTeste(page);
  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  const janela = localizarJanelaDeConfirmacao(page);
  await expect(janela).toBeVisible();
  await expect(janela.getByRole('heading', { name: 'Deletar todos os dados?' })).toHaveCSS('font-family', /^Sora/);
  await conferirMedidaEmPxDeCss(janela.getByRole('heading', { name: 'Deletar todos os dados?' }), 'font-size', 20);
  await expect(janela.locator('#texto-confirmacao-deletar')).toHaveText(TEXTO_DA_JANELA);
  await conferirMedidaEmPxDeCss(janela, 'width', 440);
  await conferirMedidaEmPxDeCss(janela, 'border-top-left-radius', 22);
  const quadradoDoIcone = janela.locator('.janela-confirmacao-icone');
  await conferirDesenhoDoIcone(quadradoDoIcone, DESENHO_DO_ICONE_ALERTA);
  await conferirMedidaEmPxDeCss(quadradoDoIcone, 'width', 48);
  await conferirMedidaEmPxDeCss(quadradoDoIcone, 'height', 48);
  await expect(quadradoDoIcone).toHaveCSS('color', COR_CORAL);
  const botaoCancelar = janela.getByRole('button', { name: 'Cancelar' });
  const botaoConfirmar = janela.getByRole('button', { name: 'Deletar tudo' });
  await conferirMedidaEmPxDeCss(botaoCancelar, 'height', 46);
  await expect(botaoCancelar).toHaveCSS('background-color', COR_DO_FUNDO_DO_CANCELAR);
  await conferirMedidaEmPxDeCss(botaoConfirmar, 'height', 46);
  await expect(botaoConfirmar).toHaveCSS('background-color', COR_CORAL);
  await expect(botaoConfirmar).toHaveCSS('color', COR_DO_TEXTO_SOBRE_CORAL);
  const fundoAtrasDaJanela = await janela.evaluate((janelaNaPagina) => {
    const estiloDoFundo = getComputedStyle(janelaNaPagina, '::backdrop');
    return { corDoFundo: estiloDoFundo.backgroundColor, filtroDoFundo: estiloDoFundo.backdropFilter };
  });
  expect(fundoAtrasDaJanela).toEqual({ corDoFundo: 'rgba(4, 10, 11, 0.62)', filtroDoFundo: 'blur(6px)' });
  // Por cima da tela: o centro da janela é o elemento do topo naquele ponto, e o botão do cabeçalho fica atrás dela.
  const elementoNoCentroDaJanela = await janela.evaluate((janelaNaPagina) => {
    const caixaDaJanela = janelaNaPagina.getBoundingClientRect();
    return janelaNaPagina.contains(document.elementFromPoint(caixaDaJanela.x + caixaDaJanela.width / 2, caixaDaJanela.y + caixaDaJanela.height / 2));
  });
  expect(elementoNoCentroDaJanela).toBe(true);
  await expect(janela.getByRole('button', { name: 'Cancelar' })).toBeFocused();
  await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '08-janela-aberta-1440.png') });
});

for (const caminhoParaFechar of ['botão Cancelar', 'tecla Esc', 'clique fora da janela'] as const) {
  test(`CA-18: ${caminhoParaFechar} fecha a janela sem apagar nada — lista e exposição iguais`, async ({ page }) => {
    await abrirTelaComAsOrdensDoTeste(page);
    const exposicoesAntes = await lerExposicoesNaTela(page);
    const chamadasDeApagar: string[] = [];
    page.on('request', (requisicaoDaTela) => {
      if (ehChamadaDaApi(requisicaoDaTela, 'DELETE', ROTA_DAS_ORDENS)) chamadasDeApagar.push(requisicaoDaTela.url());
    });
    await localizarBotaoDeletarTudoDoCabecalho(page).click();
    await expect(localizarJanelaDeConfirmacao(page)).toBeVisible();

    if (caminhoParaFechar === 'botão Cancelar') await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Cancelar' }).click();
    if (caminhoParaFechar === 'tecla Esc') await page.keyboard.press('Escape');
    if (caminhoParaFechar === 'clique fora da janela') await page.mouse.click(8, 8);

    await expect(localizarJanelaDeConfirmacao(page)).toHaveCount(0);
    await expect(localizarBotaoDeletarTudoDoCabecalho(page)).toBeFocused();
    await expect(localizarLinhasDaListaDeOrdens(page)).toHaveCount(ORDENS_DE_COMPRA_DO_TESTE.length);
    expect(await lerExposicoesNaTela(page)).toEqual(exposicoesAntes);
    expect(await contarOrdensNoServidor(page)).toBe(ORDENS_DE_COMPRA_DO_TESTE.length);
    expect(chamadasDeApagar).toEqual([]);
  });
}

test('CA-19 e CA-41: Deletar tudo na janela apaga o banco, relê só a página 1 e a exposição, e mostra a tela zerada sem recarregar', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  await marcarPaginaSemRecarregar(page);
  // A contagem começa no próprio DELETE: toda chamada à API depois dele entra, inclusive uma duplicada.
  const chamadasDepoisDoApagar: string[] = [];
  let apagarJaSaiu = false;
  page.on('request', (requisicaoDaTela) => {
    if (ehChamadaDaApi(requisicaoDaTela, 'DELETE', ROTA_DAS_ORDENS)) {
      apagarJaSaiu = true;
      return;
    }
    if (apagarJaSaiu && new URL(requisicaoDaTela.url()).pathname.startsWith('/api/')) {
      const enderecoDaChamada = new URL(requisicaoDaTela.url());
      chamadasDepoisDoApagar.push(`${requisicaoDaTela.method()} ${enderecoDaChamada.pathname}${enderecoDaChamada.search}`);
    }
  });
  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  const respostaDoApagar = page.waitForResponse((respostaHttp) => ehChamadaDaApi(respostaHttp.request(), 'DELETE', ROTA_DAS_ORDENS));
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Deletar tudo' }).click();
  expect((await respostaDoApagar).status()).toBe(204);

  await expect(localizarJanelaDeConfirmacao(page)).toHaveCount(0);
  await conferirTelaZerada(page);
  expect(chamadasDepoisDoApagar.sort()).toEqual(['GET /api/exposures', 'GET /api/orders?page=1']);
  expect(await lerMarcaDaPagina(page)).toBe('mesma-pagina');
  expect(await contarOrdensNoServidor(page)).toBe(0);
  expect(await lerExposicoesNoServidor(page)).toEqual(
    SIMBOLOS_DA_EXPOSICAO.map((simboloDaExposicao) => ({ symbol: simboloDaExposicao, exposure: 0, remaining: 100_000_000 })),
  );
  await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, '08-depois-do-apagar-1440.png') });
});

test('CA-45: com 503 sem apagar no servidor, a tela relê lista e exposição antes de mostrar o erro, e nada muda', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  const exposicoesAntes = await lerExposicoesNaTela(page);
  await page.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS, async (rotaInterceptada) => {
    if (rotaInterceptada.request().method() !== 'DELETE') return rotaInterceptada.fallback();
    await rotaInterceptada.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 'communication_error' }) });
  });
  const controleDaReleituraDaLista = await segurarReleituraDaListaDepoisDoApagar(page);

  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await controleDaReleituraDaLista.releituraChegou;
  // Enquanto a releitura não volta, o erro ainda não aparece.
  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveCount(0);
  controleDaReleituraDaLista.liberarReleituraDaLista();

  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveText(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  await expect(localizarJanelaDeConfirmacao(page)).toBeVisible();
  expect(controleDaReleituraDaLista.exposicaoRelidaDepoisDoApagar()).toBe(true);
  await expect(localizarLinhasDaListaDeOrdens(page)).toHaveCount(ORDENS_DE_COMPRA_DO_TESTE.length);
  expect(await lerExposicoesNaTela(page)).toEqual(exposicoesAntes);
  expect(await contarOrdensNoServidor(page)).toBe(ORDENS_DE_COMPRA_DO_TESTE.length);
});

test('CA-45: com 503 depois de o servidor apagar (prazo do OrderGenerator), a releitura mostra a tela zerada e a janela mostra o erro', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  await page.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS, async (rotaInterceptada) => {
    if (rotaInterceptada.request().method() !== 'DELETE') return rotaInterceptada.fallback();
    const respostaDoServidor = await rotaInterceptada.fetch();
    expect(respostaDoServidor.status()).toBe(204);
    await rotaInterceptada.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 'communication_error' }) });
  });
  const controleDaReleituraDaLista = await segurarReleituraDaListaDepoisDoApagar(page);

  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await controleDaReleituraDaLista.releituraChegou;
  // Enquanto a releitura não volta, o erro ainda não aparece.
  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveCount(0);
  controleDaReleituraDaLista.liberarReleituraDaLista();

  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveText(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  expect(controleDaReleituraDaLista.exposicaoRelidaDepoisDoApagar()).toBe(true);
  await conferirTelaZerada(page);
  expect(await contarOrdensNoServidor(page)).toBe(0);
});

test('ASSUMI-04: com a tela na página 2, o 503 no apagar relê a própria página 2 e a exposição antes do erro', async ({ page }) => {
  for (let posicaoDaOrdem = 0; posicaoDaOrdem < 13; posicaoDaOrdem++) {
    const respostaDaCriacao = await page.request.post(ROTA_DE_CRIACAO_DE_ORDEM, { data: { symbol: 'PETR4', side: 'buy', quantity: 1, price: 1 } });
    expect(respostaDaCriacao.status()).toBe(200);
  }
  await page.goto('/');
  await expect(localizarLinhasDaListaDeOrdens(page)).toHaveCount(10);
  await page.getByRole('group', { name: 'Páginas da lista de ordens' }).getByRole('button', { name: 'Página 2', exact: true }).click();
  await expect(localizarLinhasDaListaDeOrdens(page)).toHaveCount(3);
  await page.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS, async (rotaInterceptada) => {
    if (rotaInterceptada.request().method() !== 'DELETE') return rotaInterceptada.fallback();
    await rotaInterceptada.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ status: 'communication_error' }) });
  });
  const leiturasDepoisDoApagar: string[] = [];
  let apagarJaSaiu = false;
  page.on('request', (requisicaoDaTela) => {
    if (ehChamadaDaApi(requisicaoDaTela, 'DELETE', ROTA_DAS_ORDENS)) apagarJaSaiu = true;
    else if (apagarJaSaiu && new URL(requisicaoDaTela.url()).pathname.startsWith('/api/')) {
      const enderecoDaChamada = new URL(requisicaoDaTela.url());
      leiturasDepoisDoApagar.push(`${requisicaoDaTela.method()} ${enderecoDaChamada.pathname}${enderecoDaChamada.search}`);
    }
  });

  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Deletar tudo' }).click();

  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveText(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  expect(leiturasDepoisDoApagar.sort()).toEqual(['GET /api/exposures', 'GET /api/orders?page=2']);
  await expect(localizarLinhasDaListaDeOrdens(page)).toHaveCount(3);
  await expect(page.getByRole('group', { name: 'Páginas da lista de ordens' }).getByRole('button', { name: 'Página 2', exact: true })).toHaveAttribute('aria-current', 'page');
});

test('RF-08: enquanto o apagar está no servidor, os dois botões da janela ficam desligados e só sai um DELETE', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  let liberarApagar: () => void = () => {};
  const apagarLiberado = new Promise<void>((resolverLiberacao) => (liberarApagar = resolverLiberacao));
  let quantidadeDeDeletes = 0;
  await page.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS, async (rotaInterceptada) => {
    if (rotaInterceptada.request().method() !== 'DELETE') return rotaInterceptada.fallback();
    quantidadeDeDeletes++;
    await apagarLiberado;
    await rotaInterceptada.continue();
  });

  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  const janela = localizarJanelaDeConfirmacao(page);
  await janela.getByRole('button', { name: 'Deletar tudo' }).click();
  await expect(janela.getByRole('button', { name: 'Apagando…' })).toBeDisabled();
  await expect(janela.getByRole('button', { name: 'Cancelar' })).toBeDisabled();
  await expect(janela.getByRole('button', { name: 'Apagando…' })).toHaveCSS('opacity', '0.7');
  await expect(janela.getByRole('button', { name: 'Cancelar' })).toHaveCSS('opacity', '0.7');
  // Dois Esc seguidos: no Chrome o segundo já não passa pelo "cancel" do <dialog>.
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');
  await page.mouse.click(8, 8);
  await expect(janela).toBeVisible();
  await janela.getByRole('button', { name: 'Apagando…' }).click({ force: true });
  liberarApagar();

  await expect(janela).toHaveCount(0);
  await conferirTelaZerada(page);
  expect(quantidadeDeDeletes).toBe(1);
});

test('RNF-05: a janela é modal e o Tab nunca leva o foco a um controle fora dela', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  const janela = localizarJanelaDeConfirmacao(page);
  await expect(janela).toBeVisible();
  expect(await janela.evaluate((janelaNaPagina) => janelaNaPagina.matches(':modal'))).toBe(true);
  await expect(janela).toHaveAttribute('aria-labelledby', 'titulo-confirmacao-deletar');
  const focosPorTab: string[] = [];
  for (const teclaDeNavegacao of ['Tab', 'Tab', 'Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
    await page.keyboard.press(teclaDeNavegacao);
    // Fora dos controles da janela, o Chromium só deixa o foco ir para a própria barra do navegador (activeElement = body).
    focosPorTab.push(
      await page.evaluate(() => {
        const elementoComFoco = document.activeElement;
        if (!elementoComFoco || elementoComFoco === document.body) return 'barra do navegador';
        return elementoComFoco.closest('dialog') ? `janela: ${elementoComFoco.textContent?.trim()}` : `FORA: ${elementoComFoco.outerHTML.slice(0, 80)}`;
      }),
    );
  }
  expect(focosPorTab.filter((focoDepoisDoTab) => focoDepoisDoTab.startsWith('FORA'))).toEqual([]);
  expect(focosPorTab).toContain('janela: Cancelar');
  expect(focosPorTab).toContain('janela: Deletar tudo');
});

test('ASSUMI-05: abrir a janela de novo depois de um erro começa sem a mensagem antiga', async ({ page }) => {
  await abrirTelaComAsOrdensDoTeste(page);
  await page.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS, async (rotaInterceptada) => {
    if (rotaInterceptada.request().method() !== 'DELETE') return rotaInterceptada.fallback();
    await rotaInterceptada.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
  });
  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Deletar tudo' }).click();
  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveText(MENSAGEM_DE_ORDENS_NAO_APAGADAS);
  await localizarJanelaDeConfirmacao(page).getByRole('button', { name: 'Cancelar' }).click();
  await expect(localizarJanelaDeConfirmacao(page)).toHaveCount(0);

  await localizarBotaoDeletarTudoDoCabecalho(page).click();
  await expect(localizarJanelaDeConfirmacao(page)).toBeVisible();
  await expect(localizarJanelaDeConfirmacao(page).getByRole('alert')).toHaveCount(0);
});

// Segura a primeira leitura da lista que sai depois do DELETE, para o teste olhar a janela antes de ela voltar.
async function segurarReleituraDaListaDepoisDoApagar(paginaDaBoleta: Page) {
  let apagarJaSaiu = false;
  let exposicaoRelida = false;
  let avisarQueReleituraChegou: () => void = () => {};
  let liberarReleitura: () => void = () => {};
  const releituraChegou = new Promise<void>((resolverChegada) => (avisarQueReleituraChegou = resolverChegada));
  const releituraLiberada = new Promise<void>((resolverLiberacao) => (liberarReleitura = resolverLiberacao));
  paginaDaBoleta.on('request', (requisicaoDaTela) => {
    if (ehChamadaDaApi(requisicaoDaTela, 'DELETE', ROTA_DAS_ORDENS)) apagarJaSaiu = true;
    if (apagarJaSaiu && ehChamadaDaApi(requisicaoDaTela, 'GET', ROTA_DAS_EXPOSICOES)) exposicaoRelida = true;
  });
  await paginaDaBoleta.route((enderecoDaChamada) => enderecoDaChamada.pathname === ROTA_DAS_ORDENS && enderecoDaChamada.searchParams.has('page'), async (rotaInterceptada) => {
    if (!apagarJaSaiu) return rotaInterceptada.fallback();
    avisarQueReleituraChegou();
    await releituraLiberada;
    await rotaInterceptada.fallback();
  });
  return { releituraChegou, liberarReleituraDaLista: () => liberarReleitura(), exposicaoRelidaDepoisDoApagar: () => exposicaoRelida };
}

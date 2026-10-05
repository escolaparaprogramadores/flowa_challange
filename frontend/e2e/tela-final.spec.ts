import { expect, test, type Locator, type Page, type Request } from '@playwright/test';
import path from 'node:path';
import { ROTA_DAS_ORDENS, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Conferência final da tela inteira, com todas as fatias juntas: larguras estreitas (CA-26), foco pelo teclado (CA-27),
// prints para o confronto com as maquetes (CA-28), nenhuma chamada parada (CA-34) e nada de fora (CA-40, CA-43).

const PASTA_DAS_PROVAS = process.env.F8_PASTA_DAS_PROVAS ?? path.resolve('test-results', 'provas-tela-final');
const ESCALA_DA_PAGINA = 0.9;
const COR_DO_ACENTO = 'rgb(79, 227, 176)';
// 23 ordens dão 3 páginas, como na maquete-02 ("Mostrando 1-10 de 23 ordens").
const QUANTIDADE_DE_ORDENS_DA_MAQUETE = 23;
const SIMBOLOS_DA_EXPOSICAO = ['PETR4', 'VALE3', 'VIIA4'];

async function apagarTodasAsOrdensNoServidor(paginaDaBoleta: Page) {
  const respostaDoApagamento = await paginaDaBoleta.request.delete(ROTA_DAS_ORDENS);
  expect(respostaDoApagamento.status()).toBe(204);
}

async function criarOrdensDaMaquetePelaApi(paginaDaBoleta: Page) {
  const ordensDeExemplo = [
    { symbol: 'PETR4', side: 'buy', quantity: 100, price: 38.2 },
    { symbol: 'VALE3', side: 'sell', quantity: 200, price: 61.4 },
    { symbol: 'VIIA4', side: 'buy', quantity: 1000, price: 1.2 },
  ];
  for (let posicaoDaOrdem = 0; posicaoDaOrdem < QUANTIDADE_DE_ORDENS_DA_MAQUETE; posicaoDaOrdem++) {
    const respostaDaCriacao = await paginaDaBoleta.request.post(ROTA_DE_CRIACAO_DE_ORDEM, { data: ordensDeExemplo[posicaoDaOrdem % ordensDeExemplo.length] });
    expect(respostaDaCriacao.status()).toBe(200);
  }
}

async function abrirTelaCompleta(paginaDaBoleta: Page) {
  await paginaDaBoleta.goto('/');
  await expect(paginaDaBoleta.getByRole('region', { name: 'Compra/Venda' }).getByTestId('linha-da-ordem')).toHaveCount(10);
  await expect(paginaDaBoleta.getByTestId('exposicao-PETR4').getByTestId('exposicao-atual')).not.toHaveText('');
  await paginaDaBoleta.evaluate(() => document.fonts.ready);
}

function caixasSeSobrepoem(primeiraCaixa: { x: number; y: number; width: number; height: number }, segundaCaixa: { x: number; y: number; width: number; height: number }) {
  return (
    primeiraCaixa.x < segundaCaixa.x + segundaCaixa.width &&
    segundaCaixa.x < primeiraCaixa.x + primeiraCaixa.width &&
    primeiraCaixa.y < segundaCaixa.y + segundaCaixa.height &&
    segundaCaixa.y < primeiraCaixa.y + primeiraCaixa.height
  );
}

async function conferirVisivelELerCaixaDoElemento(elementoDaTela: Locator, nomeDoElemento: string) {
  await expect(elementoDaTela, nomeDoElemento).toHaveCount(1);
  await expect(elementoDaTela, nomeDoElemento).toBeVisible();
  return (await elementoDaTela.boundingBox())!;
}

test.beforeEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
  await criarOrdensDaMaquetePelaApi(page);
});

test.afterEach(async ({ page }) => {
  await apagarTodasAsOrdensNoServidor(page);
});

for (const larguraDaJanela of [1920, 1440, 860, 375]) {
  test(`CA-28: print da tela final em ${larguraDaJanela} px, sem rolagem horizontal da página`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 1000 });
    await abrirTelaCompleta(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(larguraDaJanela);
    await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, `08-tela-final-${larguraDaJanela}.png`), fullPage: true });
  });
}

for (const larguraDaJanela of [860, 375]) {
  test(`CA-26: em ${larguraDaJanela} px os ativos ficam um embaixo do outro, Nova ordem acima de Compra/Venda e nada sai da tela`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 1000 });
    await abrirTelaCompleta(page);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(larguraDaJanela);

    const caixasDosAtivos = [];
    for (const simboloDaExposicao of SIMBOLOS_DA_EXPOSICAO) caixasDosAtivos.push(await conferirVisivelELerCaixaDoElemento(page.getByTestId('exposicao-' + simboloDaExposicao), simboloDaExposicao));
    for (let posicaoDoAtivo = 1; posicaoDoAtivo < caixasDosAtivos.length; posicaoDoAtivo++) {
      const ativoDeCima = caixasDosAtivos[posicaoDoAtivo - 1];
      const ativoDeBaixo = caixasDosAtivos[posicaoDoAtivo];
      expect(ativoDeBaixo.y, SIMBOLOS_DA_EXPOSICAO[posicaoDoAtivo]).toBeGreaterThanOrEqual(ativoDeCima.y + ativoDeCima.height);
      expect(Math.abs(ativoDeBaixo.x - ativoDeCima.x), SIMBOLOS_DA_EXPOSICAO[posicaoDoAtivo]).toBeLessThan(1);
    }

    // Ordem do computador também no celular (decisão G-1): ativos → Nova ordem → Compra/Venda.
    const caixaDaBoleta = await conferirVisivelELerCaixaDoElemento(page.getByRole('form', { name: 'Boleta de ordem' }), 'Nova ordem');
    const caixaDaCompraVenda = await conferirVisivelELerCaixaDoElemento(page.getByRole('region', { name: 'Compra/Venda' }), 'Compra/Venda');
    const ultimoAtivo = caixasDosAtivos[caixasDosAtivos.length - 1];
    expect(caixaDaBoleta.y).toBeGreaterThanOrEqual(ultimoAtivo.y + ultimoAtivo.height);
    expect(caixaDaCompraVenda.y).toBeGreaterThanOrEqual(caixaDaBoleta.y + caixaDaBoleta.height);

    // ASSUMI-06 (decisão da F6, ASSUMI-04 dela): em vez de rolar de lado, a lista cabe inteira no cartão.
    // Em 860 a tabela fica inteira com cabeçalho; em 375 cada ordem vira um bloco com os 8 campos rotulados.
    const molduraDaLista = page.getByRole('region', { name: 'Compra/Venda' }).locator('.tabela-de-ordens-moldura');
    const caixaDaMoldura = await conferirVisivelELerCaixaDoElemento(molduraDaLista, 'moldura da lista');
    expect(caixaDaMoldura.x + caixaDaMoldura.width).toBeLessThanOrEqual(caixaDaCompraVenda.x + caixaDaCompraVenda.width + 0.5);
    const larguraDaListaNaMoldura = await molduraDaLista.evaluate((molduraNaPagina) => ({ larguraDoConteudo: molduraNaPagina.scrollWidth, larguraVisivel: molduraNaPagina.clientWidth }));
    expect(larguraDaListaNaMoldura.larguraDoConteudo, 'nada escondido para os lados').toBeLessThanOrEqual(larguraDaListaNaMoldura.larguraVisivel);
    const primeiraOrdemDaLista = page.getByRole('region', { name: 'Compra/Venda' }).getByTestId('linha-da-ordem').first();
    const caixaDaPrimeiraOrdem = await conferirVisivelELerCaixaDoElemento(primeiraOrdemDaLista, 'primeira ordem');
    expect(caixaDaPrimeiraOrdem.x + caixaDaPrimeiraOrdem.width).toBeLessThanOrEqual(caixaDaMoldura.x + caixaDaMoldura.width + 0.5);
    const formatoDaLista = await primeiraOrdemDaLista.evaluate((linhaNaPagina) => ({
      exibicaoDaLinha: getComputedStyle(linhaNaPagina).display,
      cabecalhoVisivel: getComputedStyle(linhaNaPagina.closest('table')!.querySelector('thead')!).display !== 'none',
      rotulosDosCampos: [...linhaNaPagina.querySelectorAll('td')].map((campoDaOrdem) => getComputedStyle(campoDaOrdem, '::before').content),
    }));
    const rotulosEsperados = ['"Data"', '"Status"', '"Ativo"', '"Lado"', '"Quantidade"', '"Preço"', '"Número da ordem"', '"Identificador do envio"'];
    if (larguraDaJanela === 375) {
      expect(formatoDaLista).toEqual({ exibicaoDaLinha: 'grid', cabecalhoVisivel: false, rotulosDosCampos: rotulosEsperados });
    } else {
      expect(formatoDaLista).toEqual({ exibicaoDaLinha: 'table-row', cabecalhoVisivel: true, rotulosDosCampos: Array(8).fill('none') });
    }

    // Links do Datadog e selo quebram linha sem sobrepor o logo e sem sair da tela.
    const caixaDoLogo = await conferirVisivelELerCaixaDoElemento(page.getByRole('img', { name: 'Base investimentos' }), 'logo');
    const linksDoDatadog = page.getByRole('list', { name: 'Painéis do Datadog' }).getByRole('link');
    await expect(linksDoDatadog).toHaveCount(3);
    const elementosDoTopo: Array<[string, Locator]> = [
      ['link 1 do Datadog', linksDoDatadog.nth(0)],
      ['link 2 do Datadog', linksDoDatadog.nth(1)],
      ['link 3 do Datadog', linksDoDatadog.nth(2)],
      ['selo do ambiente', page.locator('.selo-ambiente')],
    ];
    for (const [nomeDoElemento, elementoDoTopo] of elementosDoTopo) {
      const caixaDoElemento = await conferirVisivelELerCaixaDoElemento(elementoDoTopo, nomeDoElemento);
      expect(caixasSeSobrepoem(caixaDoElemento, caixaDoLogo), `${nomeDoElemento} sobre o logo`).toBe(false);
      expect(caixaDoElemento.x, nomeDoElemento).toBeGreaterThanOrEqual(0);
      expect(caixaDoElemento.x + caixaDoElemento.width, nomeDoElemento).toBeLessThanOrEqual(larguraDaJanela);
    }

    // Cabeçalho de Compra/Venda: título e "Deletar tudo" cabem lado a lado, sem cortar.
    const caixaDoTitulo = await conferirVisivelELerCaixaDoElemento(page.getByRole('heading', { name: 'Compra/Venda' }), 'título Compra/Venda');
    const caixaDoDeletarTudo = await conferirVisivelELerCaixaDoElemento(page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }), 'Deletar tudo');
    expect(caixasSeSobrepoem(caixaDoTitulo, caixaDoDeletarTudo)).toBe(false);
    expect(caixaDoDeletarTudo.x + caixaDoDeletarTudo.width).toBeLessThanOrEqual(caixaDaCompraVenda.x + caixaDaCompraVenda.width);

    // A janela de confirmação também cabe inteira.
    await page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }).click();
    const caixaDaJanela = await conferirVisivelELerCaixaDoElemento(page.getByRole('dialog', { name: 'Deletar todos os dados?' }), 'janela');
    expect(caixaDaJanela.x).toBeGreaterThanOrEqual(0);
    expect(caixaDaJanela.x + caixaDaJanela.width).toBeLessThanOrEqual(larguraDaJanela);
    for (const nomeDoBotao of ['Cancelar', 'Deletar tudo']) {
      const caixaDoBotao = await conferirVisivelELerCaixaDoElemento(page.getByRole('dialog').getByRole('button', { name: nomeDoBotao }), nomeDoBotao);
      expect(caixaDoBotao.x + caixaDoBotao.width, nomeDoBotao).toBeLessThanOrEqual(caixaDaJanela.x + caixaDaJanela.width);
    }
    await page.screenshot({ path: path.join(PASTA_DAS_PROVAS, `08-janela-aberta-${larguraDaJanela}.png`) });
  });
}

// Razão de contraste da WCAG 2 entre duas cores "rgb(r, g, b)".
function calcularContrasteWcag(corDaFrente: string, corDoFundo: string) {
  const calcularLuminanciaDaCor = (corRgb: string) => {
    const [canalVermelho, canalVerde, canalAzul] = (corRgb.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((canalDe0a255) => {
      const canalDe0a1 = canalDe0a255 / 255;
      return canalDe0a1 <= 0.03928 ? canalDe0a1 / 12.92 : ((canalDe0a1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * canalVermelho + 0.7152 * canalVerde + 0.0722 * canalAzul;
  };
  const [luminanciaMaior, luminanciaMenor] = [calcularLuminanciaDaCor(corDaFrente), calcularLuminanciaDaCor(corDoFundo)].sort((luminanciaDaPrimeira, luminanciaDaSegunda) => luminanciaDaSegunda - luminanciaDaPrimeira);
  return (luminanciaMaior + 0.05) / (luminanciaMenor + 0.05);
}

// Aperta Tab até o foco chegar no controle; o foco por teclado é o que liga o :focus-visible.
async function levarFocoPorTabAte(paginaDaBoleta: Page, controleDaTela: Locator, nomeDoControle: string) {
  for (let quantidadeDeTabs = 0; quantidadeDeTabs < 80; quantidadeDeTabs++) {
    if (await controleDaTela.evaluate((controleNaPagina) => controleNaPagina === document.activeElement)) return;
    await paginaDaBoleta.keyboard.press('Tab');
  }
  throw new Error(`O Tab não chegou em ${nomeDoControle}`);
}

async function lerFocoEContrasteDoControle(controleDaTela: Locator) {
  return controleDaTela.evaluate((controleNaPagina) => {
    // Fundo translúcido (como o coral a 12% do "Deletar tudo") se mistura com os fundos de baixo:
    // junta as camadas do elemento para cima até a primeira opaca e devolve a cor que aparece na tela.
    const calcularCorDeFundoVisivel = (elementoInicial: Element | null) => {
      const camadasDeFundo: number[][] = [];
      for (let elementoAtual = elementoInicial; elementoAtual; elementoAtual = elementoAtual.parentElement) {
        const [canalVermelho, canalVerde, canalAzul, opacidade = 1] = (getComputedStyle(elementoAtual).backgroundColor.match(/[\d.]+/g) ?? []).map(Number);
        if (opacidade > 0) camadasDeFundo.push([canalVermelho, canalVerde, canalAzul, opacidade]);
        if (opacidade >= 1) break;
      }
      const corDaPagina = (getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g) ?? []).map(Number);
      const corMisturada = camadasDeFundo.reduceRight(
        (corDeBaixo, [canalVermelho, canalVerde, canalAzul, opacidade]) => [
          canalVermelho * opacidade + corDeBaixo[0] * (1 - opacidade),
          canalVerde * opacidade + corDeBaixo[1] * (1 - opacidade),
          canalAzul * opacidade + corDeBaixo[2] * (1 - opacidade),
        ],
        corDaPagina.slice(0, 3),
      );
      return `rgb(${corMisturada.map(Math.round).join(', ')})`;
    };
    const estiloDoControle = getComputedStyle(controleNaPagina);
    return {
      estiloDoContorno: estiloDoControle.outlineStyle,
      corDoContorno: estiloDoControle.outlineColor,
      larguraDoContorno: parseFloat(estiloDoControle.outlineWidth),
      corDoTexto: estiloDoControle.color,
      corDoFundoDoControle: calcularCorDeFundoVisivel(controleNaPagina),
      corDoFundoEmVolta: calcularCorDeFundoVisivel(controleNaPagina.parentElement),
      estaComFocoVisivel: controleNaPagina.matches(':focus-visible'),
    };
  });
}

// O anel do tema tem 2px de CSS e os links do Datadog, 3px (topo.css). Com a página em 90% o Chromium arredonda
// para pixel inteiro da tela: 2px vira 1 pixel e 3px vira 2.
async function conferirFocoVisivelPorTab(paginaDaBoleta: Page, nomeDoControle: string, controleDaTela: Locator, larguraDoAnelEmPxDeCss = 2) {
  await conferirVisivelELerCaixaDoElemento(controleDaTela, nomeDoControle);
  await levarFocoPorTabAte(paginaDaBoleta, controleDaTela, nomeDoControle);
  const focoEContraste = await lerFocoEContrasteDoControle(controleDaTela);
  expect(focoEContraste.estaComFocoVisivel, nomeDoControle).toBe(true);
  expect(focoEContraste.estiloDoContorno, nomeDoControle).toBe('solid');
  expect(focoEContraste.corDoContorno, nomeDoControle).toBe(COR_DO_ACENTO);
  expect(focoEContraste.larguraDoContorno, nomeDoControle).toBeCloseTo(Math.floor(larguraDoAnelEmPxDeCss * ESCALA_DA_PAGINA) / ESCALA_DA_PAGINA, 3);
  // Anel de foco contra o fundo em volta: 3:1 (WCAG 1.4.11). Texto do controle contra o fundo dele: 4,5:1 (WCAG 1.4.3).
  expect(calcularContrasteWcag(focoEContraste.corDoContorno, focoEContraste.corDoFundoEmVolta), `${nomeDoControle}: anel`).toBeGreaterThanOrEqual(3);
  expect(calcularContrasteWcag(focoEContraste.corDoTexto, focoEContraste.corDoFundoDoControle), `${nomeDoControle}: texto`).toBeGreaterThanOrEqual(4.5);
  const caixaDoControle = (await controleDaTela.boundingBox())!;
  const margemDoPrint = 24;
  await paginaDaBoleta.screenshot({
    path: path.join(PASTA_DAS_PROVAS, `08-foco-${nomeDoControle.toLowerCase().replace(/[^a-z0-9]+/g, '-')}.png`),
    clip: {
      x: Math.max(0, caixaDoControle.x - margemDoPrint),
      y: Math.max(0, caixaDoControle.y - margemDoPrint),
      width: caixaDoControle.width + 2 * margemDoPrint,
      height: caixaDoControle.height + 2 * margemDoPrint,
    },
  });
}

test('CA-27: cada controle novo recebe foco visível pelo Tab, com anel na cor do acento e contraste legível', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await abrirTelaCompleta(page);
  const linksDoDatadog = page.getByRole('list', { name: 'Painéis do Datadog' }).getByRole('link');
  const grupoDoSimbolo = page.getByRole('group', { name: 'Símbolo' });
  const paginacao = page.getByRole('group', { name: 'Páginas da lista de ordens' });
  for (const posicaoDoLink of [0, 1, 2]) await conferirFocoVisivelPorTab(page, `Datadog ${posicaoDoLink + 1}`, linksDoDatadog.nth(posicaoDoLink), 3);
  const controlesDaTela: Array<[string, Locator]> = [
    ['Deletar tudo', page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' })],
    ['Pagina 2', paginacao.getByRole('button', { name: 'Página 2', exact: true })],
    ['Proxima pagina', paginacao.getByRole('button', { name: 'Próxima página' })],
    ['Simbolo PETR4', grupoDoSimbolo.getByRole('button', { name: 'PETR4', exact: true })],
    ['Simbolo VALE3', grupoDoSimbolo.getByRole('button', { name: 'VALE3', exact: true })],
    ['Simbolo VIIA4', grupoDoSimbolo.getByRole('button', { name: 'VIIA4', exact: true })],
  ];
  for (const [nomeDoControle, controleDaTela] of controlesDaTela) await conferirFocoVisivelPorTab(page, nomeDoControle, controleDaTela);

  // Botões da janela: abrir pelo teclado, a partir do "Deletar tudo" do cabeçalho.
  await page.getByRole('region', { name: 'Compra/Venda' }).getByRole('button', { name: 'Deletar tudo' }).focus();
  await page.keyboard.press('Enter');
  const janela = page.getByRole('dialog', { name: 'Deletar todos os dados?' });
  await expect(janela).toBeVisible();
  await conferirFocoVisivelPorTab(page, 'Janela Cancelar', janela.getByRole('button', { name: 'Cancelar' }));
  await conferirFocoVisivelPorTab(page, 'Janela Deletar tudo', janela.getByRole('button', { name: 'Deletar tudo' }));
  await page.keyboard.press('Escape');
  await expect(janela).toHaveCount(0);
});

test('CA-34: com a tela completa parada 30 s, nenhuma chamada nova sai para o servidor', async ({ page }) => {
  test.setTimeout(60_000);
  await abrirTelaCompleta(page);
  await page.waitForLoadState('networkidle');
  const chamadasComATelaParada: string[] = [];
  page.on('request', (requisicaoDaTela) => chamadasComATelaParada.push(`${requisicaoDaTela.method()} ${requisicaoDaTela.url()}`));
  await page.waitForTimeout(30_000);
  expect(chamadasComATelaParada).toEqual([]);
});

test('CA-40 e CA-43: ao abrir a tela completa, nada sai para domínio externo e nenhuma imagem é baixada (logo do Datadog vai no código)', async ({ page, baseURL }) => {
  const requisicoesAoAbrir: Request[] = [];
  page.on('request', (requisicaoDaTela) => requisicoesAoAbrir.push(requisicaoDaTela));
  await abrirTelaCompleta(page);
  await page.waitForLoadState('networkidle');
  const origemDaTela = new URL(baseURL!).origin;
  const chamadasParaFora = requisicoesAoAbrir.map((requisicaoDaTela) => requisicaoDaTela.url()).filter((enderecoDaChamada) => new URL(enderecoDaChamada).origin !== origemDaTela);
  expect(chamadasParaFora).toEqual([]);
  const imagensBaixadas = requisicoesAoAbrir.filter((requisicaoDaTela) => requisicaoDaTela.resourceType() === 'image').map((requisicaoDaTela) => requisicaoDaTela.url());
  expect(imagensBaixadas).toEqual([]);
  expect(requisicoesAoAbrir.filter((requisicaoDaTela) => /datadog/i.test(new URL(requisicaoDaTela.url()).hostname))).toEqual([]);
  // O logo do Datadog dos 3 links é SVG dentro da página.
  await expect(page.getByRole('list', { name: 'Painéis do Datadog' }).locator('.painel-datadog-logo svg')).toHaveCount(3);
});

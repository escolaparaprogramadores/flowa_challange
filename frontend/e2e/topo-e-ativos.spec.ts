import { readFileSync } from 'node:fs';
import { expect, test, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import { ROTA_DAS_EXPOSICOES, ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Fala com o OrderGenerator e o OrderAccumulator de verdade. Cada cenário que mexe na exposição
// leva o símbolo a um valor exato por ordens reais e, no fim do arquivo, devolve cada símbolo
// ao valor que tinha antes, para não mudar o ponto de partida dos outros arquivos.

const PAINEIS_DO_DATADOG_ESPERADOS = [
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

const SIMBOLOS_DOS_CARTOES = ['PETR4', 'VALE3', 'VIIA4'] as const;
const COR_DO_ACENTO = 'rgb(79, 227, 176)';
const COR_DO_ROTULO = 'rgb(143, 163, 161)';
const COR_DA_BORDA = 'rgb(28, 48, 52)';
const BARRINHA_VERDE = 'linear-gradient(90deg, rgb(47, 199, 149), rgb(79, 227, 176))';
const BARRINHA_AMBAR = 'linear-gradient(90deg, rgb(233, 167, 60), rgb(244, 197, 106))';
const COR_DA_PORCENTAGEM_PERTO_DO_LIMITE = 'rgb(244, 197, 106)';
const COR_DO_TEXTO = 'rgb(232, 239, 238)';
const COR_DO_FUNDO_DO_LINK = 'rgb(14, 27, 30)';
// Os cartões ficam 14 px de CSS um do outro (maquete 01); na tela, com a página em 90%, são 12,6 px.
const ESPACO_ENTRE_CARTOES_NA_TELA = 14 * 0.9;
const PRECO_MAXIMO_DA_ORDEM_EM_CENTAVOS = 99_999;
const QUANTIDADE_MAXIMA_DA_ORDEM = 99_999;

type ExposicaoNoServidor = { symbol: string; exposure: number; remaining: number };

async function lerExposicaoEmCentavosNoServidor(requisicaoDoTeste: APIRequestContext, simboloDaExposicao: string) {
  const respostaDasExposicoes = await requisicaoDoTeste.get(ROTA_DAS_EXPOSICOES);
  expect(respostaDasExposicoes.status()).toBe(200);
  const corpoDasExposicoes = (await respostaDasExposicoes.json()) as { exposures: ExposicaoNoServidor[] };
  const exposicaoDoSimbolo = corpoDasExposicoes.exposures.find((exposicaoNoServidor) => exposicaoNoServidor.symbol === simboloDaExposicao);
  if (!exposicaoDoSimbolo) throw new Error('Símbolo ' + simboloDaExposicao + ' ausente em /api/exposures');
  return Math.round(exposicaoDoSimbolo.exposure * 100);
}

async function enviarOrdemAceitaPelaApi(requisicaoDoTeste: APIRequestContext, simbolo: string, lado: 'buy' | 'sell', quantidade: number, precoEmCentavos: number) {
  const respostaDaOrdem = await requisicaoDoTeste.post(ROTA_DE_CRIACAO_DE_ORDEM, {
    data: { symbol: simbolo, side: lado, quantity: quantidade, price: precoEmCentavos / 100 },
  });
  expect(respostaDaOrdem.status()).toBe(200);
  expect(((await respostaDaOrdem.json()) as { status: string }).status).toBe('accepted');
}

// Anda sempre na direção do alvo, então nenhuma ordem intermediária passa do limite.
async function levarExposicaoDoSimboloAte(requisicaoDoTeste: APIRequestContext, simbolo: string, exposicaoAlvoEmCentavos: number) {
  const diferencaEmCentavos = exposicaoAlvoEmCentavos - (await lerExposicaoEmCentavosNoServidor(requisicaoDoTeste, simbolo));
  const ladoDasOrdens = diferencaEmCentavos >= 0 ? 'buy' : 'sell';
  let centavosQueFaltamMover = Math.abs(diferencaEmCentavos);
  while (centavosQueFaltamMover > 0) {
    const quantidadeAoPrecoMaximo = Math.min(Math.floor(centavosQueFaltamMover / PRECO_MAXIMO_DA_ORDEM_EM_CENTAVOS), QUANTIDADE_MAXIMA_DA_ORDEM);
    if (quantidadeAoPrecoMaximo > 0) {
      await enviarOrdemAceitaPelaApi(requisicaoDoTeste, simbolo, ladoDasOrdens, quantidadeAoPrecoMaximo, PRECO_MAXIMO_DA_ORDEM_EM_CENTAVOS);
      centavosQueFaltamMover -= quantidadeAoPrecoMaximo * PRECO_MAXIMO_DA_ORDEM_EM_CENTAVOS;
    } else {
      await enviarOrdemAceitaPelaApi(requisicaoDoTeste, simbolo, ladoDasOrdens, 1, centavosQueFaltamMover);
      centavosQueFaltamMover = 0;
    }
  }
  expect(await lerExposicaoEmCentavosNoServidor(requisicaoDoTeste, simbolo)).toBe(exposicaoAlvoEmCentavos);
}

function localizarCartaoDoAtivo(paginaDaBoleta: Page, simboloDoAtivo: string) {
  return paginaDaBoleta.getByTestId('exposicao-' + simboloDoAtivo);
}

async function conferirCartaoDoAtivo(
  paginaDaBoleta: Page,
  simboloDoAtivo: string,
  // larguraDaBarrinha: fração do trilho preenchida; 'marca-minima' = exposição diferente de zero que não chega
  // a 1 px do trilho e aparece como um ponto do tamanho da altura da barrinha.
  cartaoEsperado: {
    exposicaoAtual: string;
    faltaAteOLimite: string;
    usoDoLimite: string;
    valorDoMedidor: string;
    barrinha: string;
    larguraDaBarrinha: number | 'marca-minima';
  },
) {
  const cartao = localizarCartaoDoAtivo(paginaDaBoleta, simboloDoAtivo);
  await expect(cartao.getByTestId('exposicao-atual')).toHaveText(cartaoEsperado.exposicaoAtual);
  await expect(cartao.getByTestId('exposicao-restante')).toHaveText(cartaoEsperado.faltaAteOLimite);
  await expect(cartao.getByTestId('uso-do-limite-porcentagem')).toHaveText(cartaoEsperado.usoDoLimite);
  // A porcentagem acompanha a cor da barrinha: âmbar perto do limite, texto normal no resto.
  await expect(cartao.getByTestId('uso-do-limite-porcentagem')).toHaveCSS(
    'color',
    cartaoEsperado.barrinha === BARRINHA_AMBAR ? COR_DA_PORCENTAGEM_PERTO_DO_LIMITE : COR_DO_TEXTO,
  );
  const barrinha = cartao.getByRole('meter', { name: 'Uso do limite de ' + simboloDoAtivo });
  await expect(barrinha).toHaveAttribute('aria-valuetext', cartaoEsperado.usoDoLimite);
  await expect(barrinha).toHaveAttribute('aria-valuenow', cartaoEsperado.valorDoMedidor);
  const preenchimentoDaBarrinha = cartao.getByTestId('uso-do-limite-preenchimento');
  await expect(preenchimentoDaBarrinha).toHaveCSS('background-image', cartaoEsperado.barrinha);
  const caixaDoTrilho = await medirRetanguloNaTela(barrinha);
  const larguraPreenchida = (await preenchimentoDaBarrinha.boundingBox())?.width ?? 0;
  if (cartaoEsperado.larguraDaBarrinha === 'marca-minima') expect(larguraPreenchida).toBeCloseTo(caixaDoTrilho.height, 1);
  else expect(larguraPreenchida / caixaDoTrilho.width).toBeCloseTo(cartaoEsperado.larguraDaBarrinha, 2);
}

// Com a página em 90%, o Chrome arredonda bordas e contornos para o pixel inteiro da tela, e a
// medida de CSS calculada sai com casas (46px vira 45,99px). Compara com tolerância de 0,05 px.
async function lerMedidaDoCssEmPx(elementoNaTela: Locator, propriedadeDoCss: 'height' | 'width' | 'outline-width') {
  return parseFloat(await elementoNaTela.evaluate((elementoNaPagina, propriedade) => getComputedStyle(elementoNaPagina).getPropertyValue(propriedade), propriedadeDoCss));
}

async function lerEscalaDaPagina(paginaDaBoleta: Page) {
  return parseFloat(await paginaDaBoleta.evaluate(() => getComputedStyle(document.documentElement).zoom));
}

function medirRetanguloNaTela(elementoNaTela: Locator) {
  return elementoNaTela.boundingBox().then((retangulo) => {
    if (!retangulo) throw new Error('elemento sem caixa na tela');
    return retangulo;
  });
}

function retangulosSeCruzam(
  primeiroRetangulo: { x: number; y: number; width: number; height: number },
  segundoRetangulo: { x: number; y: number; width: number; height: number },
) {
  return (
    primeiroRetangulo.x < segundoRetangulo.x + segundoRetangulo.width &&
    segundoRetangulo.x < primeiroRetangulo.x + primeiroRetangulo.width &&
    primeiroRetangulo.y < segundoRetangulo.y + segundoRetangulo.height &&
    segundoRetangulo.y < primeiroRetangulo.y + primeiroRetangulo.height
  );
}

// Linha de base do texto, na tela: uma marca vazia em linha assenta exatamente nela.
function medirLinhaDeBaseNaTela(textoNaTela: Locator) {
  return textoNaTela.evaluate((elementoComTexto) => {
    const marcaDaLinhaDeBase = document.createElement('span');
    marcaDaLinhaDeBase.style.display = 'inline-block';
    marcaDaLinhaDeBase.style.height = '0';
    elementoComTexto.append(marcaDaLinhaDeBase);
    const linhaDeBase = marcaDaLinhaDeBase.getBoundingClientRect().top;
    marcaDaLinhaDeBase.remove();
    return linhaDeBase;
  });
}

// O desenho esperado de cada ícone sai do próprio Icones.tsx: o teste prova que o ícone da tela é aquele, não um SVG qualquer.
function lerDesenhosDoIconeNoCodigo(nomeDoIcone: string) {
  const codigoDosIcones = readFileSync(new URL('../src/Icones.tsx', import.meta.url), 'utf8');
  const inicioDoIcone = codigoDosIcones.indexOf(`export function ${nomeDoIcone}(`);
  if (inicioDoIcone === -1) throw new Error(`${nomeDoIcone} não existe em Icones.tsx`);
  const fimDoIcone = codigoDosIcones.indexOf('export function', inicioDoIcone + 1);
  const codigoDoIcone = codigoDosIcones.slice(inicioDoIcone, fimDoIcone === -1 ? undefined : fimDoIcone);
  return [...codigoDoIcone.matchAll(/ d="([^"]+)"/g)].map((desenhoEncontrado) => desenhoEncontrado[1]);
}

async function conferirDesenhoDoIcone(iconeNaTela: Locator, nomeDoIcone: string) {
  const desenhosEsperados = lerDesenhosDoIconeNoCodigo(nomeDoIcone);
  expect(desenhosEsperados.length).toBeGreaterThan(0);
  expect(await iconeNaTela.locator('path').evaluateAll((caminhos) => caminhos.map((caminho) => caminho.getAttribute('d')))).toEqual(desenhosEsperados);
}

// Contraste WCAG entre duas cores "rgb(r, g, b)".
function calcularContraste(corDoTexto: string, corDoFundo: string) {
  const luminanciaDaCor = (corEmRgb: string) => {
    const [vermelho, verde, azul] = (corEmRgb.match(/\d+/g) ?? []).slice(0, 3).map((canal) => {
      const canalNormalizado = Number(canal) / 255;
      return canalNormalizado <= 0.03928 ? canalNormalizado / 12.92 : ((canalNormalizado + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * vermelho + 0.7152 * verde + 0.0722 * azul;
  };
  const [luminanciaMaior, luminanciaMenor] = [luminanciaDaCor(corDoTexto), luminanciaDaCor(corDoFundo)].sort((primeira, segunda) => segunda - primeira);
  return (luminanciaMaior + 0.05) / (luminanciaMenor + 0.05);
}

async function lerCorCalculada(elementoNaTela: Locator, propriedadeDaCor: 'color' | 'background-color') {
  return elementoNaTela.evaluate((elementoNaPagina, propriedade) => getComputedStyle(elementoNaPagina).getPropertyValue(propriedade), propriedadeDaCor);
}

const exposicoesAntesDoArquivoEmCentavos = new Map<string, number>();

test.beforeAll(async ({ request }) => {
  for (const simboloDoAtivo of SIMBOLOS_DOS_CARTOES) {
    exposicoesAntesDoArquivoEmCentavos.set(simboloDoAtivo, await lerExposicaoEmCentavosNoServidor(request, simboloDoAtivo));
  }
});

test.afterAll(async ({ request }) => {
  for (const [simboloDoAtivo, exposicaoAntesEmCentavos] of exposicoesAntesDoArquivoEmCentavos) {
    await levarExposicaoDoSimboloAte(request, simboloDoAtivo, exposicaoAntesEmCentavos);
  }
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-9: em ${larguraDaJanela} px os 3 links do Datadog ficam no topo, nesta ordem, com logo, rótulo, nome, seta e abrem em aba nova`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    const topo = page.getByRole('banner');
    await expect(topo.getByRole('link')).toHaveCount(3);

    let ladoDireitoDoLinkAnterior = 0;
    for (const painelEsperado of PAINEIS_DO_DATADOG_ESPERADOS) {
      const linkDoPainel = topo.getByRole('link', { name: new RegExp(painelEsperado.nomeDoPainel) });
      await expect(linkDoPainel).toHaveCount(1);
      await expect(linkDoPainel).toBeVisible();
      expect(await linkDoPainel.getAttribute('href')).toBe(painelEsperado.enderecoDoPainel);
      await expect(linkDoPainel).toHaveAttribute('target', '_blank');
      await expect(linkDoPainel).toHaveAttribute('rel', 'noopener noreferrer');
      // Quem usa leitor de tela ouve a marca, o painel e o aviso de que abre outra aba.
      await expect(linkDoPainel).toHaveAccessibleName(`Datadog ${painelEsperado.nomeDoPainel} (abre em nova aba)`);
      await expect(linkDoPainel).toHaveCSS('border-radius', '14px');
      expect(await lerMedidaDoCssEmPx(linkDoPainel, 'height')).toBeCloseTo(46, 1);
      await expect(linkDoPainel).toHaveCSS('background-color', COR_DO_FUNDO_DO_LINK);
      await expect(linkDoPainel).toHaveCSS('border-top-color', COR_DA_BORDA);

      const nomeDoPainel = linkDoPainel.locator('.painel-datadog-nome');
      await expect(nomeDoPainel).toHaveText(painelEsperado.nomeDoPainel);
      await expect(nomeDoPainel).toHaveCSS('font-size', '13px');
      await expect(nomeDoPainel).toHaveCSS('font-weight', '800');
      await expect(nomeDoPainel).toHaveCSS('color', COR_DO_TEXTO);
      expect(calcularContraste(await lerCorCalculada(nomeDoPainel, 'color'), COR_DO_FUNDO_DO_LINK)).toBeGreaterThanOrEqual(4.5);

      const marcaDoPainel = linkDoPainel.locator('.painel-datadog-marca');
      await expect(marcaDoPainel).toHaveText('DATADOG', { useInnerText: true });
      await expect(marcaDoPainel).toHaveCSS('font-size', '10px');
      await expect(marcaDoPainel).toHaveCSS('color', COR_DO_ROTULO);
      expect(calcularContraste(await lerCorCalculada(marcaDoPainel, 'color'), COR_DO_FUNDO_DO_LINK)).toBeGreaterThanOrEqual(4.5);

      const logoDoPainel = linkDoPainel.locator('.painel-datadog-logo');
      await expect(logoDoPainel).toHaveCSS('background-color', 'rgb(255, 255, 255)');
      expect(await lerMedidaDoCssEmPx(logoDoPainel, 'width')).toBeCloseTo(28, 1);
      expect(await lerMedidaDoCssEmPx(logoDoPainel, 'height')).toBeCloseTo(28, 1);
      await expect(logoDoPainel.locator('svg')).toBeVisible();
      await conferirDesenhoDoIcone(logoDoPainel.locator('svg'), 'LogoDatadog');
      await expect(linkDoPainel.locator('svg.painel-datadog-seta')).toBeVisible();
      await conferirDesenhoDoIcone(linkDoPainel.locator('svg.painel-datadog-seta'), 'IconeSetaParaFora');

      const caixaDoLink = await medirRetanguloNaTela(linkDoPainel);
      expect(caixaDoLink.x).toBeGreaterThanOrEqual(ladoDireitoDoLinkAnterior);
      ladoDireitoDoLinkAnterior = caixaDoLink.x + caixaDoLink.width;

      await linkDoPainel.hover();
      await expect(linkDoPainel).toHaveCSS('border-top-color', COR_DO_ACENTO);
      await page.mouse.move(0, 0);
      await expect(linkDoPainel).toHaveCSS('border-top-color', COR_DA_BORDA);
    }

    const caixaDoLogo = await medirRetanguloNaTela(page.getByRole('img', { name: 'Base investimentos' }));
    const caixaDoPrimeiroLink = await medirRetanguloNaTela(topo.getByRole('link', { name: /Four Golden Signals/ }));
    expect(caixaDoPrimeiroLink.x).toBeGreaterThan(caixaDoLogo.x + caixaDoLogo.width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
  });
}

test('CA-9: clicar num link do Datadog abre o painel em outra aba', async ({ page, context }) => {
  await page.goto('/');
  // A aba nova não chega a carregar o Datadog: o teste só confere qual endereço ela tentou abrir.
  await context.route(/datadoghq\.com/, (rotaDoDatadog) => rotaDoDatadog.fulfill({ status: 200, body: 'painel' }));
  const painelEsperado = PAINEIS_DO_DATADOG_ESPERADOS[1];
  const abaNova = context.waitForEvent('page');
  await page.getByRole('banner').getByRole('link', { name: new RegExp(painelEsperado.nomeDoPainel) }).click();
  const abaDoPainel = await abaNova;
  await abaDoPainel.waitForLoadState();
  expect(abaDoPainel.url()).toBe(painelEsperado.enderecoDoPainel);
  expect(page.url()).toMatch(/\/$/);
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-10: em ${larguraDaJanela} px, à direita dos links fica o selo com escudo verde, "AMBIENTE" e "Demonstração"`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    const selo = page.getByRole('banner').locator('.selo-ambiente');
    await expect(selo).toHaveCount(1);
    await expect(selo.locator('.selo-ambiente-rotulo')).toHaveText('AMBIENTE', { useInnerText: true });
    await expect(selo.locator('.selo-ambiente-nome')).toHaveText('Demonstração');
    await expect(selo.locator('.selo-ambiente-icone')).toHaveCSS('background-color', 'rgba(79, 227, 176, 0.13)');
    await expect(selo.locator('.selo-ambiente-icone svg')).toHaveCSS('color', COR_DO_ACENTO);
    await conferirDesenhoDoIcone(selo.locator('.selo-ambiente-icone svg'), 'IconeEscudo');
    const caixaDoSelo = await medirRetanguloNaTela(selo);
    const caixaDoUltimoLink = await medirRetanguloNaTela(page.getByRole('banner').getByRole('link', { name: /Ordens e exposição/ }));
    expect(caixaDoSelo.x).toBeGreaterThanOrEqual(caixaDoUltimoLink.x + caixaDoUltimoLink.width);
    expect(Math.abs(caixaDoSelo.y + caixaDoSelo.height / 2 - (caixaDoUltimoLink.y + caixaDoUltimoLink.height / 2))).toBeLessThanOrEqual(1);
  });
}

test('CA-43: abrir a tela não busca nada no Datadog nem arquivo de imagem do logo', async ({ page }) => {
  const enderecosPedidos: string[] = [];
  const imagensPedidas: string[] = [];
  page.on('request', (requisicaoDaTela) => {
    enderecosPedidos.push(requisicaoDaTela.url());
    if (requisicaoDaTela.resourceType() === 'image') imagensPedidas.push(requisicaoDaTela.url());
  });
  await page.goto('/', { waitUntil: 'networkidle' });
  await expect(page.getByRole('banner').getByRole('link')).toHaveCount(3);
  await expect(page.getByRole('banner').locator('.painel-datadog-logo svg')).toHaveCount(3);
  expect(enderecosPedidos.length).toBeGreaterThan(0);
  expect(enderecosPedidos.filter((enderecoPedido) => /datadog/i.test(enderecoPedido))).toEqual([]);
  expect(imagensPedidas).toEqual([]);
  const origemDaTela = new URL(page.url()).origin;
  expect(enderecosPedidos.filter((enderecoPedido) => new URL(enderecoPedido).origin !== origemDaTela)).toEqual([]);
});

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-5: em ${larguraDaJanela} px "Exposição por ativo" fica acima de tudo, com o limite à direita e 3 cartões lado a lado`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    const secaoDaExposicao = page.locator('section.exposicao');
    const tituloDaExposicao = secaoDaExposicao.getByRole('heading', { level: 2, name: 'Exposição por ativo' });
    await expect(tituloDaExposicao).toHaveCSS('font-family', /^Sora/);
    await expect(tituloDaExposicao).toHaveCSS('font-size', '18px');
    const textoDoLimite = secaoDaExposicao.getByText('Limite por ativo · R$ 100.000.000,00');
    await expect(textoDoLimite).toBeVisible();
    await expect(textoDoLimite).toHaveCSS('font-size', '13px');
    await expect(textoDoLimite).toHaveCSS('color', COR_DO_ROTULO);
    const caixaDoTitulo = await medirRetanguloNaTela(tituloDaExposicao);
    const caixaDoLimite = await medirRetanguloNaTela(textoDoLimite);
    expect(caixaDoLimite.x).toBeGreaterThan(caixaDoTitulo.x + caixaDoTitulo.width);
    expect(Math.abs(caixaDoLimite.y + caixaDoLimite.height / 2 - (caixaDoTitulo.y + caixaDoTitulo.height / 2))).toBeLessThan(caixaDoTitulo.height);

    // Acima de tudo: nenhum outro bloco da grade começa antes de a seção de exposição terminar.
    const blocosAbaixoDaExposicao = await page.locator('main').evaluate((grade) => {
      const fimDaExposicao = grade.querySelector('section.exposicao')!.getBoundingClientRect().bottom;
      return [...grade.children].filter((bloco) => !bloco.classList.contains('exposicao')).map((bloco) => bloco.getBoundingClientRect().top >= fimDaExposicao);
    });
    expect(blocosAbaixoDaExposicao.length).toBeGreaterThan(0);
    expect(blocosAbaixoDaExposicao.every((blocoEstaAbaixo) => blocoEstaAbaixo)).toBe(true);

    let ladoDireitoDoCartaoAnterior = 0;
    let topoDoPrimeiroCartao: number | undefined;
    for (const simboloDoAtivo of SIMBOLOS_DOS_CARTOES) {
      const cartao = localizarCartaoDoAtivo(page, simboloDoAtivo);
      await expect(cartao).toHaveCount(1);
      await expect(cartao).toBeVisible();
      await expect(cartao.getByRole('heading', { level: 3 })).toHaveText(simboloDoAtivo);
      await expect(cartao.locator('.exposicao-icone svg')).toBeVisible();
      await conferirDesenhoDoIcone(cartao.locator('.exposicao-icone svg'), 'IconeGraficoSubindo');
      expect(await lerMedidaDoCssEmPx(cartao.locator('.exposicao-icone'), 'width')).toBeCloseTo(34, 1);
      expect(await lerMedidaDoCssEmPx(cartao.locator('.exposicao-icone'), 'height')).toBeCloseTo(34, 1);
      await expect(cartao.locator('dt', { hasText: 'Exposição atual' })).toHaveCount(1);
      await expect(cartao.locator('dt', { hasText: 'Falta até o limite' })).toHaveCount(1);
      await expect(cartao.getByTestId('exposicao-atual')).toHaveCSS('font-family', /^Sora/);
      await expect(cartao.getByTestId('exposicao-atual')).toHaveCSS('font-size', '20px');
      await expect(cartao).toHaveCSS('border-radius', '20px');
      await expect(cartao).toHaveCSS('border-top-color', COR_DA_BORDA);
      await expect(cartao).toHaveCSS('background-image', 'linear-gradient(rgb(15, 31, 34), rgb(14, 27, 30))');
      await expect(cartao).toHaveCSS('box-shadow', 'rgba(0, 0, 0, 0.35) 0px 24px 50px 0px');
      await expect(cartao.getByText('Uso do limite', { exact: true })).toHaveCSS('font-size', '12px');
      await expect(cartao.getByTestId('uso-do-limite-porcentagem')).toHaveCSS('font-size', '12px');
      expect(await lerMedidaDoCssEmPx(cartao.getByRole('meter'), 'height')).toBeCloseTo(6, 1);

      const caixaDoCartao = await medirRetanguloNaTela(cartao);
      if (simboloDoAtivo !== SIMBOLOS_DOS_CARTOES[0]) expect(caixaDoCartao.x).toBeCloseTo(ladoDireitoDoCartaoAnterior + ESPACO_ENTRE_CARTOES_NA_TELA, 1);
      ladoDireitoDoCartaoAnterior = caixaDoCartao.x + caixaDoCartao.width;
      topoDoPrimeiroCartao ??= caixaDoCartao.y;
      expect(caixaDoCartao.y).toBeCloseTo(topoDoPrimeiroCartao, 0);

      const caixaDaExposicaoAtual = await medirRetanguloNaTela(cartao.getByTestId('exposicao-atual'));
      const caixaDaFalta = await medirRetanguloNaTela(cartao.getByTestId('exposicao-restante'));
      expect(caixaDaFalta.x).toBeGreaterThan(caixaDaExposicaoAtual.x);
      // Como na maquete: os dois rótulos começam na mesma altura e os dígitos dos dois valores assentam na mesma linha.
      const caixaDoRotuloDaExposicao = await medirRetanguloNaTela(cartao.locator('dt', { hasText: 'Exposição atual' }));
      const caixaDoRotuloDaFalta = await medirRetanguloNaTela(cartao.locator('dt', { hasText: 'Falta até o limite' }));
      expect(Math.abs(caixaDoRotuloDaFalta.y - caixaDoRotuloDaExposicao.y)).toBeLessThanOrEqual(0.5);
      const linhaDeBaseDaExposicao = await medirLinhaDeBaseNaTela(cartao.getByTestId('exposicao-atual'));
      const linhaDeBaseDaFalta = await medirLinhaDeBaseNaTela(cartao.getByTestId('exposicao-restante'));
      expect(Math.abs(linhaDeBaseDaFalta - linhaDeBaseDaExposicao)).toBeLessThanOrEqual(1);
    }

    // O texto do limite termina na mesma borda direita do último cartão, como na maquete.
    const caixaDoUltimoCartao = await medirRetanguloNaTela(localizarCartaoDoAtivo(page, 'VIIA4'));
    expect(caixaDoLimite.x + caixaDoLimite.width).toBeCloseTo(caixaDoUltimoCartao.x + caixaDoUltimoCartao.width, 0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
      await page.evaluate(() => document.documentElement.clientWidth),
    );
  });
}

test('CA-6 e CA-39: o uso do limite vem do servidor, corta sem arredondar, fica âmbar a partir de 90% e usa o valor sem sinal', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await levarExposicaoDoSimboloAte(request, 'VIIA4', 9_500_000_000);
  await levarExposicaoDoSimboloAte(request, 'VALE3', 0);
  await levarExposicaoDoSimboloAte(request, 'PETR4', -9_500_000_000);
  await page.goto('/');

  await conferirCartaoDoAtivo(page, 'VIIA4', {
    exposicaoAtual: 'R$ 95.000.000,00',
    faltaAteOLimite: 'R$ 5.000.000,00',
    usoDoLimite: '95%',
    barrinha: BARRINHA_AMBAR,
    valorDoMedidor: '95',
    larguraDaBarrinha: 0.95,
  });
  await conferirCartaoDoAtivo(page, 'VALE3', {
    exposicaoAtual: 'R$ 0,00',
    faltaAteOLimite: 'R$ 100.000.000,00',
    usoDoLimite: '0%',
    barrinha: BARRINHA_VERDE,
    valorDoMedidor: '0',
    larguraDaBarrinha: 0,
  });
  await conferirCartaoDoAtivo(page, 'PETR4', {
    exposicaoAtual: '-R$ 95.000.000,00',
    faltaAteOLimite: 'R$ 5.000.000,00',
    usoDoLimite: '95%',
    barrinha: BARRINHA_AMBAR,
    valorDoMedidor: '95',
    larguraDaBarrinha: 0.95,
  });

  // Compra pela própria boleta: a tela relê a exposição e o cartão muda junto, sem conta no cliente.
  // −95.000.000,00 + 99.999 × 950,00 = −950,00, que é mais que zero e menos que 0,01% do limite.
  await expect(page.getByLabel('Quantidade de PETR4')).toBeVisible();
  await page.getByLabel('Quantidade de PETR4').fill('99999');
  await page.getByLabel('Preço por ação (R$)').fill('950,00');
  const leituraDaExposicaoDepoisDoEnvio = page.waitForResponse(
    (respostaHttp) => respostaHttp.request().method() === 'GET' && new URL(respostaHttp.url()).pathname === ROTA_DAS_EXPOSICOES,
  );
  await page.getByRole('button', { name: 'Enviar ordem de compra' }).click();
  await leituraDaExposicaoDepoisDoEnvio;
  await conferirCartaoDoAtivo(page, 'PETR4', {
    exposicaoAtual: '-R$ 950,00',
    faltaAteOLimite: 'R$ 99.999.050,00',
    usoDoLimite: '< 0,01%',
    barrinha: BARRINHA_VERDE,
    valorDoMedidor: '0',
    larguraDaBarrinha: 'marca-minima',
  });
  expect(await lerExposicaoEmCentavosNoServidor(request, 'PETR4')).toBe(-95_000);
});

test('CA-6: na tela, 89,99% fica verde e 99,997…% aparece 99,99% âmbar, sem arredondar para 100%', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  // Valores em que cortar e arredondar dão resultados diferentes, um de cada lado do limiar de 90%.
  await levarExposicaoDoSimboloAte(request, 'VIIA4', 8_999_999_999);
  await levarExposicaoDoSimboloAte(request, 'VALE3', 9_999_799_701);
  await page.goto('/');

  await conferirCartaoDoAtivo(page, 'VIIA4', {
    exposicaoAtual: 'R$ 89.999.999,99',
    faltaAteOLimite: 'R$ 10.000.000,01',
    usoDoLimite: '89,99%',
    barrinha: BARRINHA_VERDE,
    valorDoMedidor: '89.99',
    larguraDaBarrinha: 0.8999,
  });
  await conferirCartaoDoAtivo(page, 'VALE3', {
    exposicaoAtual: 'R$ 99.997.997,01',
    faltaAteOLimite: 'R$ 2.002,99',
    usoDoLimite: '99,99%',
    barrinha: BARRINHA_AMBAR,
    valorDoMedidor: '99.99',
    larguraDaBarrinha: 0.9999,
  });
});

for (const larguraDaJanela of [860, 375]) {
  test(`CA-26: em ${larguraDaJanela} px os cartões ficam um embaixo do outro e links e selo quebram linha sem cobrir o logo`, async ({ page, request }) => {
    // Os números mais compridos que cabem no limite, com e sem sinal.
    await levarExposicaoDoSimboloAte(request, 'PETR4', -9_999_799_701);
    await levarExposicaoDoSimboloAte(request, 'VIIA4', 9_999_799_701);
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    await expect(localizarCartaoDoAtivo(page, 'VIIA4')).toBeVisible();

    let fundoDoCartaoAnterior = 0;
    let esquerdaDoPrimeiroCartao: number | undefined;
    for (const simboloDoAtivo of SIMBOLOS_DOS_CARTOES) {
      const caixaDoCartao = await medirRetanguloNaTela(localizarCartaoDoAtivo(page, simboloDoAtivo));
      if (simboloDoAtivo !== SIMBOLOS_DOS_CARTOES[0]) expect(caixaDoCartao.y).toBeCloseTo(fundoDoCartaoAnterior + ESPACO_ENTRE_CARTOES_NA_TELA, 1);
      fundoDoCartaoAnterior = caixaDoCartao.y + caixaDoCartao.height;
      esquerdaDoPrimeiroCartao ??= caixaDoCartao.x;
      expect(caixaDoCartao.x).toBeCloseTo(esquerdaDoPrimeiroCartao, 0);
      // Nenhum valor quebra no meio do número: cada um ocupa uma linha só e cabe no cartão.
      for (const idDoValor of ['exposicao-atual', 'exposicao-restante']) {
        const valorDoCartao = localizarCartaoDoAtivo(page, simboloDoAtivo).getByTestId(idDoValor);
        const valorEmUmaLinha = await valorDoCartao.evaluate(
          (valorNaPagina) => valorNaPagina.getBoundingClientRect().height < parseFloat(getComputedStyle(valorNaPagina).fontSize) * 2,
        );
        expect(valorEmUmaLinha, `${simboloDoAtivo} / ${idDoValor}`).toBe(true);
        const caixaDoValor = await medirRetanguloNaTela(valorDoCartao);
        expect(caixaDoValor.x + caixaDoValor.width).toBeLessThanOrEqual(caixaDoCartao.x + caixaDoCartao.width);
      }
    }

    const caixaDoLogo = await medirRetanguloNaTela(page.getByRole('img', { name: 'Base investimentos' }));
    const larguraVisivel = await page.evaluate(() => document.documentElement.clientWidth);
    const itensDoTopo = [
      ...PAINEIS_DO_DATADOG_ESPERADOS.map((painelEsperado) => page.getByRole('banner').getByRole('link', { name: new RegExp(painelEsperado.nomeDoPainel) })),
      page.getByRole('banner').locator('.selo-ambiente'),
    ];
    for (const itemDoTopo of itensDoTopo) {
      await expect(itemDoTopo).toBeVisible();
      const caixaDoItem = await medirRetanguloNaTela(itemDoTopo);
      expect(retangulosSeCruzam(caixaDoItem, caixaDoLogo)).toBe(false);
      expect(caixaDoItem.x).toBeGreaterThanOrEqual(0);
      expect(caixaDoItem.x + caixaDoItem.width).toBeLessThanOrEqual(larguraVisivel);
    }
    for (const [indiceDoItem, itemDoTopo] of itensDoTopo.entries()) {
      for (const outroItemDoTopo of itensDoTopo.slice(indiceDoItem + 1)) {
        expect(retangulosSeCruzam(await medirRetanguloNaTela(itemDoTopo), await medirRetanguloNaTela(outroItemDoTopo))).toBe(false);
      }
    }

    const larguraDoConteudo = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(larguraDoConteudo).toBeLessThanOrEqual(larguraVisivel);
  });
}

for (const larguraDaJanela of [1440, 1920]) {
  test(`CA-27: em ${larguraDaJanela} px cada link do Datadog recebe foco pelo teclado com contorno visível`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    for (const painelEsperado of PAINEIS_DO_DATADOG_ESPERADOS) {
      const linkDoPainel = page.getByRole('banner').getByRole('link', { name: new RegExp(painelEsperado.nomeDoPainel) });
      await page.keyboard.press('Tab');
      await expect(linkDoPainel).toBeFocused();
      await expect(linkDoPainel).toHaveCSS('outline-style', 'solid');
      // O contorno tem de aparecer com pelo menos 2 px na tela, já com os 90%.
      const contornoNaTelaEmPx = (await lerMedidaDoCssEmPx(linkDoPainel, 'outline-width')) * (await lerEscalaDaPagina(page));
      expect(Math.round(contornoNaTelaEmPx * 100) / 100).toBeGreaterThanOrEqual(2);
      await expect(linkDoPainel).toHaveCSS('outline-color', COR_DO_ACENTO);
    }
  });
}

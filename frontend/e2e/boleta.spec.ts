import { expect, test, type Locator, type Page } from '@playwright/test';
import { ROTA_DE_CRIACAO_DE_ORDEM } from '../src/ordensService';

// Cores do BRIEF (CA-23, CA-24) como o navegador devolve no estilo calculado.
const COR_DO_TRILHO = 'rgb(11, 23, 25)';
const COR_DA_BORDA_DO_TRILHO = 'rgb(30, 50, 54)';
const COR_DO_SIMBOLO_ESCOLHIDO = 'rgb(232, 239, 238)';
const COR_DO_TEXTO_DO_SIMBOLO_ESCOLHIDO = 'rgb(8, 17, 19)';
const COR_DO_ACENTO = 'rgb(79, 227, 176)';
const COR_DE_DESTAQUE_DO_PASSO = 'rgb(18, 36, 39)';
const BRILHO_DA_OPCAO_COMPRA = 'rgba(79, 227, 176, 0.35) 0px 0px 18px 0px';
const BRILHO_DA_OPCAO_VENDA = 'rgba(255, 164, 151, 0.32) 0px 0px 18px 0px';
const BRILHO_DO_ENVIAR_COMPRA = 'rgba(79, 227, 176, 0.45) 0px 10px 28px -6px';
const BRILHO_DO_ENVIAR_VENDA = 'rgba(255, 164, 151, 0.42) 0px 10px 28px -6px';
const ANEL_VERDE_DO_FOCO = 'rgba(79, 227, 176, 0.22) 0px 0px 0px 3px';
// Desenho do IconeEscudo (Icones.tsx): prende a nota ao escudo e não a qualquer ícone.
const DESENHO_DO_ESCUDO = 'M12 3l7 3v5c0 4.5-3 8.3-7 10-4-1.7-7-5.5-7-10V6l7-3z';
const SIMBOLOS_NA_ORDEM = ['PETR4', 'VALE3', 'VIIA4'];

function localizarBoletaDaPagina(paginaDaBoleta: Page) {
  return paginaDaBoleta.getByRole('form', { name: 'Boleta de ordem' });
}

function localizarTrilhoDeSimbolos(paginaDaBoleta: Page) {
  return localizarBoletaDaPagina(paginaDaBoleta).getByRole('group', { name: 'Símbolo' });
}

function localizarBotaoDoSimbolo(paginaDaBoleta: Page, simboloDaBoleta: string) {
  return localizarTrilhoDeSimbolos(paginaDaBoleta).getByRole('button', { name: simboloDaBoleta, exact: true });
}

async function lerEstiloCalculado(alvoDaPagina: Locator, propriedadesDoEstilo: string[]) {
  return alvoDaPagina.evaluate((elementoNaPagina, nomesDasPropriedades) => {
    const estiloDoElemento = getComputedStyle(elementoNaPagina);
    return Object.fromEntries(nomesDasPropriedades.map((nomeDaPropriedade) => [nomeDaPropriedade, estiloDoElemento.getPropertyValue(nomeDaPropriedade)]));
  }, propriedadesDoEstilo);
}

// Com a página em 90% o navegador arredonda bordas finas para o pixel da tela: 1px de CSS volta como 1,11px.
function lerNumeroDaMedidaEmPixels(valorEmPixels: string) {
  return parseFloat(valorEmPixels);
}

// Razão de contraste da WCAG 2 entre duas cores "rgb(r, g, b)".
function calcularContrasteWcag(corDoTexto: string, corDoFundo: string) {
  const calcularLuminanciaDaCor = (corRgb: string) => {
    const [canalVermelho, canalVerde, canalAzul] = (corRgb.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((canalDe0a255) => {
      const canalDe0a1 = canalDe0a255 / 255;
      return canalDe0a1 <= 0.03928 ? canalDe0a1 / 12.92 : ((canalDe0a1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * canalVermelho + 0.7152 * canalVerde + 0.0722 * canalAzul;
  };
  const [luminanciaMaior, luminanciaMenor] = [calcularLuminanciaDaCor(corDoTexto), calcularLuminanciaDaCor(corDoFundo)].sort((luminanciaDaPrimeira, luminanciaDaSegunda) => luminanciaDaSegunda - luminanciaDaPrimeira);
  return (luminanciaMaior + 0.05) / (luminanciaMenor + 0.05);
}

async function esperarOBotaoDoSimboloEscolhido(paginaDaBoleta: Page, simboloEscolhido: string) {
  for (const simboloDaBoleta of SIMBOLOS_NA_ORDEM) {
    await expect(localizarBotaoDoSimbolo(paginaDaBoleta, simboloDaBoleta), simboloDaBoleta).toHaveAttribute('aria-pressed', String(simboloDaBoleta === simboloEscolhido));
  }
  await expect(localizarBoletaDaPagina(paginaDaBoleta).getByLabel(/^Quantidade de/)).toHaveAccessibleName(`Quantidade de ${simboloEscolhido}`);
}

test.beforeEach(async ({ page }) => {
  await page.goto('/');
});

test('CA-23: o símbolo são 3 botões lado a lado, PETR4, VALE3 e VIIA4, sem lista suspensa', async ({ page }) => {
  await expect(localizarBoletaDaPagina(page).locator('select')).toHaveCount(0);
  await expect(localizarBoletaDaPagina(page).getByRole('combobox')).toHaveCount(0);
  await expect(localizarTrilhoDeSimbolos(page).getByRole('button')).toHaveText(SIMBOLOS_NA_ORDEM);
  const topoDeCadaBotao = [];
  for (const simboloDaBoleta of SIMBOLOS_NA_ORDEM) {
    await expect(localizarBotaoDoSimbolo(page, simboloDaBoleta)).toBeVisible();
    topoDeCadaBotao.push((await localizarBotaoDoSimbolo(page, simboloDaBoleta).boundingBox())!.y);
  }
  expect(new Set(topoDeCadaBotao).size).toBe(1);
  await esperarOBotaoDoSimboloEscolhido(page, 'PETR4');
});

test('CA-23: o trilho é escuro e o símbolo escolhido fica claro, com os valores do BRIEF', async ({ page }) => {
  const estiloDoTrilho = await lerEstiloCalculado(localizarTrilhoDeSimbolos(page), ['background-color', 'border-top-color', 'border-top-width', 'border-top-left-radius', 'padding-top', 'padding-left']);
  expect(estiloDoTrilho).toMatchObject({
    'background-color': COR_DO_TRILHO,
    'border-top-color': COR_DA_BORDA_DO_TRILHO,
    'border-top-left-radius': '16px',
    'padding-top': '6px',
    'padding-left': '6px',
  });
  expect(lerNumeroDaMedidaEmPixels(estiloDoTrilho['border-top-width'])).toBeCloseTo(1, 0);
  await localizarBotaoDoSimbolo(page, 'VIIA4').click();
  await esperarOBotaoDoSimboloEscolhido(page, 'VIIA4');
  await expect.poll(() => lerEstiloCalculado(localizarBotaoDoSimbolo(page, 'VIIA4'), ['background-color', 'color', 'border-top-left-radius'])).toEqual({
    'background-color': COR_DO_SIMBOLO_ESCOLHIDO,
    color: COR_DO_TEXTO_DO_SIMBOLO_ESCOLHIDO,
    'border-top-left-radius': '11px',
  });
  await expect.poll(() => lerEstiloCalculado(localizarBotaoDoSimbolo(page, 'PETR4'), ['background-color'])).toEqual({ 'background-color': 'rgba(0, 0, 0, 0)' });
});

test('CA-23: clicar em VIIA4 troca o rótulo e a ordem sai com VIIA4', async ({ page }) => {
  await localizarBotaoDoSimbolo(page, 'VIIA4').click();
  await esperarOBotaoDoSimboloEscolhido(page, 'VIIA4');
  await localizarBoletaDaPagina(page).getByLabel(/^Quantidade de/).fill('1');
  await localizarBoletaDaPagina(page).getByLabel('Preço por ação (R$)').fill('1,00');
  const requisicaoDaCriacaoDaOrdem = page.waitForRequest((requisicaoHttp) => requisicaoHttp.method() === 'POST' && new URL(requisicaoHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  const respostaDaCriacaoDaOrdem = page.waitForResponse((respostaHttp) => respostaHttp.request().method() === 'POST' && new URL(respostaHttp.url()).pathname === ROTA_DE_CRIACAO_DE_ORDEM);
  await localizarBoletaDaPagina(page).getByRole('button', { name: 'Enviar ordem de compra' }).click();
  expect((await requisicaoDaCriacaoDaOrdem).postDataJSON()).toEqual({ symbol: 'VIIA4', side: 'buy', quantity: 1, price: 1 });
  const respostaDoServidor = await respostaDaCriacaoDaOrdem;
  expect(respostaDoServidor.status()).toBe(200);
  expect((await respostaDoServidor.json()).symbol).toBe('VIIA4');
});

test('CA-23/CA-27: pelo teclado, Tab chega nos símbolos, Enter e Espaço escolhem, e o foco aparece', async ({ page }) => {
  await localizarBoletaDaPagina(page).getByRole('button', { name: 'Venda', exact: true }).focus();
  for (const simboloDaBoleta of SIMBOLOS_NA_ORDEM) {
    await page.keyboard.press('Tab');
    await expect(localizarBotaoDoSimbolo(page, simboloDaBoleta)).toBeFocused();
    const contornoDoFoco = await lerEstiloCalculado(localizarBotaoDoSimbolo(page, simboloDaBoleta), ['outline-style', 'outline-color', 'outline-width']);
    expect(contornoDoFoco, simboloDaBoleta).toMatchObject({ 'outline-style': 'solid', 'outline-color': COR_DO_ACENTO });
    expect(lerNumeroDaMedidaEmPixels(contornoDoFoco['outline-width']), simboloDaBoleta).toBeGreaterThanOrEqual(1);
  }
  await page.keyboard.press('Enter');
  await esperarOBotaoDoSimboloEscolhido(page, 'VIIA4');
  await page.keyboard.press('Shift+Tab');
  await expect(localizarBotaoDoSimbolo(page, 'VALE3')).toBeFocused();
  await page.keyboard.press(' ');
  await esperarOBotaoDoSimboloEscolhido(page, 'VALE3');
});

test('CA-27 (RNF-08a): pelo teclado, o foco do − e do + aparece dentro do botão, com fundo de destaque', async ({ page }) => {
  const passosDaQuantidade = [
    localizarBoletaDaPagina(page).getByRole('button', { name: 'Diminuir quantidade' }),
    localizarBoletaDaPagina(page).getByRole('button', { name: 'Aumentar quantidade' }),
  ];
  await page.mouse.move(0, 0);
  await localizarBotaoDoSimbolo(page, 'VIIA4').focus();
  await page.keyboard.press('Tab');
  await expect(passosDaQuantidade[0]).toBeFocused();
  for (const [indiceDoPasso, passoComFoco] of passosDaQuantidade.entries()) {
    if (indiceDoPasso === 1) {
      await page.keyboard.press('Tab');
      await expect(localizarBoletaDaPagina(page).getByLabel(/^Quantidade de/)).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(passoComFoco).toBeFocused();
    }
    const passoSemFoco = passosDaQuantidade[1 - indiceDoPasso];
    await expect.poll(() => lerEstiloCalculado(passoComFoco, ['background-color', 'outline-style', 'outline-color'])).toEqual({
      'background-color': COR_DE_DESTAQUE_DO_PASSO,
      'outline-style': 'solid',
      'outline-color': COR_DO_ACENTO,
    });
    const contornoDoPasso = await lerEstiloCalculado(passoComFoco, ['outline-offset', 'outline-width']);
    // Recuo para dentro maior que a espessura: o contorno inteiro cabe no botão e a caixa não o corta.
    expect(lerNumeroDaMedidaEmPixels(contornoDoPasso['outline-offset'])).toBeLessThanOrEqual(-lerNumeroDaMedidaEmPixels(contornoDoPasso['outline-width']));
    await expect.poll(() => lerEstiloCalculado(passoSemFoco, ['background-color'])).toEqual({ 'background-color': COR_DO_TRILHO });
  }
});

test('CA-27: o texto dos botões de símbolo tem contraste de pelo menos 4,5:1, escolhido ou não', async ({ page }) => {
  const corDoTrilho = (await lerEstiloCalculado(localizarTrilhoDeSimbolos(page), ['background-color']))['background-color'];
  for (const simboloDaBoleta of SIMBOLOS_NA_ORDEM) {
    await localizarBotaoDoSimbolo(page, simboloDaBoleta).click();
    await esperarOBotaoDoSimboloEscolhido(page, simboloDaBoleta);
    await page.mouse.move(0, 0);
    await expect.poll(() => lerEstiloCalculado(localizarBotaoDoSimbolo(page, simboloDaBoleta), ['background-color'])).toEqual({ 'background-color': COR_DO_SIMBOLO_ESCOLHIDO });
    for (const simboloNaoEscolhido of SIMBOLOS_NA_ORDEM.filter((simboloDaLista) => simboloDaLista !== simboloDaBoleta)) {
      await expect.poll(() => lerEstiloCalculado(localizarBotaoDoSimbolo(page, simboloNaoEscolhido), ['background-color'])).toEqual({ 'background-color': 'rgba(0, 0, 0, 0)' });
    }
    for (const simboloComparado of SIMBOLOS_NA_ORDEM) {
      const estiloDoBotao = await lerEstiloCalculado(localizarBotaoDoSimbolo(page, simboloComparado), ['color', 'background-color']);
      const corDoFundoDoBotao = estiloDoBotao['background-color'] === 'rgba(0, 0, 0, 0)' ? corDoTrilho : estiloDoBotao['background-color'];
      expect(calcularContrasteWcag(estiloDoBotao.color, corDoFundoDoBotao), `${simboloComparado} com ${simboloDaBoleta} escolhido`).toBeGreaterThanOrEqual(4.5);
    }
  }
});

test('CA-24: alternador Compra/Venda no trilho escuro de cantos 16px, com brilho verde na compra e coral na venda', async ({ page }) => {
  const alternadorDeLado = localizarBoletaDaPagina(page).getByRole('group', { name: 'Lado da ordem' });
  expect(await lerEstiloCalculado(alternadorDeLado, ['background-color', 'border-top-left-radius'])).toEqual({ 'background-color': COR_DO_TRILHO, 'border-top-left-radius': '16px' });
  await expect.poll(async () => (await lerEstiloCalculado(alternadorDeLado.getByRole('button', { name: 'Compra' }), ['box-shadow']))['box-shadow']).toBe(BRILHO_DA_OPCAO_COMPRA);
  await alternadorDeLado.getByRole('button', { name: 'Venda' }).click();
  await expect.poll(async () => (await lerEstiloCalculado(alternadorDeLado.getByRole('button', { name: 'Venda' }), ['box-shadow']))['box-shadow']).toBe(BRILHO_DA_OPCAO_VENDA);
  await expect.poll(async () => (await lerEstiloCalculado(alternadorDeLado.getByRole('button', { name: 'Compra' }), ['box-shadow']))['box-shadow']).toBe('none');
});

test('CA-24: rótulos em maiúsculas pequenas, campos com cantos 14px e anel verde no foco', async ({ page }) => {
  for (const rotuloDoCampo of [localizarBoletaDaPagina(page).getByText('Símbolo', { exact: true }), localizarBoletaDaPagina(page).getByText('Quantidade de PETR4', { exact: true }), localizarBoletaDaPagina(page).getByText('Preço por ação (R$)', { exact: true })]) {
    expect(await lerEstiloCalculado(rotuloDoCampo, ['text-transform', 'font-size'])).toEqual({ 'text-transform': 'uppercase', 'font-size': '12px' });
  }
  const campoDoPreco = localizarBoletaDaPagina(page).getByLabel('Preço por ação (R$)');
  const molduraDaQuantidade = localizarBoletaDaPagina(page).locator('.quantidade');
  expect((await lerEstiloCalculado(campoDoPreco, ['border-top-left-radius']))['border-top-left-radius']).toBe('14px');
  expect((await lerEstiloCalculado(molduraDaQuantidade, ['border-top-left-radius']))['border-top-left-radius']).toBe('14px');
  await campoDoPreco.focus();
  await expect.poll(async () => lerEstiloCalculado(campoDoPreco, ['border-top-color', 'box-shadow'])).toMatchObject({ 'border-top-color': COR_DO_ACENTO });
  await expect.poll(async () => (await lerEstiloCalculado(campoDoPreco, ['box-shadow']))['box-shadow']).toBe(ANEL_VERDE_DO_FOCO);
  await localizarBoletaDaPagina(page).getByLabel(/^Quantidade de/).focus();
  await expect.poll(async () => (await lerEstiloCalculado(molduraDaQuantidade, ['box-shadow']))['box-shadow']).toBe(ANEL_VERDE_DO_FOCO);
});

test('CA-24: quantidade em número grande Sora 22px e resumo numa caixa com o total em número grande', async ({ page }) => {
  const estiloDaQuantidade = await lerEstiloCalculado(localizarBoletaDaPagina(page).getByLabel(/^Quantidade de/), ['font-family', 'font-size']);
  expect(estiloDaQuantidade['font-family']).toMatch(/^"?Sora"?,/);
  expect(estiloDaQuantidade['font-size']).toBe('22px');
  await localizarBoletaDaPagina(page).getByLabel('Preço por ação (R$)').fill('100');
  const totalEstimado = page.getByTestId('total-estimado');
  await expect(totalEstimado).toHaveText('R$ 10.000,00');
  const estiloDoTotal = await lerEstiloCalculado(totalEstimado, ['font-family', 'font-size']);
  expect(estiloDoTotal['font-family']).toMatch(/^"?Sora"?,/);
  expect(estiloDoTotal['font-size']).toBe('22px');
  const caixaDoResumo = await totalEstimado.evaluate((totalNaPagina) => {
    const estiloDoResumo = getComputedStyle(totalNaPagina.closest('dl')!);
    return {
      fundo: estiloDoResumo.backgroundColor,
      corDaBorda: estiloDoResumo.borderTopColor,
      espessuraDaBorda: estiloDoResumo.borderTopWidth,
      canto: estiloDoResumo.borderTopLeftRadius,
      linhasDoResumo: Array.from(totalNaPagina.closest('dl')!.querySelectorAll('dt')).map((rotuloDoResumo) => rotuloDoResumo.textContent),
    };
  });
  expect(caixaDoResumo).toMatchObject({ fundo: COR_DO_TRILHO, corDaBorda: COR_DA_BORDA_DO_TRILHO, canto: '16px', linhasDoResumo: ['Preço por ação', 'Valor total estimado'] });
  expect(lerNumeroDaMedidaEmPixels(caixaDoResumo.espessuraDaBorda)).toBeCloseTo(1, 0);
});

test('CA-24: botão de enviar com 52px de altura, cantos 14px e brilho na cor do lado', async ({ page }) => {
  const botaoDeEnviar = localizarBoletaDaPagina(page).getByRole('button', { name: 'Enviar ordem de compra' });
  const estiloDoBotaoDeCompra = await lerEstiloCalculado(botaoDeEnviar, ['height', 'border-top-left-radius', 'box-shadow']);
  expect(lerNumeroDaMedidaEmPixels(estiloDoBotaoDeCompra.height)).toBeCloseTo(52, 1);
  expect(estiloDoBotaoDeCompra['border-top-left-radius']).toBe('14px');
  expect(estiloDoBotaoDeCompra['box-shadow']).toBe(BRILHO_DO_ENVIAR_COMPRA);
  await localizarBoletaDaPagina(page).getByRole('group', { name: 'Lado da ordem' }).getByRole('button', { name: 'Venda' }).click();
  await expect.poll(async () => (await lerEstiloCalculado(localizarBoletaDaPagina(page).getByRole('button', { name: 'Enviar ordem de venda' }), ['box-shadow']))['box-shadow']).toBe(BRILHO_DO_ENVIAR_VENDA);
});

test('CA-24/CA-25: a nota de demonstração fica numa caixa com o escudo e o texto de sempre', async ({ page }) => {
  const notaDeDemonstracao = localizarBoletaDaPagina(page).getByText('Ordem de demonstração: nenhuma operação real é feita.', { exact: true });
  await expect(notaDeDemonstracao).toHaveCount(1);
  await expect(notaDeDemonstracao.locator('svg[aria-hidden="true"]')).toHaveCount(1);
  await expect(notaDeDemonstracao.locator('svg')).toBeVisible();
  await expect(notaDeDemonstracao.locator('svg path')).toHaveAttribute('d', DESENHO_DO_ESCUDO);
  const estiloDaNota = await lerEstiloCalculado(notaDeDemonstracao, ['background-color', 'border-top-color', 'border-top-width', 'border-top-left-radius']);
  expect(estiloDaNota).toMatchObject({ 'background-color': COR_DO_TRILHO, 'border-top-color': COR_DA_BORDA_DO_TRILHO, 'border-top-left-radius': '14px' });
  expect(lerNumeroDaMedidaEmPixels(estiloDaNota['border-top-width'])).toBeCloseTo(1, 0);
});

for (const larguraDaJanela of [375, 860]) {
  test(`CA-26: em ${larguraDaJanela} px os 3 símbolos e a boleta cabem sem corte e sem rolagem para o lado`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(larguraDaJanela);
    const caixaDaBoleta = (await localizarBoletaDaPagina(page).boundingBox())!;
    expect(caixaDaBoleta.x).toBeGreaterThanOrEqual(0);
    expect(caixaDaBoleta.x + caixaDaBoleta.width).toBeLessThanOrEqual(larguraDaJanela);
    const topoDeCadaBotao = [];
    for (const simboloDaBoleta of SIMBOLOS_NA_ORDEM) {
      const botaoDoAtivo = localizarBotaoDoSimbolo(page, simboloDaBoleta);
      await expect(botaoDoAtivo).toBeVisible();
      const caixaDoBotao = (await botaoDoAtivo.boundingBox())!;
      topoDeCadaBotao.push(caixaDoBotao.y);
      expect(caixaDoBotao.x, `${simboloDaBoleta}: esquerda`).toBeGreaterThanOrEqual(caixaDaBoleta.x);
      expect(caixaDoBotao.x + caixaDoBotao.width, `${simboloDaBoleta}: direita`).toBeLessThanOrEqual(caixaDaBoleta.x + caixaDaBoleta.width);
      const textoCabeNoBotao = await botaoDoAtivo.evaluate((botaoNaPagina) => botaoNaPagina.scrollWidth <= botaoNaPagina.clientWidth);
      expect(textoCabeNoBotao, `${simboloDaBoleta}: texto inteiro`).toBe(true);
    }
    expect(new Set(topoDeCadaBotao).size).toBe(1);
  });
}

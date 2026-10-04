import { expect, test, type Locator, type Page } from '@playwright/test';

// Quebras da casca (CA-7 e CA-26): uma coluna até 860 px e duas a partir de 861 px.
// A fronteira é medida dos dois lados; 375, 1440 e 1920 são larguras de prova da rodada.
const LARGURAS_DO_TEMA = [
  { largura: 375, colunasEsperadas: 1 },
  { largura: 390, colunasEsperadas: 1 },
  { largura: 860, colunasEsperadas: 1 },
  { largura: 861, colunasEsperadas: 2 },
  { largura: 1280, colunasEsperadas: 2 },
  { largura: 1440, colunasEsperadas: 2 },
  { largura: 1920, colunasEsperadas: 2 },
];
const LARGURAS_DO_CA_23 = [390, 860, 1280];
const COR_DO_ACENTO = 'rgb(79, 227, 176)';
const COR_DO_TEXTO_APAGADO = 'rgb(157, 176, 174)';

// Razão de contraste da WCAG 2 entre duas cores "rgb(r, g, b)" (luminância relativa).
function calcularContrasteWcag(corDoTexto: string, corDoFundo: string) {
  const luminanciaDaCor = (corRgb: string) => {
    const [canalVermelho, canalVerde, canalAzul] = (corRgb.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number).map((canalDe0a255) => {
      const canalDe0a1 = canalDe0a255 / 255;
      return canalDe0a1 <= 0.03928 ? canalDe0a1 / 12.92 : ((canalDe0a1 + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * canalVermelho + 0.7152 * canalVerde + 0.0722 * canalAzul;
  };
  const [luminanciaMaior, luminanciaMenor] = [luminanciaDaCor(corDoTexto), luminanciaDaCor(corDoFundo)].sort((luminanciaDaPrimeira, luminanciaDaSegunda) => luminanciaDaSegunda - luminanciaDaPrimeira);
  return (luminanciaMaior + 0.05) / (luminanciaMenor + 0.05);
}

async function contarColunasDaGrade(paginaDaBoleta: Page) {
  return paginaDaBoleta.locator('.grade').evaluate((gradeDaPagina) => getComputedStyle(gradeDaPagina).gridTemplateColumns.split(' ').length);
}

function controlesDaBoleta(paginaDaBoleta: Page): Array<[string, Locator]> {
  return [
    ['Compra', paginaDaBoleta.getByRole('button', { name: 'Compra', exact: true })],
    ['Venda', paginaDaBoleta.getByRole('button', { name: 'Venda', exact: true })],
    ['Símbolo PETR4', paginaDaBoleta.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'PETR4', exact: true })],
    ['Símbolo VALE3', paginaDaBoleta.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'VALE3', exact: true })],
    ['Símbolo VIIA4', paginaDaBoleta.getByRole('group', { name: 'Símbolo' }).getByRole('button', { name: 'VIIA4', exact: true })],
    ['Diminuir quantidade', paginaDaBoleta.getByRole('button', { name: 'Diminuir quantidade' })],
    ['Quantidade', paginaDaBoleta.getByLabel(/^Quantidade de/)],
    ['Aumentar quantidade', paginaDaBoleta.getByRole('button', { name: 'Aumentar quantidade' })],
    ['Preço', paginaDaBoleta.getByLabel('Preço por ação (R$)')],
    ['Enviar', paginaDaBoleta.getByRole('button', { name: /^Enviar ordem/ })],
  ];
}

for (const { largura: larguraDaJanela, colunasEsperadas } of LARGURAS_DO_TEMA) {
  test(`RNF-03: em ${larguraDaJanela} px a página não rola para o lado e a grade tem ${colunasEsperadas} coluna(s)`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toBeVisible();
    const larguraDoConteudo = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(larguraDoConteudo).toBe(larguraDaJanela);
    expect(await contarColunasDaGrade(page)).toBe(colunasEsperadas);
  });
}

test('RF-01/RF-02: página única sem login, com logo, boleta, resposta e exposição, sem menu nem lista de ordens', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('img', { name: 'Base investimentos' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Boleta de ordens');
  await expect(page.getByRole('heading', { level: 2 })).toHaveText(['Exposição por ativo', 'Resposta da ordem', 'Nova ordem']);
  await expect(page.getByRole('form', { name: 'Boleta de ordem' })).toBeVisible();
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.getByRole('table')).toHaveCount(0);
  // Os links do Datadog moram no topo (CA-9, F4); dentro do conteúdo não há link.
  await expect(page.getByRole('main').getByRole('link')).toHaveCount(0);
});

for (const larguraDaJanela of [375, 860]) {
  test(`G-1 e CA-26: em ${larguraDaJanela} px a ordem empilhada é ativos, Nova ordem e resposta, sem nada fora da tela`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    await expect(page.getByTestId('exposicao-PETR4')).toBeVisible();
    const caixaDaExposicao = (await page.locator('section.exposicao').boundingBox())!;
    const caixaDaNovaOrdem = (await page.getByRole('form', { name: 'Boleta de ordem' }).boundingBox())!;
    const caixaDaResposta = (await page.locator('section.resposta').boundingBox())!;
    expect(caixaDaExposicao.y + caixaDaExposicao.height).toBeLessThanOrEqual(caixaDaNovaOrdem.y);
    expect(caixaDaNovaOrdem.y + caixaDaNovaOrdem.height).toBeLessThanOrEqual(caixaDaResposta.y);
    const caixasDosCartoes = [
      ['Exposição', caixaDaExposicao],
      ['Nova ordem', caixaDaNovaOrdem],
      ['Resposta', caixaDaResposta],
    ] as const;
    for (const [nomeDoCartao, caixaDoCartao] of caixasDosCartoes) {
      expect(caixaDoCartao.x, `${nomeDoCartao}: começa dentro da tela`).toBeGreaterThanOrEqual(0);
      expect(caixaDoCartao.x + caixaDoCartao.width, `${nomeDoCartao}: termina dentro da tela`).toBeLessThanOrEqual(larguraDaJanela);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(larguraDaJanela);

    // Nada cortado por dentro: os cartões escondem o que transborda (overflow: hidden), então cada
    // cartão não pode ter conteúdo mais largo ou mais alto que ele, e cada pedaço visível dentro dele
    // tem de caber no cartão e na janela.
    const conferenciaDosCartoes = await page.getByRole('main').evaluate((conteudoDaPagina, larguraDaTela) => {
      const problemasEncontrados: string[] = [];
      const pecasConferidasPorCartao: Record<string, number> = {};
      for (const cartaoDaPagina of conteudoDaPagina.querySelectorAll<HTMLElement>(':scope > .painel, :scope > .cartao')) {
        const nomeDoCartao = cartaoDaPagina.className;
        if (cartaoDaPagina.scrollWidth > cartaoDaPagina.clientWidth) problemasEncontrados.push(`${nomeDoCartao}: conteúdo mais largo que o cartão`);
        if (cartaoDaPagina.scrollHeight > cartaoDaPagina.clientHeight) problemasEncontrados.push(`${nomeDoCartao}: conteúdo mais alto que o cartão`);
        const caixaDoCartao = cartaoDaPagina.getBoundingClientRect();
        pecasConferidasPorCartao[nomeDoCartao] = 0;
        for (const pecaDoCartao of cartaoDaPagina.querySelectorAll<HTMLElement>('*')) {
          const caixaDaPeca = pecaDoCartao.getBoundingClientRect();
          if (caixaDaPeca.width === 0 || caixaDaPeca.height === 0 || pecaDoCartao.closest('.sr')) continue;
          pecasConferidasPorCartao[nomeDoCartao] += 1;
          const cabeNoCartao = caixaDaPeca.left >= caixaDoCartao.left - 0.5 && caixaDaPeca.right <= caixaDoCartao.right + 0.5 && caixaDaPeca.top >= caixaDoCartao.top - 0.5 && caixaDaPeca.bottom <= caixaDoCartao.bottom + 0.5;
          const cabeNaTela = caixaDaPeca.left >= -0.5 && caixaDaPeca.right <= larguraDaTela + 0.5;
          if (!cabeNoCartao || !cabeNaTela) problemasEncontrados.push(`${nomeDoCartao} > ${pecaDoCartao.tagName}.${pecaDoCartao.className}: fora do cartão ou da tela`);
        }
      }
      return { problemasEncontrados, pecasConferidasPorCartao };
    }, larguraDaJanela);
    expect(conferenciaDosCartoes.problemasEncontrados).toEqual([]);
    expect(Object.keys(conferenciaDosCartoes.pecasConferidasPorCartao)).toEqual(['painel exposicao', 'cartao resposta', 'cartao boleta']);

    // Cada peça obrigatória, pelo nome: visível, com tamanho e inteira dentro do seu cartão e da janela.
    // Peça que sumisse (largura 0) não pode passar só porque as outras do cartão continuam lá.
    const cartaoDaExposicao = page.locator('section.exposicao');
    const cartaoDaNovaOrdem = page.getByRole('form', { name: 'Boleta de ordem' });
    const cartaoDaResposta = page.locator('section.resposta');
    const pecasObrigatorias: Array<[string, Locator, Locator]> = [
      ['Título da exposição', cartaoDaExposicao.getByRole('heading', { name: 'Exposição por ativo' }), cartaoDaExposicao],
      ...['PETR4', 'VALE3', 'VIIA4'].flatMap((simboloDoAtivo): Array<[string, Locator, Locator]> => [
        [`${simboloDoAtivo}: exposição atual`, page.getByTestId(`exposicao-${simboloDoAtivo}`).getByTestId('exposicao-atual'), cartaoDaExposicao],
        [`${simboloDoAtivo}: falta até o limite`, page.getByTestId(`exposicao-${simboloDoAtivo}`).getByTestId('exposicao-restante'), cartaoDaExposicao],
      ]),
      ['Título da Nova ordem', cartaoDaNovaOrdem.getByRole('heading', { name: 'Nova ordem' }), cartaoDaNovaOrdem],
      ...controlesDaBoleta(page).map(([nomeDoControle, controleDaBoleta]): [string, Locator, Locator] => [nomeDoControle, controleDaBoleta, cartaoDaNovaOrdem]),
      ['Título da resposta', cartaoDaResposta.getByRole('heading', { name: 'Resposta da ordem' }), cartaoDaResposta],
      ['Aviso sem ordem', cartaoDaResposta.getByText('Nenhuma ordem enviada ainda.', { exact: false }), cartaoDaResposta],
    ];
    for (const [nomeDaPeca, pecaObrigatoria, cartaoDaPeca] of pecasObrigatorias) {
      await expect(pecaObrigatoria, nomeDaPeca).toHaveCount(1);
      await expect(pecaObrigatoria, nomeDaPeca).toBeVisible();
      const caixaDaPeca = (await pecaObrigatoria.boundingBox())!;
      const caixaDoCartaoDaPeca = (await cartaoDaPeca.boundingBox())!;
      expect(caixaDaPeca.width, `${nomeDaPeca}: largura`).toBeGreaterThan(0);
      expect(caixaDaPeca.height, `${nomeDaPeca}: altura`).toBeGreaterThan(0);
      expect(caixaDaPeca.x, `${nomeDaPeca}: começa dentro do cartão`).toBeGreaterThanOrEqual(caixaDoCartaoDaPeca.x - 0.5);
      expect(caixaDaPeca.x + caixaDaPeca.width, `${nomeDaPeca}: termina dentro do cartão`).toBeLessThanOrEqual(caixaDoCartaoDaPeca.x + caixaDoCartaoDaPeca.width + 0.5);
      expect(caixaDaPeca.x + caixaDaPeca.width, `${nomeDaPeca}: termina dentro da tela`).toBeLessThanOrEqual(larguraDaJanela + 0.5);
    }
    // Sem fullPage: com o zoom de 90% o Playwright mede a página inteira sem a escala e o print sai mais largo.
    await page.screenshot({ path: test.info().outputPath(`ca-26-empilhado-${larguraDaJanela}.png`) });
  });
}

// A página abre em 90% (CA-2, decisão 7 do dono): a caixa na tela vale 0,9 do tamanho de CSS.
// A área de toque é conferida em px de CSS, o tamanho que o código controla (spec da F3, ASSUMI-03).
for (const larguraDaJanela of LARGURAS_DO_CA_23) {
  test(`RNF-05: em ${larguraDaJanela} px cada controle da boleta tem área de toque de pelo menos 44 px de CSS`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    const escalaDaPagina = Number(await page.evaluate(() => getComputedStyle(document.documentElement).zoom));
    expect(escalaDaPagina).toBe(0.9);
    for (const [nomeDoControle, controleDaBoleta] of controlesDaBoleta(page)) {
      await expect(controleDaBoleta, nomeDoControle).toHaveCount(1);
      const caixaDoControle = (await controleDaBoleta.boundingBox())!;
      // A caixa na tela vem arredondada ao 1/64 de px; a folga é esse arredondamento, levado para px de CSS.
      const folgaDoArredondamentoEmCss = 1 / 64 / escalaDaPagina;
      expect(caixaDoControle.height / escalaDaPagina + folgaDoArredondamentoEmCss, `${nomeDoControle}: altura`).toBeGreaterThanOrEqual(44);
      expect(caixaDoControle.width / escalaDaPagina + folgaDoArredondamentoEmCss, `${nomeDoControle}: largura`).toBeGreaterThanOrEqual(44);
    }
  });
}

test('RNF-05: o foco pelo teclado é visível em cada controle da boleta, na cor do acento', async ({ page }) => {
  await page.goto('/');
  for (const [nomeDoControle, controleDaBoleta] of controlesDaBoleta(page)) {
    await controleDaBoleta.focus();
    // A borda muda com transição de 180 ms; espera a cor assentar em vez de ler no meio do caminho.
    await expect
      .poll(
        () =>
          controleDaBoleta.evaluate((controleNaPagina) => {
            const estiloDoControle = getComputedStyle(controleNaPagina);
            const molduraDaQuantidade = controleNaPagina.closest('.quantidade');
            const corDoContornoDeFoco = estiloDoControle.outlineStyle === 'solid' ? estiloDoControle.outlineColor : '';
            const corDaBordaDeFoco = (molduraDaQuantidade ? getComputedStyle(molduraDaQuantidade) : estiloDoControle).borderTopColor;
            return [corDoContornoDeFoco, corDaBordaDeFoco];
          }),
        { message: nomeDoControle },
      )
      .toContain(COR_DO_ACENTO);
  }
});

test('RNF-04: o texto de exemplo do preço usa a cor apagada do tema, com contraste de pelo menos 4,5:1', async ({ page }) => {
  await page.goto('/');
  const campoDoPreco = page.getByLabel('Preço por ação (R$)');
  const coresDoExemplo = await campoDoPreco.evaluate((campoNaPagina) => ({
    corDoTexto: getComputedStyle(campoNaPagina, '::placeholder').color,
    corDoFundo: getComputedStyle(campoNaPagina).backgroundColor,
  }));
  expect(coresDoExemplo.corDoTexto).toBe(COR_DO_TEXTO_APAGADO);
  expect(calcularContrasteWcag(coresDoExemplo.corDoTexto, coresDoExemplo.corDoFundo)).toBeGreaterThanOrEqual(4.5);
});

test('RNF-01: a página declara o esquema escuro, para seleção, rolagem e controles nativos seguirem o tema', async ({ page }) => {
  await page.goto('/');
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
});

for (const larguraDaJanela of [390, 1024, 1179, 1280]) {
  test(`RNF-03: em ${larguraDaJanela} px os rótulos do painel de exposição cabem numa linha`, async ({ page }) => {
    await page.setViewportSize({ width: larguraDaJanela, height: 900 });
    await page.goto('/');
    for (const simboloDaExposicao of ['PETR4', 'VALE3', 'VIIA4']) {
      for (const rotuloDaExposicao of ['Exposição atual', 'Falta até o limite']) {
        const rotuloDoSimbolo = page.getByTestId('exposicao-' + simboloDaExposicao).locator('dt', { hasText: rotuloDaExposicao });
        await expect(rotuloDoSimbolo).toHaveCount(1);
        // Uma linha mede menos que duas vezes o tamanho da letra; quebrado em duas, passa disso.
        const cabeEmUmaLinha = await rotuloDoSimbolo.evaluate(
          (rotuloNaPagina) => rotuloNaPagina.getBoundingClientRect().height < parseFloat(getComputedStyle(rotuloNaPagina).fontSize) * 2,
        );
        expect(cabeEmUmaLinha, `${simboloDaExposicao} / ${rotuloDaExposicao}`).toBe(true);
      }
    }
  });
}

test('RNF-02: as fontes Sora e Manrope do tema carregam e os números usam algarismos tabulares', async ({ page }) => {
  await page.goto('/');
  const fontesCarregadas = await page.evaluate(async () => {
    await document.fonts.ready;
    return [...document.fonts]
      .filter((fonteCarregada) => fonteCarregada.status === 'loaded')
      .map((fonteCarregada) => `${fonteCarregada.family.replaceAll('"', '')} ${fonteCarregada.weight}`);
  });
  expect(fontesCarregadas).toContain('Sora 600');
  expect(fontesCarregadas).toContain('Manrope 400');
  await expect(page.getByRole('heading', { name: 'Boleta de ordens' })).toHaveCSS('font-family', /^Sora/);
  await expect(page.locator('body')).toHaveCSS('font-family', /^Manrope/);
  await expect(page.getByTestId('total-estimado')).toHaveCSS('font-variant-numeric', 'tabular-nums');
});

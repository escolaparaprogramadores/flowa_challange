import { createElement, type ComponentType } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import {
  IconeAlerta,
  IconeDocumento,
  IconeEscudo,
  IconeGraficoSubindo,
  IconeLixeira,
  IconeSetaParaFora,
  LogoDatadog,
} from './Icones';

type IconeDoContrato = { nomeDoIcone: string; componenteDoIcone: ComponentType<{ className?: string }> };

// Cada ícone de traço com o seu desenho: trocar o desenho de um pelo de outro quebra o teste.
const ICONES_DE_TRACO: Array<IconeDoContrato & { desenhosEsperados: string[] }> = [
  { nomeDoIcone: 'IconeGraficoSubindo', componenteDoIcone: IconeGraficoSubindo, desenhosEsperados: ['M3 17l6-6 4 4 8-8', 'M15 7h6v6'] },
  { nomeDoIcone: 'IconeEscudo', componenteDoIcone: IconeEscudo, desenhosEsperados: ['M12 3l7 3v5c0 4.5-3 8.3-7 10-4-1.7-7-5.5-7-10V6l7-3z'] },
  { nomeDoIcone: 'IconeLixeira', componenteDoIcone: IconeLixeira, desenhosEsperados: ['M4 7h16', 'M10 11v6M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'] },
  {
    nomeDoIcone: 'IconeAlerta',
    componenteDoIcone: IconeAlerta,
    desenhosEsperados: ['M10.3 3.9L2.4 17.5a2 2 0 001.7 3h15.8a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', 'M12 9v4', 'M12 17h.01'],
  },
  {
    nomeDoIcone: 'IconeDocumento',
    componenteDoIcone: IconeDocumento,
    desenhosEsperados: ['M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8l-5-5z', 'M14 3v5h5', 'M9 13h6M9 17h6'],
  },
  { nomeDoIcone: 'IconeSetaParaFora', componenteDoIcone: IconeSetaParaFora, desenhosEsperados: ['M7 17L17 7', 'M8 7h9v9'] },
];

const ICONES_DO_CONTRATO: IconeDoContrato[] = [...ICONES_DE_TRACO, { nomeDoIcone: 'LogoDatadog', componenteDoIcone: LogoDatadog }];

function lerDesenhosDoIcone(desenhoDoIcone: string) {
  return [...desenhoDoIcone.matchAll(/<path d="([^"]+)"/g)].map(([, caminhoDoDesenho]) => caminhoDoDesenho);
}

describe('CA-40: ícones da tela são SVG escritos no código', () => {
  it.each(ICONES_DO_CONTRATO)('$nomeDoIcone desenha um SVG escondido da leitura de tela, sem buscar nada fora', ({ componenteDoIcone }) => {
    const desenhoDoIcone = renderToStaticMarkup(createElement(componenteDoIcone, { className: 'icone-de-teste' }));
    expect(desenhoDoIcone).toMatch(/^<svg [^>]*class="icone-de-teste"[^>]*>.*<\/svg>$/);
    expect(desenhoDoIcone).toContain('viewBox="0 0 24 24"');
    expect(desenhoDoIcone).toContain('aria-hidden="true"');
    expect(desenhoDoIcone).toContain('focusable="false"');
    expect(desenhoDoIcone).not.toMatch(/<image|href=|url\(|https?:/);
  });

  it.each(ICONES_DE_TRACO)('$nomeDoIcone é de traço na cor de quem usa e tem o seu próprio desenho', ({ componenteDoIcone, desenhosEsperados }) => {
    const desenhoDoIcone = renderToStaticMarkup(createElement(componenteDoIcone));
    expect(desenhoDoIcone).toContain('stroke="currentColor"');
    expect(desenhoDoIcone).toContain('fill="none"');
    expect(lerDesenhosDoIcone(desenhoDoIcone)).toEqual(desenhosEsperados);
  });

  it('LogoDatadog é a marca oficial preenchida com a cor de quem usa', () => {
    const desenhoDaMarca = renderToStaticMarkup(createElement(LogoDatadog));
    expect(desenhoDaMarca).toContain('fill="currentColor"');
    // Início, fim e tamanho do desenho da marca no Simple Icons 16.34.0 (datadog.svg): confere que é ela, inteira.
    const [desenhoDoCaminho = ''] = lerDesenhosDoIcone(desenhoDaMarca);
    expect(lerDesenhosDoIcone(desenhoDaMarca)).toHaveLength(1);
    expect(desenhoDoCaminho.startsWith('M19.57 17.04l-1.997-1.316-1.665 2.782')).toBe(true);
    expect(desenhoDoCaminho.endsWith('-.044-.542-.455-.456-.146-.749')).toBe(true);
    expect(desenhoDoCaminho).toHaveLength(2887);
  });
});

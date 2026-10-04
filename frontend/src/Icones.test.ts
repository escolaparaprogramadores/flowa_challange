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

const ICONES_DO_CONTRATO: Array<[string, ComponentType<{ className?: string }>]> = [
  ['IconeGraficoSubindo', IconeGraficoSubindo],
  ['IconeEscudo', IconeEscudo],
  ['IconeLixeira', IconeLixeira],
  ['IconeAlerta', IconeAlerta],
  ['IconeDocumento', IconeDocumento],
  ['IconeSetaParaFora', IconeSetaParaFora],
  ['LogoDatadog', LogoDatadog],
];

describe('CA-40: ícones da tela são SVG escritos no código', () => {
  it.each(ICONES_DO_CONTRATO)('%s desenha um SVG escondido da leitura de tela, sem buscar nada fora', (_nomeDoIcone, componenteDoIcone) => {
    const desenhoDoIcone = renderToStaticMarkup(createElement(componenteDoIcone, { className: 'icone-de-teste' }));
    expect(desenhoDoIcone).toMatch(/^<svg [^>]*class="icone-de-teste"[^>]*>.*<\/svg>$/);
    expect(desenhoDoIcone).toContain('viewBox="0 0 24 24"');
    expect(desenhoDoIcone).toContain('aria-hidden="true"');
    expect(desenhoDoIcone).toContain('focusable="false"');
    expect(desenhoDoIcone).toMatch(/<path d="M[^"]+"/);
    expect(desenhoDoIcone).not.toMatch(/<image|href=|url\(|https?:/);
  });

  it('os ícones de traço herdam a cor de quem usa e não pintam o miolo', () => {
    for (const [nomeDoIcone, componenteDoIcone] of ICONES_DO_CONTRATO.filter(([nomeDoIcone]) => nomeDoIcone !== 'LogoDatadog')) {
      const desenhoDoIcone = renderToStaticMarkup(createElement(componenteDoIcone));
      expect(desenhoDoIcone, nomeDoIcone).toContain('stroke="currentColor"');
      expect(desenhoDoIcone, nomeDoIcone).toContain('fill="none"');
    }
  });

  it('LogoDatadog é a marca oficial preenchida com a cor de quem usa', () => {
    const desenhoDaMarca = renderToStaticMarkup(createElement(LogoDatadog));
    expect(desenhoDaMarca).toContain('fill="currentColor"');
    // Início, fim e tamanho do desenho da marca no Simple Icons 16.34.0 (datadog.svg): confere que é ela, inteira.
    const desenhoDoCaminho = desenhoDaMarca.match(/<path d="([^"]+)"/)?.[1] ?? '';
    expect(desenhoDoCaminho.startsWith('M19.57 17.04l-1.997-1.316-1.665 2.782')).toBe(true);
    expect(desenhoDoCaminho.endsWith('-.044-.542-.455-.456-.146-.749')).toBe(true);
    expect(desenhoDoCaminho).toHaveLength(2887);
  });
});

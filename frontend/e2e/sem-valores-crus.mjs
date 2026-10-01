// CA-23: cor, fonte, raio, espaço e tamanho de letra só podem ser escritos no :root do tema; fora dele, var(--...).
// Antes de varrer src/, a guarda prova que recusa cada forma crua conhecida (autoteste).
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const PASTA_DA_TELA = fileURLToPath(new URL('../src/', import.meta.url));
const PROPRIEDADES_DE_COR = /^(color|background(-color)?|border(-(top|right|bottom|left))?(-color)?|outline(-color)?|fill|stroke|(box|text)-shadow|caret-color|accent-color|text-decoration-color)$/;
const PALAVRAS_SEM_COR = new Set(['transparent', 'currentcolor', 'inherit', 'initial', 'unset', 'none', 'solid', 'dashed', 'dotted', 'double', 'inset']);
const PROPRIEDADES_DE_ESPACO_E_LETRA = /^(padding|margin|gap|row-gap|column-gap|font-size|letter-spacing|grid-template-columns)(-(top|right|bottom|left))?$/;
const JSX_COM_VALOR_CRU = /\b(color|background(Color)?|borderColor|fontFamily|font|fontSize|letterSpacing|borderRadius|boxShadow|padding\w*|margin\w*|gap|rowGap|columnGap)\s*:/;

function valorSemVariaveis(valorDaDeclaracao) {
  return valorDaDeclaracao.replace(/var\(--[\w-]+\)/g, '').trim();
}

// Devolve o motivo quando a declaração CSS "propriedade: valor" traz cor, fonte, raio, espaço ou letra escritos direto.
function motivoDoValorCruNoCss(propriedade, valorDaDeclaracao) {
  const valorSemTokens = valorSemVariaveis(valorDaDeclaracao);
  if (/#[0-9a-f]{3,8}\b|\b(rgb|hsl|hwb|lab|lch|oklab|oklch)a?\(/i.test(valorSemTokens)) return 'cor escrita direto';
  if (PROPRIEDADES_DE_COR.test(propriedade)) {
    const palavraDeCor = (valorSemTokens.match(/[a-z]+/gi) ?? []).find((palavra) => !PALAVRAS_SEM_COR.has(palavra.toLowerCase()) && !/^(px|em|rem|s|ms|deg)$/i.test(palavra));
    if (palavraDeCor) return `cor por nome (${palavraDeCor})`;
  }
  if ((propriedade === 'font-family' || propriedade === 'font') && valorSemTokens !== '' && valorSemTokens !== 'inherit') return 'fonte escrita direto';
  if (/^border(-(top|bottom)-(left|right))?-radius$/.test(propriedade) && valorSemTokens !== '' && valorSemTokens !== '0') return 'raio escrito direto';
  if (PROPRIEDADES_DE_ESPACO_E_LETRA.test(propriedade) && /(^|[\s(,-])\d*\.?\d+(px|rem|em|vw|vh|%)(?![\w-])/.test(valorSemTokens)) return 'espaço ou tamanho de letra escrito direto';
  return undefined;
}

function acharValoresCrus(nomeDoArquivo, conteudoDoArquivo) {
  const achadosDeValoresCrusDaTela = [];
  if (/\.tsx?$/.test(nomeDoArquivo)) {
    conteudoDoArquivo.split(/\r?\n/).forEach((linhaDoArquivo, indice) => {
      if (JSX_COM_VALOR_CRU.test(linhaDoArquivo)) achadosDeValoresCrusDaTela.push(`${nomeDoArquivo}:${indice + 1}: estilo com cor, fonte, raio, espaço ou letra no componente`);
      if (/#[0-9a-f]{3,8}\b|\brgba?\(/i.test(linhaDoArquivo)) achadosDeValoresCrusDaTela.push(`${nomeDoArquivo}:${indice + 1}: cor escrita direto no componente`);
    });
    return achadosDeValoresCrusDaTela;
  }
  let dentroDoRoot = false;
  conteudoDoArquivo.split(/\r?\n/).forEach((linhaDoArquivo, indice) => {
    if (/^:root\s*\{/.test(linhaDoArquivo)) dentroDoRoot = true;
    for (const [, propriedade, valorDaDeclaracao] of linhaDoArquivo.matchAll(/([a-z-]+)\s*:\s*([^;{}]+)/gi)) {
      if (dentroDoRoot && propriedade.startsWith('--')) continue;
      const motivoDoValorCruNaDeclaracaoCss = motivoDoValorCruNoCss(propriedade.toLowerCase(), valorDaDeclaracao);
      if (motivoDoValorCruNaDeclaracaoCss) achadosDeValoresCrusDaTela.push(`${nomeDoArquivo}:${indice + 1}: ${motivoDoValorCruNaDeclaracaoCss}: ${linhaDoArquivo.trim()}`);
    }
    if (dentroDoRoot && /^\}/.test(linhaDoArquivo)) dentroDoRoot = false;
  });
  return achadosDeValoresCrusDaTela;
}

const AMOSTRAS_QUE_DEVEM_SER_RECUSADAS = [
  ['amostra.css', '.x { color: #fff; }'],
  ['amostra.css', '.x { color: crimson; }'],
  ['amostra.css', '.x { background: white; }'],
  ['amostra.css', '.x { border: 1px solid red; }'],
  ['amostra.css', '.x { background-color: rgba(0, 0, 0, .5); }'],
  ['amostra.css', '.x { box-shadow: 0 0 4px black; }'],
  ['amostra.css', '.x { font-family: Arial; }'],
  ['amostra.css', '.x { font: 600 16px Sora; }'],
  ['amostra.css', '.x { border-radius: 12px; }'],
  ['amostra.css', '.x { padding: 18px var(--espaco-5); }'],
  ['amostra.css', '.x { font-size: 15px; }'],
  ['amostra.css', '.grade { grid-template-columns: 260px minmax(0, 1fr); }'],
  ['amostra.css', '.x { font-size: 0.95rem; }'],
  ['amostra.css', '.x { gap: 1.5em; }'],
  ['amostra.css', '.x { padding: 5% 0; }'],
  ['amostra.css', '.x { padding: 1px; }'],
  ['amostra.css', '.x { letter-spacing: -0.03em; }'],
  ['amostra.tsx', 'export const Bloco = () => <div style={{ padding: 18, fontSize: 15 }} />;'],
  ['amostra.tsx', 'export const Cartao = () => <div style={{ borderRadius: 4 }} />;'],
  ['amostra.tsx', "export const Aviso = () => <p style={{ color: 'red' }} />;"],
];
const AMOSTRAS_QUE_DEVEM_PASSAR = [
  ['amostra.css', ':root {\n  --cor-acento: #4fe3b0;\n}\n.x { color: var(--cor-acento); border: 1px solid var(--cor-borda); background: transparent; }'],
  ['amostra.css', '.x { font-family: inherit; border-radius: var(--raio-card); box-shadow: inset 0 0 0 1px var(--cor-borda); }'],
];

const falhasDoAutoteste = [
  ...AMOSTRAS_QUE_DEVEM_SER_RECUSADAS.filter(([nomeDaAmostra, conteudoDaAmostra]) => acharValoresCrus(nomeDaAmostra, conteudoDaAmostra).length === 0).map(([, conteudoDaAmostra]) => `não recusou: ${conteudoDaAmostra}`),
  ...AMOSTRAS_QUE_DEVEM_PASSAR.filter(([nomeDaAmostra, conteudoDaAmostra]) => acharValoresCrus(nomeDaAmostra, conteudoDaAmostra).length > 0).map(([, conteudoDaAmostra]) => `recusou por engano: ${conteudoDaAmostra}`),
];
if (falhasDoAutoteste.length > 0) {
  console.error(`Autoteste da guarda falhou:\n${falhasDoAutoteste.join('\n')}`);
  process.exit(1);
}

function listarArquivosDaTela(pastaComArquivosDaTela) {
  return readdirSync(pastaComArquivosDaTela, { withFileTypes: true }).flatMap((entradaDaPasta) =>
    entradaDaPasta.isDirectory() ? listarArquivosDaTela(join(pastaComArquivosDaTela, entradaDaPasta.name)) : [join(pastaComArquivosDaTela, entradaDaPasta.name)],
  );
}

const achadosNaTela = listarArquivosDaTela(PASTA_DA_TELA)
  .filter((arquivo) => /\.(tsx?|css)$/.test(arquivo) && !/\.test\.tsx?$/.test(arquivo))
  .flatMap((arquivo) => acharValoresCrus(arquivo, readFileSync(arquivo, 'utf8')));

if (achadosNaTela.length > 0) {
  console.error(`Valores crus fora do :root do tema (${achadosNaTela.length}):\n${achadosNaTela.join('\n')}`);
  process.exit(1);
}
console.log(`Autoteste: ${AMOSTRAS_QUE_DEVEM_SER_RECUSADAS.length} amostras cruas recusadas e ${AMOSTRAS_QUE_DEVEM_PASSAR.length} limpas aceitas.`);
console.log('Nenhuma cor, fonte, raio, espaço ou tamanho de letra escrito direto fora do :root do tema.');

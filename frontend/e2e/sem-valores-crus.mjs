// CA-23: cor, fonte e raio só podem ser escritos no bloco :root do tema; o resto usa var(--...).
// Antes de varrer src/, a guarda prova que recusa cada forma crua conhecida (autoteste).
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const PASTA_DA_TELA = fileURLToPath(new URL('../src/', import.meta.url));
const PROPRIEDADES_DE_COR = /^(color|background(-color)?|border(-(top|right|bottom|left))?(-color)?|outline(-color)?|fill|stroke|(box|text)-shadow|caret-color|accent-color|text-decoration-color)$/;
const PALAVRAS_SEM_COR = new Set(['transparent', 'currentcolor', 'inherit', 'initial', 'unset', 'none', 'solid', 'dashed', 'dotted', 'double', 'inset']);
const JSX_COM_VALOR_CRU = /\b(color|background(Color)?|borderColor|fontFamily|font|borderRadius|boxShadow)\s*:/;

function valorSemVariaveis(valor) {
  return valor.replace(/var\(--[\w-]+\)/g, '').trim();
}

// Devolve o motivo quando a declaração CSS "propriedade: valor" traz cor, fonte ou raio escritos direto.
function motivoDoValorCruNoCss(propriedade, valor) {
  const resto = valorSemVariaveis(valor);
  if (/#[0-9a-f]{3,8}\b|\b(rgb|hsl|hwb|lab|lch|oklab|oklch)a?\(/i.test(resto)) return 'cor escrita direto';
  if (PROPRIEDADES_DE_COR.test(propriedade)) {
    const palavraDeCor = (resto.match(/[a-z]+/gi) ?? []).find((palavra) => !PALAVRAS_SEM_COR.has(palavra.toLowerCase()) && !/^(px|em|rem|s|ms|deg)$/i.test(palavra));
    if (palavraDeCor) return `cor por nome (${palavraDeCor})`;
  }
  if ((propriedade === 'font-family' || propriedade === 'font') && resto !== '' && resto !== 'inherit') return 'fonte escrita direto';
  if (/^border(-(top|bottom)-(left|right))?-radius$/.test(propriedade) && resto !== '' && resto !== '0') return 'raio escrito direto';
  return undefined;
}

function acharValoresCrus(nomeDoArquivo, conteudo) {
  const achados = [];
  if (/\.tsx?$/.test(nomeDoArquivo)) {
    conteudo.split(/\r?\n/).forEach((linha, indice) => {
      if (JSX_COM_VALOR_CRU.test(linha)) achados.push(`${nomeDoArquivo}:${indice + 1}: estilo com cor, fonte ou raio no componente`);
      if (/#[0-9a-f]{3,8}\b|\brgba?\(/i.test(linha)) achados.push(`${nomeDoArquivo}:${indice + 1}: cor escrita direto no componente`);
    });
    return achados;
  }
  let dentroDoRoot = false;
  conteudo.split(/\r?\n/).forEach((linha, indice) => {
    if (/^:root\s*\{/.test(linha)) dentroDoRoot = true;
    for (const [, propriedade, valor] of linha.matchAll(/([a-z-]+)\s*:\s*([^;{}]+)/gi)) {
      if (dentroDoRoot && propriedade.startsWith('--')) continue;
      const motivo = motivoDoValorCruNoCss(propriedade.toLowerCase(), valor);
      if (motivo) achados.push(`${nomeDoArquivo}:${indice + 1}: ${motivo}: ${linha.trim()}`);
    }
    if (dentroDoRoot && /^\}/.test(linha)) dentroDoRoot = false;
  });
  return achados;
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
  ['amostra.tsx', 'export const Cartao = () => <div style={{ borderRadius: 4 }} />;'],
  ['amostra.tsx', "export const Aviso = () => <p style={{ color: 'red' }} />;"],
];
const AMOSTRAS_QUE_DEVEM_PASSAR = [
  ['amostra.css', ':root {\n  --cor-acento: #4fe3b0;\n}\n.x { color: var(--cor-acento); border: 1px solid var(--cor-borda); background: transparent; }'],
  ['amostra.css', '.x { font-family: inherit; border-radius: var(--raio-card); box-shadow: inset 0 0 0 1px var(--cor-borda); }'],
];

const falhasDoAutoteste = [
  ...AMOSTRAS_QUE_DEVEM_SER_RECUSADAS.filter(([nome, conteudo]) => acharValoresCrus(nome, conteudo).length === 0).map(([, conteudo]) => `não recusou: ${conteudo}`),
  ...AMOSTRAS_QUE_DEVEM_PASSAR.filter(([nome, conteudo]) => acharValoresCrus(nome, conteudo).length > 0).map(([, conteudo]) => `recusou por engano: ${conteudo}`),
];
if (falhasDoAutoteste.length > 0) {
  console.error(`Autoteste da guarda falhou:\n${falhasDoAutoteste.join('\n')}`);
  process.exit(1);
}

function listarArquivosDaTela(pasta) {
  return readdirSync(pasta, { withFileTypes: true }).flatMap((entrada) =>
    entrada.isDirectory() ? listarArquivosDaTela(join(pasta, entrada.name)) : [join(pasta, entrada.name)],
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
console.log('Nenhuma cor, fonte ou raio escrito direto fora do :root do tema.');

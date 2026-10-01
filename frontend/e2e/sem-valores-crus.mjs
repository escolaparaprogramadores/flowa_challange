// CA-23: cor, fonte e raio só podem ser escritos no bloco :root do tema; componentes usam var(--...).
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const PASTA_DA_TELA = new URL('../src/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const VALOR_CRU = /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(|font-family\s*:\s*(?!inherit\b)['"A-Za-z]|border-radius\s*:\s*\d|fontFamily|borderRadius|\bcolor\s*:\s*['"#]/;

function listarArquivosDaTela(pasta) {
  return readdirSync(pasta, { withFileTypes: true }).flatMap((entrada) =>
    entrada.isDirectory() ? listarArquivosDaTela(join(pasta, entrada.name)) : [join(pasta, entrada.name)],
  );
}

const achados = [];
for (const arquivo of listarArquivosDaTela(PASTA_DA_TELA)) {
  if (!/\.(tsx?|css)$/.test(arquivo)) continue;
  const linhas = readFileSync(arquivo, 'utf8').split(/\r?\n/);
  let dentroDoRoot = false;
  linhas.forEach((linha, indice) => {
    if (arquivo.endsWith('.css') && /^:root\s*\{/.test(linha)) dentroDoRoot = true;
    const valorPermitido = dentroDoRoot && /^\s*--[\w-]+\s*:/.test(linha);
    if (!valorPermitido && VALOR_CRU.test(linha.replace(/var\(--[\w-]+\)/g, ''))) achados.push(`${arquivo}:${indice + 1}: ${linha.trim()}`);
    if (dentroDoRoot && /^\}/.test(linha)) dentroDoRoot = false;
  });
}

if (achados.length > 0) {
  console.error(`Valores crus fora do :root do tema (${achados.length}):\n${achados.join('\n')}`);
  process.exit(1);
}
console.log('Nenhuma cor, fonte ou raio escrito direto fora do :root do tema.');

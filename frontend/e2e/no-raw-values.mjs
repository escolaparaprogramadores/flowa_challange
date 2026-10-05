// CA-23: color, font, radius, spacing and font size may only be written in the theme :root; outside it, var(--...).
// Before scanning src/, the guard proves it rejects every known raw form (self-test).
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const SCREEN_SOURCE_FOLDER = fileURLToPath(new URL('../src/', import.meta.url));
const COLOR_PROPERTIES = /^(color|background(-color)?|border(-(top|right|bottom|left))?(-color)?|outline(-color)?|fill|stroke|(box|text)-shadow|caret-color|accent-color|text-decoration-color)$/;
const NON_COLOR_KEYWORDS = new Set(['transparent', 'currentcolor', 'inherit', 'initial', 'unset', 'none', 'solid', 'dashed', 'dotted', 'double', 'inset']);
const SPACING_AND_FONT_SIZE_PROPERTIES = /^(padding|margin|gap|row-gap|column-gap|font-size|letter-spacing|grid-template-columns)(-(top|right|bottom|left))?$/;
const JSX_WITH_RAW_VALUE = /\b(color|background(Color)?|borderColor|fontFamily|font|fontSize|letterSpacing|borderRadius|boxShadow|padding\w*|margin\w*|gap|rowGap|columnGap)\s*:/;

function removeCssTokensFromDeclarationValue(declarationValue) {
  return declarationValue.replace(/var\(--[\w-]+\)/g, '').trim();
}

// Returns the reason when the CSS declaration "property: value" writes color, font, radius, spacing or font size directly.
function findRawCssValueReason(declarationProperty, declarationValue) {
  const declarationValueWithoutTokens = removeCssTokensFromDeclarationValue(declarationValue);
  if (/#[0-9a-f]{3,8}\b|\b(rgb|hsl|hwb|lab|lch|oklab|oklch)a?\(/i.test(declarationValueWithoutTokens)) return 'color written directly';
  if (COLOR_PROPERTIES.test(declarationProperty)) {
    const colorKeyword = (declarationValueWithoutTokens.match(/[a-z]+/gi) ?? []).find((valueWord) => !NON_COLOR_KEYWORDS.has(valueWord.toLowerCase()) && !/^(px|em|rem|s|ms|deg)$/i.test(valueWord));
    if (colorKeyword) return `color by name (${colorKeyword})`;
  }
  if ((declarationProperty === 'font-family' || declarationProperty === 'font') && declarationValueWithoutTokens !== '' && declarationValueWithoutTokens !== 'inherit') return 'font written directly';
  if (/^border(-(top|bottom)-(left|right))?-radius$/.test(declarationProperty) && declarationValueWithoutTokens !== '' && declarationValueWithoutTokens !== '0') return 'radius written directly';
  if (SPACING_AND_FONT_SIZE_PROPERTIES.test(declarationProperty) && /(^|[\s(,-])\d*\.?\d+(px|rem|em|vw|vh|%)(?![\w-])/.test(declarationValueWithoutTokens)) return 'spacing or font size written directly';
  return undefined;
}

function findRawValues(fileName, fileContent) {
  const rawValueFindings = [];
  if (/\.tsx?$/.test(fileName)) {
    fileContent.split(/\r?\n/).forEach((fileLine, lineIndex) => {
      if (JSX_WITH_RAW_VALUE.test(fileLine)) rawValueFindings.push(`${fileName}:${lineIndex + 1}: style with color, font, radius, spacing or font size in the component`);
      if (/#[0-9a-f]{3,8}\b|\brgba?\(/i.test(fileLine)) rawValueFindings.push(`${fileName}:${lineIndex + 1}: color written directly in the component`);
    });
    return rawValueFindings;
  }
  let isInsideRoot = false;
  fileContent.split(/\r?\n/).forEach((fileLine, lineIndex) => {
    if (/^:root\s*\{/.test(fileLine)) isInsideRoot = true;
    for (const [, declarationProperty, declarationValue] of fileLine.matchAll(/([a-z-]+)\s*:\s*([^;{}]+)/gi)) {
      if (isInsideRoot && declarationProperty.startsWith('--')) continue;
      const rawCssValueReason = findRawCssValueReason(declarationProperty.toLowerCase(), declarationValue);
      if (rawCssValueReason) rawValueFindings.push(`${fileName}:${lineIndex + 1}: ${rawCssValueReason}: ${fileLine.trim()}`);
    }
    if (isInsideRoot && /^\}/.test(fileLine)) isInsideRoot = false;
  });
  return rawValueFindings;
}

const SAMPLES_THAT_MUST_BE_REJECTED = [
  ['sample.css', '.x { color: #fff; }'],
  ['sample.css', '.x { color: crimson; }'],
  ['sample.css', '.x { background: white; }'],
  ['sample.css', '.x { border: 1px solid red; }'],
  ['sample.css', '.x { background-color: rgba(0, 0, 0, .5); }'],
  ['sample.css', '.x { box-shadow: 0 0 4px black; }'],
  ['sample.css', '.x { font-family: Arial; }'],
  ['sample.css', '.x { font: 600 16px Sora; }'],
  ['sample.css', '.x { border-radius: 12px; }'],
  ['sample.css', '.x { padding: 18px var(--space-5); }'],
  ['sample.css', '.x { font-size: 15px; }'],
  ['sample.css', '.layout-grid { grid-template-columns: 260px minmax(0, 1fr); }'],
  ['sample.css', '.x { font-size: 0.95rem; }'],
  ['sample.css', '.x { gap: 1.5em; }'],
  ['sample.css', '.x { padding: 5% 0; }'],
  ['sample.css', '.x { padding: 1px; }'],
  ['sample.css', '.x { letter-spacing: -0.03em; }'],
  ['sample.tsx', 'export const Block = () => <div style={{ padding: 18, fontSize: 15 }} />;'],
  ['sample.tsx', 'export const Card = () => <div style={{ borderRadius: 4 }} />;'],
  ['sample.tsx', "export const Notice = () => <p style={{ color: 'red' }} />;"],
];
const SAMPLES_THAT_MUST_PASS = [
  ['sample.css', ':root {\n  --color-accent: #4fe3b0;\n}\n.x { color: var(--color-accent); border: 1px solid var(--color-border); background: transparent; }'],
  ['sample.css', '.x { font-family: inherit; border-radius: var(--radius-card); box-shadow: inset 0 0 0 1px var(--color-border); }'],
];

const selfTestFailures = [
  ...SAMPLES_THAT_MUST_BE_REJECTED.filter(([sampleName, sampleContent]) => findRawValues(sampleName, sampleContent).length === 0).map(([, sampleContent]) => `not rejected: ${sampleContent}`),
  ...SAMPLES_THAT_MUST_PASS.filter(([sampleName, sampleContent]) => findRawValues(sampleName, sampleContent).length > 0).map(([, sampleContent]) => `wrongly rejected: ${sampleContent}`),
];
if (selfTestFailures.length > 0) {
  console.error(`Guard self-test failed:\n${selfTestFailures.join('\n')}`);
  process.exit(1);
}

function listScreenSourceFiles(screenSourceFolder) {
  return readdirSync(screenSourceFolder, { withFileTypes: true }).flatMap((folderEntry) =>
    folderEntry.isDirectory() ? listScreenSourceFiles(join(screenSourceFolder, folderEntry.name)) : [join(screenSourceFolder, folderEntry.name)],
  );
}

const screenRawValueFindings = listScreenSourceFiles(SCREEN_SOURCE_FOLDER)
  .filter((screenSourceFilePath) => /\.(tsx?|css)$/.test(screenSourceFilePath) && !/\.test\.tsx?$/.test(screenSourceFilePath))
  .flatMap((screenSourceFilePath) => findRawValues(screenSourceFilePath, readFileSync(screenSourceFilePath, 'utf8')));

if (screenRawValueFindings.length > 0) {
  console.error(`Raw values outside the theme :root (${screenRawValueFindings.length}):\n${screenRawValueFindings.join('\n')}`);
  process.exit(1);
}
console.log(`Self-test: ${SAMPLES_THAT_MUST_BE_REJECTED.length} raw samples rejected and ${SAMPLES_THAT_MUST_PASS.length} clean samples accepted.`);
console.log('No color, font, radius, spacing or font size written directly outside the theme :root.');

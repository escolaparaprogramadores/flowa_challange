import { createHash } from 'node:crypto';
import { createElement, type ComponentType } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import {
  AlertIcon,
  DatadogLogo,
  DocumentIcon,
  ExternalArrowIcon,
  RisingChartIcon,
  ShieldIcon,
  TrashIcon,
} from './Icons';

const DATADOG_MARK_PATH_HASH = 'd9c09ff2589ace4b691ba53ec21ee3bca5f45f8458c99d864e8d021cdb7564de';

type ContractIcon = { iconName: string; iconComponent: ComponentType<{ className?: string }> };

// Each stroke icon with its own drawing: swapping one drawing for another breaks the test.
const STROKE_ICONS: Array<ContractIcon & { expectedPaths: string[] }> = [
  { iconName: 'RisingChartIcon', iconComponent: RisingChartIcon, expectedPaths: ['M3 17l6-6 4 4 8-8', 'M15 7h6v6'] },
  { iconName: 'ShieldIcon', iconComponent: ShieldIcon, expectedPaths: ['M12 3l7 3v5c0 4.5-3 8.3-7 10-4-1.7-7-5.5-7-10V6l7-3z'] },
  { iconName: 'TrashIcon', iconComponent: TrashIcon, expectedPaths: ['M4 7h16', 'M10 11v6M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'] },
  {
    iconName: 'AlertIcon',
    iconComponent: AlertIcon,
    expectedPaths: ['M10.3 3.9L2.4 17.5a2 2 0 001.7 3h15.8a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z', 'M12 9v4', 'M12 17h.01'],
  },
  {
    iconName: 'DocumentIcon',
    iconComponent: DocumentIcon,
    expectedPaths: ['M14 3H7a2 2 0 00-2 2v14a2 2 0 002 2h10a2 2 0 002-2V8l-5-5z', 'M14 3v5h5', 'M9 13h6M9 17h6'],
  },
  { iconName: 'ExternalArrowIcon', iconComponent: ExternalArrowIcon, expectedPaths: ['M7 17L17 7', 'M8 7h9v9'] },
];

const CONTRACT_ICONS: ContractIcon[] = [...STROKE_ICONS, { iconName: 'DatadogLogo', iconComponent: DatadogLogo }];

function readIconPaths(iconMarkup: string) {
  return [...iconMarkup.matchAll(/<path d="([^"]+)"/g)].map(([, iconPath]) => iconPath);
}

describe('CA-40: screen icons are SVG written in code', () => {
  it.each(CONTRACT_ICONS)('$iconName draws an SVG hidden from screen readers, fetching nothing outside', ({ iconComponent }) => {
    const iconMarkup = renderToStaticMarkup(createElement(iconComponent, { className: 'test-icon' }));
    expect(iconMarkup).toMatch(/^<svg [^>]*class="test-icon"[^>]*>.*<\/svg>$/);
    expect(iconMarkup).toContain('viewBox="0 0 24 24"');
    expect(iconMarkup).toContain('aria-hidden="true"');
    expect(iconMarkup).toContain('focusable="false"');
    expect(iconMarkup).not.toMatch(/<image|href=|url\(|https?:/);
  });

  it.each(STROKE_ICONS)('$iconName is a stroke icon in the color of whoever uses it and has its own drawing', ({ iconComponent, expectedPaths }) => {
    const iconMarkup = renderToStaticMarkup(createElement(iconComponent));
    expect(iconMarkup).toContain('stroke="currentColor"');
    expect(iconMarkup).toContain('fill="none"');
    expect(readIconPaths(iconMarkup)).toEqual(expectedPaths);
  });

  it('DatadogLogo is the official mark filled with the color of whoever uses it', () => {
    const markMarkup = renderToStaticMarkup(createElement(DatadogLogo));
    expect(markMarkup).toContain('fill="currentColor"');
    // SHA-256 of the whole path of datadog.svg from Simple Icons 16.34.0: any number changed in the drawing breaks the test.
    const [markPath = ''] = readIconPaths(markMarkup);
    expect(readIconPaths(markMarkup)).toHaveLength(1);
    expect(markPath.startsWith('M19.57 17.04l-1.997-1.316-1.665 2.782')).toBe(true);
    expect(createHash('sha256').update(markPath).digest('hex')).toBe(DATADOG_MARK_PATH_HASH);
  });
});
